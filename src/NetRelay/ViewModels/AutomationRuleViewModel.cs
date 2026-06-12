using System.Linq;
using System.Net.NetworkInformation;
using NetRelay.Infrastructure;
using NetRelay.Models;
using NetRelay.Services;

namespace NetRelay.ViewModels;

public sealed class AutomationRuleViewModel : ObservableObject
{
    private AutomationRule _rule;
    private readonly ConfigurationService _configService;
    private readonly NativeNetworkConnectionService _connectionService;
    private readonly Action _onRuleChanged;

    public AutomationRuleViewModel(
        AutomationRule rule,
        ConfigurationService configService,
        NativeNetworkConnectionService connectionService,
        Action onRuleChanged)
    {
        _rule = rule;
        _configService = configService;
        _connectionService = connectionService;
        _onRuleChanged = onRuleChanged;
    }

    public AutomationRule Rule => _rule;

    public Guid Id => _rule.Id;

    public string Name => _rule.Name;

    public bool Enabled
    {
        get => _rule.Enabled;
        set
        {
            if (_rule.Enabled != value)
            {
                // Update in configuration list
                var currentRules = _configService.Current.Rules;
                var index = currentRules.FindIndex(r => r.Id == _rule.Id);
                if (index >= 0)
                {
                    _rule = _rule with { Enabled = value };
                    currentRules[index] = _rule;
                    _configService.Save();
                    RaisePropertyChanged(nameof(Enabled));
                    _onRuleChanged();
                }
            }
        }
    }

    public string TargetAdapterId => _rule.TargetAdapterId;

    public string TargetAdapterName => ResolveAdapterName(_rule.TargetAdapterId, _connectionService);

    public string ActionLabel => _rule.Action == RuleAction.Enable ? "启用网卡" : "禁用网卡";

    public bool IsActionDisable => _rule.Action == RuleAction.Disable;

    public string TriggerLabel
    {
        get
        {
            if (_rule.Trigger is RuleTrigger.Once once)
            {
                return $"单次：{once.At.LocalDateTime:yyyy-MM-dd HH:mm}";
            }
            if (_rule.Trigger is RuleTrigger.Daily daily)
            {
                return $"每日 {daily.LocalTime:HH:mm}";
            }
            if (_rule.Trigger is RuleTrigger.Weekly weekly)
            {
                var days = string.Join("、", weekly.Weekdays.OrderBy(d => (int)d == 0 ? 7 : (int)d).Select(GetWeekdayChName));
                return $"每周 ({days}) {weekly.LocalTime:HH:mm}";
            }
            if (_rule.Trigger is RuleTrigger.NetworkChange netChange)
            {
                if (netChange.Condition is AdapterOfflineCondition cond)
                {
                    var name = ResolveAdapterName(cond.AdapterId, _connectionService);
                    return $"网络变化：“{name}”断开时 (防抖 {netChange.DebounceSeconds} 秒)";
                }
                return "网络条件变化时";
            }
            return "未知触发器";
        }
    }

    public string RecoveryLabel => _rule.Recovery is { Enabled: true, DelayMinutes: var mins }
        ? $"延时 {mins} 分钟自动启用"
        : "无自动恢复";

    public bool HasRecovery => _rule.Recovery is { Enabled: true };

    public bool RequireUsableBackup => _rule.RequireUsableBackup;

    public int CooldownSeconds => _rule.CooldownSeconds;

    public void UpdateRule(AutomationRule newRule)
    {
        _rule = newRule;
        RaisePropertyChanged(string.Empty); // Raise all properties changed
    }

    private static string GetWeekdayChName(DayOfWeek day)
    {
        return day switch
        {
            DayOfWeek.Monday => "周一",
            DayOfWeek.Tuesday => "周二",
            DayOfWeek.Wednesday => "周三",
            DayOfWeek.Thursday => "周四",
            DayOfWeek.Friday => "周五",
            DayOfWeek.Saturday => "周六",
            DayOfWeek.Sunday => "周日",
            _ => day.ToString()
        };
    }

    private static string ResolveAdapterName(string adapterId, NativeNetworkConnectionService connectionService)
    {
        try
        {
            var ni = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => string.Equals(n.Id, adapterId, StringComparison.OrdinalIgnoreCase));
            if (ni != null) return ni.Name;

            var conn = connectionService.GetConnections()
                .FirstOrDefault(c => string.Equals(c.Id.ToString("B"), adapterId, StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(c.Id.ToString(), adapterId, StringComparison.OrdinalIgnoreCase));
            if (conn != null) return conn.Name;
        }
        catch { }

        // Fallback: show short GUID
        if (Guid.TryParse(adapterId, out var g))
        {
            var s = g.ToString().ToUpper();
            return $"未知网卡 (...{s.Substring(s.Length - 8)})";
        }
        return $"未知网卡 ({adapterId})";
    }
}
