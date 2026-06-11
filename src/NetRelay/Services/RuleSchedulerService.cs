using System.Net.NetworkInformation;
using System.Threading;
using NetRelay.Models;

namespace NetRelay.Services;

public sealed class RuleSchedulerService : IDisposable
{
    private readonly RuleEngine _ruleEngine;
    private readonly ConfigurationService _configService;
    private System.Threading.Timer? _timer;
    private readonly object _lock = new();
    
    // Tracks daily/weekly rule execution dates to prevent multiple fires within the matching minute.
    private readonly Dictionary<Guid, DateTime> _timeTriggerLastRan = new();

    // Tracks once rule executed state in-memory.
    private readonly HashSet<Guid> _onceTriggerRan = new();

    // Debounce cancel tokens for network change rules. Key: Rule ID.
    private readonly Dictionary<Guid, CancellationTokenSource> _networkChangeDebouncers = new();

    // Last known operational status of adapters for edge trigger evaluation. Key: Adapter ID.
    private readonly Dictionary<string, OperationalStatus> _lastAdapterStatuses = new();

    public RuleSchedulerService(RuleEngine ruleEngine, ConfigurationService configService)
    {
        _ruleEngine = ruleEngine;
        _configService = configService;
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_timer != null) return;

            UpdateAdapterStatuses();
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            _timer = new System.Threading.Timer(OnTimerTick, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
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
        try
        {
            var rules = _configService.Current.Rules.ToList();
            var now = DateTimeOffset.Now;

            foreach (var rule in rules)
            {
                if (!rule.Enabled) continue;

                if (rule.Trigger is RuleTrigger.Once once)
                {
                    if (now >= once.At && !_onceTriggerRan.Contains(rule.Id))
                    {
                        _onceTriggerRan.Add(rule.Id);
                        _ = ExecuteOnceRuleAsync(rule);
                    }
                }
                else if (rule.Trigger is RuleTrigger.Daily daily)
                {
                    var sched = daily.LocalTime;
                    if (now.Hour == sched.Hour && now.Minute == sched.Minute)
                    {
                        if (!_timeTriggerLastRan.TryGetValue(rule.Id, out var lastRanDate) || lastRanDate < now.Date)
                        {
                            _timeTriggerLastRan[rule.Id] = now.Date;
                            _ = _ruleEngine.ExecuteRuleAsync(rule, RuleSource.Schedule);
                        }
                    }
                }
                else if (rule.Trigger is RuleTrigger.Weekly weekly)
                {
                    var sched = weekly.LocalTime;
                    if (weekly.Weekdays.Contains(now.DayOfWeek) && now.Hour == sched.Hour && now.Minute == sched.Minute)
                    {
                        if (!_timeTriggerLastRan.TryGetValue(rule.Id, out var lastRanDate) || lastRanDate < now.Date)
                        {
                            _timeTriggerLastRan[rule.Id] = now.Date;
                            _ = _ruleEngine.ExecuteRuleAsync(rule, RuleSource.Schedule);
                        }
                    }
                }
            }
        }
        catch
        {
            // Keep timer thread safe
        }
    }

    private async Task ExecuteOnceRuleAsync(AutomationRule rule)
    {
        try
        {
            await _ruleEngine.ExecuteRuleAsync(rule, RuleSource.Schedule);

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
            // Ignore execution failure to not crash background thread
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
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
                    var ni = NetworkInterface.GetAllNetworkInterfaces()
                        .FirstOrDefault(n => string.Equals(n.Id, cond.AdapterId, StringComparison.OrdinalIgnoreCase));
                    isStillOffline = ni == null || ni.OperationalStatus != OperationalStatus.Up;
                }

                if (isStillOffline)
                {
                    await _ruleEngine.ExecuteRuleAsync(rule, RuleSource.NetworkChange);
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
