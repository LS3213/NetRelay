using System.IO;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading;
using NetRelay.Models;

namespace NetRelay.Services;

public sealed class PreNotificationEventArgs : EventArgs
{
    public AutomationRule Rule { get; }
    public PreNotification Notification { get; }
    public DateTimeOffset TargetTime { get; }
    public int MinutesRemaining { get; }

    public PreNotificationEventArgs(AutomationRule rule, PreNotification notification, DateTimeOffset targetTime, int minutesRemaining)
    {
        Rule = rule;
        Notification = notification;
        TargetTime = targetTime;
        MinutesRemaining = minutesRemaining;
    }
}

public sealed class RuleSchedulerService : IDisposable
{
    private readonly RuleEngine _ruleEngine;
    private readonly ConfigurationService _configService;
    private readonly ConnectivityService _connectivityService;
    private System.Threading.Timer? _timer;
    private readonly object _lock = new();
    private readonly NonReentrantGate _timerTickGate = new();
    private readonly NonReentrantGate _connectivityEvaluationGate = new();

    // Maximum tolerance (in minutes) to catch-up run a missed scheduled rule.
    private const int TriggerToleranceMinutes = 2;

    // Tracks daily/weekly rule execution dates to prevent multiple fires within the matching minute.
    private readonly Dictionary<Guid, DateTime> _timeTriggerLastRan = new();

    // Tracks once rule executed state in-memory.
    private readonly HashSet<Guid> _onceTriggerRan = new();

    // Tracks when pre-notifications were last triggered today. Key: (RuleId, MinutesBefore).
    private readonly Dictionary<(Guid RuleId, int MinutesBefore), DateTime> _preNotificationLastTriggeredDate = new();

    // Tracks if a Once rule's pre-notification has been triggered. Key: (RuleId, MinutesBefore).
    private readonly HashSet<(Guid RuleId, int MinutesBefore)> _oncePreNotificationsTriggered = new();

    // In-memory temporary delays for rules today. Key: RuleId.
    private readonly Dictionary<Guid, TimeSpan> _tempRuleDelays = new();

    // Debounce cancel tokens for network change rules. Key: Rule ID.
    private readonly Dictionary<Guid, CancellationTokenSource> _networkChangeDebouncers = new();

    // Last known operational status of adapters for edge trigger evaluation. Key: Adapter ID.
    private readonly Dictionary<string, OperationalStatus> _lastAdapterStatuses = new();

    // Last known internet state of adapters for HTTP-probed edge trigger evaluation. Key: Adapter ID.
    private readonly Dictionary<string, bool> _lastAdapterInternetStates = new(StringComparer.OrdinalIgnoreCase);

    // Events
    public event EventHandler<PreNotificationEventArgs>? PreNotificationTriggered;
    public event EventHandler<ExecutionRecord>? RuleExecuted;

    public RuleSchedulerService(
        RuleEngine ruleEngine,
        ConfigurationService configService,
        ConnectivityService connectivityService)
    {
        _ruleEngine = ruleEngine;
        _configService = configService;
        _connectivityService = connectivityService;
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_timer != null) return;

            InitializeLastRanFromLogs();
            UpdateAdapterStatuses();
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            _timer = new System.Threading.Timer(OnTimerTick, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        }
    }

    private void InitializeLastRanFromLogs()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var logDir = Path.Combine(appData, "NetRelay", "logs");
            if (!Directory.Exists(logDir)) return;

            var todayLogPath = Path.Combine(logDir, $"execution-{DateTime.Today:yyyy-MM-dd}.jsonl");
            if (!File.Exists(todayLogPath)) return;

            var scheduledTimeRuleIds = _configService.Current.Rules
                .Where(rule => rule.Trigger is RuleTrigger.Daily or RuleTrigger.Weekly)
                .Select(rule => rule.Id)
                .ToHashSet();
            var lines = File.ReadAllLines(todayLogPath);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var record = JsonSerializer.Deserialize<ExecutionRecord>(line);
                    if (record is not null
                        && RuleSchedulerPolicy.ShouldRestoreTimeRuleOccurrence(record, scheduledTimeRuleIds))
                    {
                        var ruleId = record.RuleId!.Value;
                        var ranDate = record.StartedAt.LocalDateTime.Date;

                        if (!_timeTriggerLastRan.TryGetValue(ruleId, out var existingDate) || existingDate < ranDate)
                        {
                            _timeTriggerLastRan[ruleId] = ranDate;
                        }
                    }
                }
                catch
                {
                    // 忽略单行日志解析错误以维持健壮性
                }
            }
        }
        catch
        {
            // 忽略读取文件异常
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;

            if (_timer != null)
            {
                _timer.Dispose();
                _timer = null;
            }

            foreach (var cts in _networkChangeDebouncers.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }
            _networkChangeDebouncers.Clear();
        }
    }

    public void Reload()
    {
        lock (_lock)
        {
            var ruleIds = _configService.Current.Rules.Select(r => r.Id).ToHashSet();

            var keysToRemove = _timeTriggerLastRan.Keys.Where(id => !ruleIds.Contains(id)).ToList();
            foreach (var key in keysToRemove) _timeTriggerLastRan.Remove(key);

            var onceToRemove = _onceTriggerRan.Where(id => !ruleIds.Contains(id)).ToList();
            foreach (var key in onceToRemove) _onceTriggerRan.Remove(key);

            var keysToRemovePre = _preNotificationLastTriggeredDate.Keys.Where(k => !ruleIds.Contains(k.RuleId)).ToList();
            foreach (var key in keysToRemovePre) _preNotificationLastTriggeredDate.Remove(key);

            var oncePreToRemove = _oncePreNotificationsTriggered.Where(k => !ruleIds.Contains(k.RuleId)).ToList();
            foreach (var key in oncePreToRemove) _oncePreNotificationsTriggered.Remove(key);

            var delaysToRemove = _tempRuleDelays.Keys.Where(id => !ruleIds.Contains(id)).ToList();
            foreach (var id in delaysToRemove) _tempRuleDelays.Remove(id);

            var debouncersToRemove = _networkChangeDebouncers.Keys.Where(id => !ruleIds.Contains(id)).ToList();
            foreach (var key in debouncersToRemove)
            {
                if (_networkChangeDebouncers.Remove(key, out var cts))
                {
                    cts.Cancel();
                    cts.Dispose();
                }
            }

            UpdateAdapterStatuses();
        }
    }

    public void DelayRule(Guid ruleId, TimeSpan delay)
    {
        lock (_lock)
        {
            _tempRuleDelays[ruleId] = delay;
        }
    }

    public void SkipRuleOccurrence(Guid ruleId)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.Now;
            var rule = _configService.Current.Rules.FirstOrDefault(r => r.Id == ruleId);
            if (rule != null)
            {
                if (rule.Trigger is RuleTrigger.Once)
                {
                    _onceTriggerRan.Add(ruleId);
                    var index = _configService.Current.Rules.FindIndex(r => r.Id == ruleId);
                    if (index >= 0)
                    {
                        _configService.Current.Rules[index] = _configService.Current.Rules[index] with { Enabled = false };
                        _configService.Save();
                    }

                    var record = new ExecutionRecord(
                        Guid.NewGuid(),
                        ruleId,
                        RuleSource.Schedule,
                        rule.TargetAdapterId,
                        rule.Action,
                        now,
                        now,
                        Outcome: "SKIPPED",
                        ReasonCode: "MANUAL_SKIP",
                        WindowsErrorCode: null
                    );
                    _ = RuleEngine.WriteExecutionRecordAsync(record);
                    RuleExecuted?.Invoke(this, record);
                }
                else
                {
                    _timeTriggerLastRan[ruleId] = now.Date;
                    _tempRuleDelays.Remove(ruleId);

                    var record = new ExecutionRecord(
                        Guid.NewGuid(),
                        ruleId,
                        RuleSource.Schedule,
                        rule.TargetAdapterId,
                        rule.Action,
                        now,
                        now,
                        Outcome: "SKIPPED",
                        ReasonCode: "MANUAL_SKIP",
                        WindowsErrorCode: null
                    );
                    _ = RuleEngine.WriteExecutionRecordAsync(record);
                    RuleExecuted?.Invoke(this, record);
                }
            }
        }
    }

    private void UpdateAdapterStatuses()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var ni in interfaces)
            {
                _lastAdapterStatuses[ni.Id] = ni.OperationalStatus;
            }
        }
        catch
        {
            // Ignore network interface query failures
        }
    }

    private void OnTimerTick(object? state)
    {
        if (!_timerTickGate.TryEnter())
        {
            return;
        }

        try
        {
            if (!_configService.IsAutomationEnabled)
            {
                return;
            }

            lock (_lock)
            {
                var rules = _configService.Current.Rules.ToList();
                var now = DateTimeOffset.Now;

                foreach (var rule in rules)
                {
                    if (!rule.Enabled) continue;

                    if (rule.Trigger is RuleTrigger.Once once)
                    {
                        var delayOffset = TimeSpan.Zero;
                        if (_tempRuleDelays.TryGetValue(rule.Id, out var offset))
                        {
                            delayOffset = offset;
                        }
                        var adjustedTarget = once.At + delayOffset;

                        // 1. Evaluate Pre-Notifications
                        if (!_onceTriggerRan.Contains(rule.Id))
                        {
                            foreach (var preNotify in rule.PreNotifications)
                            {
                                var preNotifyTime = adjustedTarget - TimeSpan.FromMinutes(preNotify.MinutesBefore);
                                if (now >= preNotifyTime && now < adjustedTarget)
                                {
                                    var key = (rule.Id, preNotify.MinutesBefore);
                                    lock (_lock)
                                    {
                                        if (!_oncePreNotificationsTriggered.Contains(key))
                                        {
                                            _oncePreNotificationsTriggered.Add(key);
                                            _ = Task.Run(() => PreNotificationTriggered?.Invoke(this, new PreNotificationEventArgs(rule, preNotify, adjustedTarget, preNotify.MinutesBefore)));
                                        }
                                    }
                                }
                            }
                        }

                        // 2. Evaluate Rule Execution
                        if (now >= adjustedTarget && !_onceTriggerRan.Contains(rule.Id))
                        {
                            _onceTriggerRan.Add(rule.Id);
                            lock (_lock)
                            {
                                _tempRuleDelays.Remove(rule.Id);
                            }

                            // 检查是否迟到太久（允许最大偏离 TriggerToleranceMinutes 分钟）
                            if (now <= adjustedTarget.AddMinutes(TriggerToleranceMinutes))
                            {
                                _ = ExecuteOnceRuleAndNotifyAsync(rule);
                            }
                            else
                            {
                                // 迟到太久则直接标记为已执行/已失效并写日志以防重复启动
                                lock (_lock)
                                {
                                    var currentRules = _configService.Current.Rules;
                                    var index = currentRules.FindIndex(r => r.Id == rule.Id);
                                    if (index >= 0)
                                    {
                                        currentRules[index] = currentRules[index] with { Enabled = false };
                                        _configService.Save();
                                    }
                                }

                                var record = new ExecutionRecord(
                                    Guid.NewGuid(),
                                    rule.Id,
                                    RuleSource.Schedule,
                                    rule.TargetAdapterId,
                                    rule.Action,
                                    now,
                                    now,
                                    Outcome: "SKIPPED",
                                    ReasonCode: "TRIGGER_EXPIRED",
                                    WindowsErrorCode: null
                                );
                                _ = RuleEngine.WriteExecutionRecordAsync(record);
                                RuleExecuted?.Invoke(this, record);
                            }
                        }
                    }
                    else if (rule.Trigger is RuleTrigger.Daily daily)
                    {
                        var targetToday = new DateTimeOffset(now.Year, now.Month, now.Day, daily.LocalTime.Hour, daily.LocalTime.Minute, 0, now.Offset);
                        var delayOffset = TimeSpan.Zero;
                        if (_tempRuleDelays.TryGetValue(rule.Id, out var offset))
                        {
                            delayOffset = offset;
                        }
                        var adjustedTarget = targetToday + delayOffset;

                        // 1. Evaluate Pre-Notifications
                        if (!_timeTriggerLastRan.TryGetValue(rule.Id, out var lastRanDate) || lastRanDate < now.Date)
                        {
                            foreach (var preNotify in rule.PreNotifications)
                            {
                                var preNotifyTime = adjustedTarget - TimeSpan.FromMinutes(preNotify.MinutesBefore);
                                if (now >= preNotifyTime && now < adjustedTarget)
                                {
                                    var key = (rule.Id, preNotify.MinutesBefore);
                                    lock (_lock)
                                    {
                                        if (!_preNotificationLastTriggeredDate.TryGetValue(key, out var lastTriggered) || lastTriggered < now.Date)
                                        {
                                            _preNotificationLastTriggeredDate[key] = now.Date;
                                            _ = Task.Run(() => PreNotificationTriggered?.Invoke(this, new PreNotificationEventArgs(rule, preNotify, adjustedTarget, preNotify.MinutesBefore)));
                                        }
                                    }
                                }
                            }
                        }

                        // 2. Evaluate Rule Execution
                        if (now >= adjustedTarget)
                        {
                            if (!_timeTriggerLastRan.TryGetValue(rule.Id, out var lastRanDateDailyExec) || lastRanDateDailyExec < now.Date)
                            {
                                _timeTriggerLastRan[rule.Id] = now.Date;
                                lock (_lock)
                                {
                                    _tempRuleDelays.Remove(rule.Id);
                                }

                                // 检查是否迟到太久（允许最大偏离 TriggerToleranceMinutes 分钟）
                                if (now <= adjustedTarget.AddMinutes(TriggerToleranceMinutes))
                                {
                                    _ = ExecuteRuleAndNotifyAsync(rule, RuleSource.Schedule);
                                }
                                else
                                {
                                    var record = new ExecutionRecord(
                                        Guid.NewGuid(),
                                        rule.Id,
                                        RuleSource.Schedule,
                                        rule.TargetAdapterId,
                                        rule.Action,
                                        now,
                                        now,
                                        Outcome: "SKIPPED",
                                        ReasonCode: "TRIGGER_EXPIRED",
                                        WindowsErrorCode: null
                                    );
                                    _ = RuleEngine.WriteExecutionRecordAsync(record);
                                    RuleExecuted?.Invoke(this, record);
                                }
                            }
                        }
                    }
                    else if (rule.Trigger is RuleTrigger.Weekly weekly)
                    {
                        var targetToday = new DateTimeOffset(now.Year, now.Month, now.Day, weekly.LocalTime.Hour, weekly.LocalTime.Minute, 0, now.Offset);
                        var delayOffset = TimeSpan.Zero;
                        if (_tempRuleDelays.TryGetValue(rule.Id, out var offset))
                        {
                            delayOffset = offset;
                        }
                        var adjustedTarget = targetToday + delayOffset;

                        // 1. Evaluate Pre-Notifications
                        if (weekly.Weekdays.Contains(now.DayOfWeek) && (!_timeTriggerLastRan.TryGetValue(rule.Id, out var lastRanDate) || lastRanDate < now.Date))
                        {
                            foreach (var preNotify in rule.PreNotifications)
                            {
                                var preNotifyTime = adjustedTarget - TimeSpan.FromMinutes(preNotify.MinutesBefore);
                                if (now >= preNotifyTime && now < adjustedTarget)
                                {
                                    var key = (rule.Id, preNotify.MinutesBefore);
                                    lock (_lock)
                                    {
                                        if (!_preNotificationLastTriggeredDate.TryGetValue(key, out var lastTriggered) || lastTriggered < now.Date)
                                        {
                                            _preNotificationLastTriggeredDate[key] = now.Date;
                                            _ = Task.Run(() => PreNotificationTriggered?.Invoke(this, new PreNotificationEventArgs(rule, preNotify, adjustedTarget, preNotify.MinutesBefore)));
                                        }
                                    }
                                }
                            }
                        }

                        // 2. Evaluate Rule Execution
                        if (weekly.Weekdays.Contains(now.DayOfWeek) && now >= adjustedTarget)
                        {
                            if (!_timeTriggerLastRan.TryGetValue(rule.Id, out var lastRanDateWeeklyExec) || lastRanDateWeeklyExec < now.Date)
                            {
                                _timeTriggerLastRan[rule.Id] = now.Date;
                                lock (_lock)
                                {
                                    _tempRuleDelays.Remove(rule.Id);
                                }

                                // 检查是否迟到太久（允许最大偏离 TriggerToleranceMinutes 分钟）
                                if (now <= adjustedTarget.AddMinutes(TriggerToleranceMinutes))
                                {
                                    _ = ExecuteRuleAndNotifyAsync(rule, RuleSource.Schedule);
                                }
                                else
                                {
                                    var record = new ExecutionRecord(
                                        Guid.NewGuid(),
                                        rule.Id,
                                        RuleSource.Schedule,
                                        rule.TargetAdapterId,
                                        rule.Action,
                                        now,
                                        now,
                                        Outcome: "SKIPPED",
                                        ReasonCode: "TRIGGER_EXPIRED",
                                        WindowsErrorCode: null
                                    );
                                    _ = RuleEngine.WriteExecutionRecordAsync(record);
                                    RuleExecuted?.Invoke(this, record);
                                }
                            }
                        }
                    }
                }

                _ = EvaluateConnectivityRulesAsync(rules);
            }
        }
        catch
        {
            // Keep timer thread safe
        }
        finally
        {
            _timerTickGate.Exit();
        }
    }

    private async Task EvaluateConnectivityRulesAsync(IReadOnlyList<AutomationRule> rules)
    {
        if (!_connectivityEvaluationGate.TryEnter())
        {
            return;
        }

        try
        {
            var monitoredRules = rules
                .Where(rule => rule.Enabled
                    && rule.Trigger is RuleTrigger.NetworkChange
                    {
                        Condition: AdapterOfflineCondition
                    })
                .ToList();
            var monitoredAdapterIds = monitoredRules
                .Select(rule => ((AdapterOfflineCondition)((RuleTrigger.NetworkChange)rule.Trigger).Condition).AdapterId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (monitoredAdapterIds.Count == 0)
            {
                return;
            }

            var policy = _configService.Current.ProbePolicy;
            var probeTasks = monitoredAdapterIds.Select(async adapterId =>
            {
                var result = await _connectivityService.ProbeAdapterAsync(adapterId, policy);
                return (AdapterId: adapterId, result.Online);
            });
            var results = await Task.WhenAll(probeTasks);

            lock (_lock)
            {
                foreach (var result in results)
                {
                    var hadPreviousState = _lastAdapterInternetStates.TryGetValue(result.AdapterId, out var wasOnline);
                    _lastAdapterInternetStates[result.AdapterId] = result.Online;

                    if (!RuleExecutionPolicy.ShouldTriggerOfflineTransition(
                            hadPreviousState,
                            wasOnline,
                            result.Online))
                    {
                        continue;
                    }

                    foreach (var rule in monitoredRules.Where(rule =>
                                 rule.Trigger is RuleTrigger.NetworkChange
                                 {
                                     Condition: AdapterOfflineCondition condition
                                 }
                                 && string.Equals(condition.AdapterId, result.AdapterId, StringComparison.OrdinalIgnoreCase)))
                    {
                        var networkChange = (RuleTrigger.NetworkChange)rule.Trigger;
                        TriggerNetworkChangeDebounce(rule, networkChange.DebounceSeconds);
                    }
                }
            }
        }
        catch
        {
            // A failed probe cycle must not stop the scheduler timer.
        }
        finally
        {
            _connectivityEvaluationGate.Exit();
        }
    }

    private async Task ExecuteRuleAndNotifyAsync(AutomationRule rule, RuleSource source)
    {
        try
        {
            var record = await _ruleEngine.ExecuteRuleAsync(rule, source);
            RuleExecuted?.Invoke(this, record);
        }
        catch
        {
            // Fail silently
        }
    }

    private async Task ExecuteOnceRuleAndNotifyAsync(AutomationRule rule)
    {
        try
        {
            var record = await _ruleEngine.ExecuteRuleAsync(rule, RuleSource.Schedule);
            RuleExecuted?.Invoke(this, record);

            lock (_lock)
            {
                var currentRules = _configService.Current.Rules;
                var index = currentRules.FindIndex(r => r.Id == rule.Id);
                if (index >= 0)
                {
                    currentRules[index] = currentRules[index] with { Enabled = false };
                    _configService.Save();
                }
            }
        }
        catch
        {
            // Ignore execution failure
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        if (!_configService.IsAutomationEnabled)
        {
            return;
        }

        lock (_lock)
        {
            Dictionary<string, NetworkInterface> currentInterfaces;
            try
            {
                currentInterfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .ToDictionary(ni => ni.Id);
            }
            catch
            {
                return;
            }

            var rules = _configService.Current.Rules.ToList();

            foreach (var rule in rules)
            {
                if (!rule.Enabled || rule.Trigger is not RuleTrigger.NetworkChange netChange) continue;

                if (netChange.Condition is AdapterOfflineCondition cond)
                {
                    var adapterId = cond.AdapterId;
                    var isCurrentlyUp = currentInterfaces.TryGetValue(adapterId, out var currentNi) &&
                                        currentNi.OperationalStatus == OperationalStatus.Up;

                    _lastAdapterStatuses.TryGetValue(adapterId, out var lastStatus);
                    var wasPreviouslyUp = lastStatus == OperationalStatus.Up;

                    if (currentNi != null)
                    {
                        _lastAdapterStatuses[adapterId] = currentNi.OperationalStatus;
                    }
                    else
                    {
                        _lastAdapterStatuses[adapterId] = OperationalStatus.NotPresent;
                    }

                    if (wasPreviouslyUp && !isCurrentlyUp)
                    {
                        TriggerNetworkChangeDebounce(rule, netChange.DebounceSeconds);
                    }
                }
            }
        }
    }

    private void TriggerNetworkChangeDebounce(AutomationRule rule, int debounceSeconds)
    {
        if (_networkChangeDebouncers.TryGetValue(rule.Id, out var existingCts))
        {
            existingCts.Cancel();
            existingCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _networkChangeDebouncers[rule.Id] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(debounceSeconds), cts.Token);

                if (cts.Token.IsCancellationRequested) return;

                var isStillOffline = false;
                if (rule.Trigger is RuleTrigger.NetworkChange netChange && netChange.Condition is AdapterOfflineCondition cond)
                {
                    var result = await _connectivityService.ProbeAdapterAsync(
                        cond.AdapterId,
                        _configService.Current.ProbePolicy);
                    isStillOffline = !result.Online;
                }

                if (isStillOffline)
                {
                    await ExecuteRuleAndNotifyAsync(rule, RuleSource.NetworkChange);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        });
    }

    public void Dispose()
    {
        Stop();
    }
}
