using System.IO;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using NetRelay.Models;

namespace NetRelay.Services;

public sealed class PreNotificationEventArgs : EventArgs
{
    public Guid NotificationActionId { get; }
    public string NotificationActionToken { get; }
    public AutomationRule Rule { get; }
    public PreNotification Notification { get; }
    public DateTimeOffset TargetTime { get; }
    public int MinutesRemaining { get; }

    public PreNotificationEventArgs(
        Guid notificationActionId,
        string notificationActionToken,
        AutomationRule rule,
        PreNotification notification,
        DateTimeOffset targetTime,
        int minutesRemaining)
    {
        NotificationActionId = notificationActionId;
        NotificationActionToken = notificationActionToken;
        Rule = rule;
        Notification = notification;
        TargetTime = targetTime;
        MinutesRemaining = minutesRemaining;
    }
}

public sealed record PreNotificationActionResult(bool Applied, string ReasonCode, Guid? RuleId = null);

public sealed class RuleSchedulerService : IDisposable
{
    private sealed record PendingPreNotificationAction(
        Guid Id,
        string Token,
        Guid RuleId,
        PreNotification Notification,
        DateTimeOffset TargetTime,
        DateTimeOffset ExpiresAt);

    private readonly RuleEngine _ruleEngine;
    private readonly ConfigurationService _configService;
    private readonly ConnectivityService _connectivityService;
    private System.Threading.Timer? _timer;
    private CancellationTokenSource _cts = new();
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

    // One-shot action tickets for interactive notifications. Key: notification action ID.
    private readonly Dictionary<Guid, PendingPreNotificationAction> _pendingPreNotificationActions = new();

    // Debounce cancel tokens for network change rules. Key: Rule ID.
    private readonly Dictionary<Guid, CancellationTokenSource> _networkChangeDebouncers = new();

    // Last known operational status of adapters for edge trigger evaluation. Key: Adapter ID.
    private readonly Dictionary<string, OperationalStatus> _lastAdapterStatuses = new();

    // Last known internet state of adapters for HTTP-probed edge trigger evaluation. Key: Adapter ID.
    private readonly Dictionary<string, bool> _lastAdapterInternetStates = new(StringComparer.OrdinalIgnoreCase);

    // Tracks execution records from the last 2 days (yesterday and today) in-memory for stateless recovery evaluation.
    private readonly List<ExecutionRecord> _executionHistory = new();

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

            _cts = new CancellationTokenSource();
            InitializeLastRanFromLogs();
            UpdateAdapterStatuses();
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            _ruleEngine.ExecutionRecorded += OnRuleExecutionRecorded;
            _timer = new System.Threading.Timer(OnTimerTick, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        }
    }

    private void InitializeLastRanFromLogs()
    {
        _executionHistory.Clear();
        try
        {
            var logDir = _ruleEngine.ExecutionLogDirectory;
            if (!Directory.Exists(logDir)) return;

            var scheduledTimeRuleIds = _configService.Current.Rules
                .Where(rule => rule.Trigger is RuleTrigger.Daily or RuleTrigger.Weekly)
                .Select(rule => rule.Id)
                .ToHashSet();

            for (int dayOffset = -1; dayOffset <= 0; dayOffset++)
            {
                var logPath = Path.Combine(logDir, $"execution-{DateTime.Today.AddDays(dayOffset):yyyy-MM-dd}.jsonl");
                if (!File.Exists(logPath)) continue;

                var lines = File.ReadAllLines(logPath);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        var record = JsonSerializer.Deserialize<ExecutionRecord>(line);
                        if (record is not null)
                        {
                            _executionHistory.Add(record);

                            if (dayOffset == 0 && RuleSchedulerPolicy.ShouldRestoreTimeRuleOccurrence(record, scheduledTimeRuleIds))
                            {
                                var ruleId = record.RuleId!.Value;
                                var ranDate = record.StartedAt.LocalDateTime.Date;

                                if (!_timeTriggerLastRan.TryGetValue(ruleId, out var existingDate) || existingDate < ranDate)
                                {
                                    _timeTriggerLastRan[ruleId] = ranDate;
                                }
                            }
                        }
                    }
                    catch
                    {
                        // 忽略单行日志解析错误以维持健壮性
                    }
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
            if (_timer == null) return;

            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            _ruleEngine.ExecutionRecorded -= OnRuleExecutionRecorded;

            _timer.Dispose();
            _timer = null;

            _cts.Cancel();
            _cts.Dispose();

            foreach (var cts in _networkChangeDebouncers.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }
            _networkChangeDebouncers.Clear();
            _pendingPreNotificationActions.Clear();
        }
    }

    public void Reload(Guid? resetRuleId = null)
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
                RemoveDebouncer(key);
            }

            foreach (var actionId in _pendingPreNotificationActions
                         .Where(pair => !ruleIds.Contains(pair.Value.RuleId))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _pendingPreNotificationActions.Remove(actionId);
            }

            if (resetRuleId.HasValue && ruleIds.Contains(resetRuleId.Value))
            {
                ResetRuntimeState(resetRuleId.Value);
                _lastAdapterStatuses.Clear();
                _lastAdapterInternetStates.Clear();
            }

            UpdateAdapterStatuses();
        }
    }

    private void ResetRuntimeState(Guid ruleId)
    {
        _timeTriggerLastRan.Remove(ruleId);
        _onceTriggerRan.Remove(ruleId);
        _tempRuleDelays.Remove(ruleId);

        foreach (var key in _preNotificationLastTriggeredDate.Keys
                     .Where(key => key.RuleId == ruleId)
                     .ToArray())
        {
            _preNotificationLastTriggeredDate.Remove(key);
        }

        _oncePreNotificationsTriggered.RemoveWhere(key => key.RuleId == ruleId);
        RemoveDebouncer(ruleId);
        InvalidatePendingPreNotificationActions(ruleId);
    }

    private void RemoveDebouncer(Guid ruleId)
    {
        if (_networkChangeDebouncers.Remove(ruleId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    public void DelayRule(Guid ruleId, TimeSpan delay)
    {
        lock (_lock)
        {
            _tempRuleDelays[ruleId] = delay;
            InvalidatePendingPreNotificationActions(ruleId);
        }
    }

    public void SkipRuleOccurrence(Guid ruleId)
    {
        lock (_lock)
        {
            SkipRuleOccurrenceCore(ruleId, RuleSource.Schedule, "MANUAL_SKIP");
        }
    }

    public PreNotificationActionResult TryApplyPreNotificationAction(
        Guid notificationActionId,
        string token,
        PreNotificationAction action,
        DateTimeOffset? evaluatedAt = null)
    {
        lock (_lock)
        {
            var now = evaluatedAt ?? DateTimeOffset.Now;
            if (!_pendingPreNotificationActions.TryGetValue(notificationActionId, out var pending))
            {
                return new PreNotificationActionResult(false, "NOTIFICATION_NOT_FOUND");
            }

            if (!TokensEqual(pending.Token, token))
            {
                return new PreNotificationActionResult(false, "NOTIFICATION_TOKEN_INVALID");
            }

            var rule = _configService.Current.Rules.FirstOrDefault(candidate => candidate.Id == pending.RuleId);
            if (rule is null || !rule.Enabled || !rule.PreNotifications.Contains(pending.Notification))
            {
                _pendingPreNotificationActions.Remove(notificationActionId);
                return new PreNotificationActionResult(false, "NOTIFICATION_RULE_INVALID", pending.RuleId);
            }

            if (now >= pending.TargetTime || now > pending.ExpiresAt)
            {
                _pendingPreNotificationActions.Remove(notificationActionId);
                return new PreNotificationActionResult(false, "NOTIFICATION_EXPIRED", pending.RuleId);
            }

            if (action == PreNotificationAction.Delay)
            {
                if (!pending.Notification.AllowDelay || pending.Notification.DelayMinutes <= 0)
                {
                    return new PreNotificationActionResult(false, "NOTIFICATION_ACTION_NOT_ALLOWED", pending.RuleId);
                }

                DelayRule(pending.RuleId, TimeSpan.FromMinutes(pending.Notification.DelayMinutes));
                WriteNotificationActionRecord(rule, "NOTIFICATION_DELAYED");
                return new PreNotificationActionResult(true, "NOTIFICATION_DELAYED", pending.RuleId);
            }

            if (!pending.Notification.AllowCancelOccurrence)
            {
                return new PreNotificationActionResult(false, "NOTIFICATION_ACTION_NOT_ALLOWED", pending.RuleId);
            }

            SkipRuleOccurrenceCore(pending.RuleId, RuleSource.Notification, "NOTIFICATION_CANCELLED");
            return new PreNotificationActionResult(true, "NOTIFICATION_CANCELLED", pending.RuleId);
        }
    }

    private void SkipRuleOccurrenceCore(Guid ruleId, RuleSource source, string reasonCode)
    {
        var now = DateTimeOffset.Now;
        var rule = _configService.Current.Rules.FirstOrDefault(candidate => candidate.Id == ruleId);
        if (rule is null)
        {
            return;
        }

        if (rule.Trigger is RuleTrigger.Once)
        {
            _onceTriggerRan.Add(ruleId);
            var index = _configService.Current.Rules.FindIndex(candidate => candidate.Id == ruleId);
            if (index >= 0)
            {
                _configService.Current.Rules[index] = _configService.Current.Rules[index] with { Enabled = false };
                _configService.Save();
            }
        }
        else
        {
            _timeTriggerLastRan[ruleId] = now.Date;
            _tempRuleDelays.Remove(ruleId);
        }

        var record = new ExecutionRecord(
            Guid.NewGuid(),
            ruleId,
            source,
            rule.TargetAdapterId,
            rule.Action,
            now,
            now,
            Outcome: "SKIPPED",
            ReasonCode: reasonCode,
            WindowsErrorCode: null);
        _ = _ruleEngine.WriteExecutionRecordAsync(record);
        RuleExecuted?.Invoke(this, record);
        InvalidatePendingPreNotificationActions(ruleId);
    }

    private void WriteNotificationActionRecord(AutomationRule rule, string reasonCode)
    {
        var now = DateTimeOffset.Now;
        var record = new ExecutionRecord(
            Guid.NewGuid(),
            rule.Id,
            RuleSource.Notification,
            rule.TargetAdapterId,
            rule.Action,
            now,
            now,
            Outcome: "SKIPPED",
            ReasonCode: reasonCode,
            WindowsErrorCode: null);
        _ = _ruleEngine.WriteExecutionRecordAsync(record);
        RuleExecuted?.Invoke(this, record);
    }

    private void TriggerPreNotification(
        AutomationRule rule,
        PreNotification notification,
        DateTimeOffset targetTime,
        int minutesRemaining)
    {
        var actionId = Guid.NewGuid();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _pendingPreNotificationActions[actionId] = new PendingPreNotificationAction(
            actionId,
            token,
            rule.Id,
            notification,
            targetTime,
            targetTime.AddMinutes(TriggerToleranceMinutes));

        var args = new PreNotificationEventArgs(actionId, token, rule, notification, targetTime, minutesRemaining);
        _ = Task.Run(() => PreNotificationTriggered?.Invoke(this, args));
    }

    private void InvalidatePendingPreNotificationActions(Guid ruleId)
    {
        foreach (var actionId in _pendingPreNotificationActions
                     .Where(pair => pair.Value.RuleId == ruleId)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _pendingPreNotificationActions.Remove(actionId);
        }
    }

    private void RemoveExpiredPreNotificationActions(DateTimeOffset now)
    {
        foreach (var actionId in _pendingPreNotificationActions
                     .Where(pair => now > pair.Value.ExpiresAt)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _pendingPreNotificationActions.Remove(actionId);
        }
    }

    private static bool TokensEqual(string expected, string supplied)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
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
            if (!_configService.IsAutomationEnabled || App.PolicyService?.IsBlocked == true)
            {
                return;
            }

            lock (_lock)
            {
                var rules = _configService.Current.Rules.ToList();
                var now = DateTimeOffset.Now;
                RemoveExpiredPreNotificationActions(now);

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
                                            TriggerPreNotification(rule, preNotify, adjustedTarget, preNotify.MinutesBefore);
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
                                _ = _ruleEngine.WriteExecutionRecordAsync(record);
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
                                            TriggerPreNotification(rule, preNotify, adjustedTarget, preNotify.MinutesBefore);
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
                                    _ = _ruleEngine.WriteExecutionRecordAsync(record);
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
                                            TriggerPreNotification(rule, preNotify, adjustedTarget, preNotify.MinutesBefore);
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
                                    _ = _ruleEngine.WriteExecutionRecordAsync(record);
                                    RuleExecuted?.Invoke(this, record);
                                }
                            }
                        }
                    }
                }

                // 3. Evaluate Automatic Recoveries
                foreach (var rule in rules)
                {
                    if (!rule.Enabled) continue;
                    if (rule.Recovery is not { Enabled: true, DelayMinutes: var delayMinutes and > 0 }) continue;

                    // Find the last successful Disable execution of this rule
                    var lastDisableRecord = _executionHistory
                        .Where(r => r.RuleId == rule.Id
                            && r.RequestedAction == RuleAction.Disable
                            && r.Outcome == "SUCCESS")
                        .OrderByDescending(r => r.StartedAt)
                        .FirstOrDefault();

                    if (lastDisableRecord == null) continue;

                    // Check if there is any subsequent record for this rule or target adapter
                    // that indicates recovery has already been executed/skipped or the adapter was enabled
                    var hasSubsequentAction = _executionHistory.Any(r =>
                        r.StartedAt > lastDisableRecord.StartedAt
                        && (
                            // Either this rule had a recovery/enable attempt (success, failed, or expired)
                            (r.RuleId == rule.Id && (r.RequestedAction == RuleAction.Enable || r.ReasonCode == "TRIGGER_EXPIRED"))
                            // Or there was a manual enable on this adapter
                            || (string.Equals(r.TargetAdapterId, rule.TargetAdapterId, StringComparison.OrdinalIgnoreCase)
                                && r.RequestedAction == RuleAction.Enable
                                && r.Outcome == "SUCCESS")
                           )
                    );

                    if (hasSubsequentAction) continue;

                    var recoveryTime = lastDisableRecord.StartedAt.AddMinutes(delayMinutes);
                    if (now >= recoveryTime)
                    {
                        if (now <= recoveryTime.AddMinutes(TriggerToleranceMinutes))
                        {
                            _ = ExecuteRecoveryRuleAndNotifyAsync(rule);
                        }
                        else
                        {
                            var record = new ExecutionRecord(
                                Guid.NewGuid(),
                                rule.Id,
                                RuleSource.Recovery,
                                rule.TargetAdapterId,
                                RuleAction.Enable,
                                now,
                                now,
                                Outcome: "SKIPPED",
                                ReasonCode: "TRIGGER_EXPIRED",
                                WindowsErrorCode: null
                            );
                            _ = _ruleEngine.WriteExecutionRecordAsync(record);
                            RuleExecuted?.Invoke(this, record);
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
            CancellationToken token;
            try
            {
                lock (_lock)
                {
                    token = _cts.Token;
                }
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            var probeTasks = monitoredAdapterIds.Select(async adapterId =>
            {
                try
                {
                    var result = await _connectivityService.ProbeAdapterAsync(adapterId, policy, token);
                    return (AdapterId: adapterId, result.Online);
                }
                catch
                {
                    return (AdapterId: adapterId, Online: false);
                }
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
        if (!_configService.IsAutomationEnabled || App.PolicyService?.IsBlocked == true)
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
                        _configService.Current.ProbePolicy,
                        cts.Token);
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

    private void OnRuleExecutionRecorded(object? sender, ExecutionRecord record)
    {
        lock (_lock)
        {
            _executionHistory.Add(record);
            var threshold = DateTimeOffset.Now.AddDays(-2);
            _executionHistory.RemoveAll(r => r.StartedAt < threshold);
        }
    }

    private async Task ExecuteRecoveryRuleAndNotifyAsync(AutomationRule rule)
    {
        try
        {
            var recoveryRule = rule with { Action = RuleAction.Enable };
            var record = await _ruleEngine.ExecuteRuleAsync(recoveryRule, RuleSource.Recovery);
            RuleExecuted?.Invoke(this, record);
        }
        catch
        {
            // Ignore execution failure
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
