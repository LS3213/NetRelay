using System.Windows;
using System.Windows.Controls;
using NetRelay.Models;
using NetRelay.Services;

namespace NetRelay.Dialogs;

public partial class RuleEditDialog : Window
{
    private readonly AutomationRule? _rule;
    private readonly List<NetworkAdapterInfo> _adapters;
    private readonly AppConfiguration _config;

    public AutomationRule? ResultRule { get; private set; }

    public RuleEditDialog(AutomationRule? rule, List<NetworkAdapterInfo> adapters, AppConfiguration config)
    {
        InitializeComponent();
        _rule = rule;
        _adapters = adapters;
        _config = config;

        PopulateComboboxes();
        LoadData();
    }

    private void PopulateComboboxes()
    {
        // Populate Adapters List
        TargetAdapterComboBox.ItemsSource = _adapters;
        ConditionAdapterComboBox.ItemsSource = _adapters.Where(a => a.CanToggle || a.IsEnabled).ToList();

        // Populate Time Dropdowns
        var hours = Enumerable.Range(0, 24).Select(i => i.ToString("00")).ToList();
        var minutes = Enumerable.Range(0, 60).Select(i => i.ToString("00")).ToList();

        OnceHourComboBox.ItemsSource = hours;
        OnceMinuteComboBox.ItemsSource = hours; // wait, hours and minutes have different count, bind to minutes
        OnceMinuteComboBox.ItemsSource = minutes;
        
        DailyHourComboBox.ItemsSource = hours;
        DailyMinuteComboBox.ItemsSource = minutes;
        
        WeeklyHourComboBox.ItemsSource = hours;
        WeeklyMinuteComboBox.ItemsSource = minutes;

        // Default Selections
        OnceDatePicker.SelectedDate = DateTime.Today;
        OnceHourComboBox.SelectedIndex = 12;
        OnceMinuteComboBox.SelectedIndex = 0;

        DailyHourComboBox.SelectedIndex = 23;
        DailyMinuteComboBox.SelectedIndex = 0;

        WeeklyHourComboBox.SelectedIndex = 23;
        WeeklyMinuteComboBox.SelectedIndex = 0;
    }

    private void LoadData()
    {
        if (_rule is null)
        {
            DialogTitleText.Text = "添加自动化规则";
            TriggerTypeComboBox.SelectedIndex = 1; // Default to Daily
            CooldownTextBox.Text = (_config.CooldownMinutes * 60).ToString();
            DebounceTextBox.Text = _config.DebounceSeconds.ToString();
            return;
        }

        DialogTitleText.Text = "编辑自动化规则";
        RuleNameTextBox.Text = _rule.Name;
        ActionComboBox.SelectedIndex = _rule.Action == RuleAction.Disable ? 0 : 1;
        TargetAdapterComboBox.SelectedValue = FindMatchingAdapterId(TargetAdapterComboBox.ItemsSource, _rule.TargetAdapterId);
        CooldownTextBox.Text = _rule.CooldownSeconds.ToString();
        RequireBackupCheck.IsChecked = _rule.RequireUsableBackup;

        // Triggers Mapping
        if (_rule.Trigger is RuleTrigger.Once once)
        {
            TriggerTypeComboBox.SelectedIndex = 0;
            var localTime = once.At.LocalDateTime;
            OnceDatePicker.SelectedDate = localTime.Date;
            OnceHourComboBox.SelectedItem = localTime.Hour.ToString("00");
            OnceMinuteComboBox.SelectedItem = localTime.Minute.ToString("00");
        }
        else if (_rule.Trigger is RuleTrigger.Daily daily)
        {
            TriggerTypeComboBox.SelectedIndex = 1;
            DailyHourComboBox.SelectedItem = daily.LocalTime.Hour.ToString("00");
            DailyMinuteComboBox.SelectedItem = daily.LocalTime.Minute.ToString("00");
        }
        else if (_rule.Trigger is RuleTrigger.Weekly weekly)
        {
            TriggerTypeComboBox.SelectedIndex = 2;
            WeeklyHourComboBox.SelectedItem = weekly.LocalTime.Hour.ToString("00");
            WeeklyMinuteComboBox.SelectedItem = weekly.LocalTime.Minute.ToString("00");

            MonCheck.IsChecked = weekly.Weekdays.Contains(DayOfWeek.Monday);
            TueCheck.IsChecked = weekly.Weekdays.Contains(DayOfWeek.Tuesday);
            WedCheck.IsChecked = weekly.Weekdays.Contains(DayOfWeek.Wednesday);
            ThuCheck.IsChecked = weekly.Weekdays.Contains(DayOfWeek.Thursday);
            FriCheck.IsChecked = weekly.Weekdays.Contains(DayOfWeek.Friday);
            SatCheck.IsChecked = weekly.Weekdays.Contains(DayOfWeek.Saturday);
            SunCheck.IsChecked = weekly.Weekdays.Contains(DayOfWeek.Sunday);
        }
        else if (_rule.Trigger is RuleTrigger.NetworkChange netChange)
        {
            TriggerTypeComboBox.SelectedIndex = 3;
            DebounceTextBox.Text = netChange.DebounceSeconds.ToString();
            if (netChange.Condition is AdapterOfflineCondition cond)
            {
                ConditionAdapterComboBox.SelectedValue = FindMatchingAdapterId(ConditionAdapterComboBox.ItemsSource, cond.AdapterId);
            }
        }

        // Recovery Policy
        if (_rule.Recovery is { Enabled: true } rec)
        {
            RecoveryEnabledCheck.IsChecked = true;
            RecoveryPanel.IsEnabled = true;
            RecoveryDelayTextBox.Text = rec.DelayMinutes.ToString();
        }
        else
        {
            RecoveryEnabledCheck.IsChecked = false;
            RecoveryPanel.IsEnabled = false;
        }

        // Pre-Notifications
        if (_rule.PreNotifications != null && _rule.PreNotifications.Any())
        {
            var first = _rule.PreNotifications.First();
            PreNotifyEnabledCheck.IsChecked = true;
            PreNotifyPanel.IsEnabled = true;
            PreNotifyMinutesTextBox.Text = first.MinutesBefore.ToString();
            PreNotifyDelayTextBox.Text = first.DelayMinutes.ToString();
            AllowCancelCheck.IsChecked = first.AllowCancelOccurrence;
        }
        else
        {
            PreNotifyEnabledCheck.IsChecked = false;
            PreNotifyPanel.IsEnabled = false;
        }
    }

    private void TriggerTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OncePanel == null || DailyPanel == null || WeeklyPanel == null || NetworkChangePanel == null) return;

        var selectedIdx = TriggerTypeComboBox.SelectedIndex;
        OncePanel.Visibility = selectedIdx == 0 ? Visibility.Visible : Visibility.Collapsed;
        DailyPanel.Visibility = selectedIdx == 1 ? Visibility.Visible : Visibility.Collapsed;
        WeeklyPanel.Visibility = selectedIdx == 2 ? Visibility.Visible : Visibility.Collapsed;
        NetworkChangePanel.Visibility = selectedIdx == 3 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RecoveryCheckChanged(object sender, RoutedEventArgs e)
    {
        if (RecoveryPanel != null)
        {
            RecoveryPanel.IsEnabled = RecoveryEnabledCheck.IsChecked == true;
        }
    }

    private void PreNotifyCheckChanged(object sender, RoutedEventArgs e)
    {
        if (PreNotifyPanel != null)
        {
            PreNotifyPanel.IsEnabled = PreNotifyEnabledCheck.IsChecked == true;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        ErrorMsgText.Text = "";

        // 1. Validate Basic Info
        var name = RuleNameTextBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            ErrorMsgText.Text = "规则名称不能为空。";
            return;
        }

        var targetAdapterId = TargetAdapterComboBox.SelectedValue?.ToString();
        if (string.IsNullOrEmpty(targetAdapterId))
        {
            ErrorMsgText.Text = "请选择执行动作的目标网卡。";
            return;
        }

        // 2. Validate Cooldown Seconds
        if (!int.TryParse(CooldownTextBox.Text, out var cooldown) || cooldown < 0)
        {
            ErrorMsgText.Text = "冷却时间必须为非负整数。";
            return;
        }

        // 3. Construct Rule Trigger
        RuleTrigger trigger;
        var triggerTypeIndex = TriggerTypeComboBox.SelectedIndex;

        if (triggerTypeIndex == 0) // Once
        {
            if (OnceDatePicker.SelectedDate == null)
            {
                ErrorMsgText.Text = "请选择一次性规则的触发日期。";
                return;
            }
            if (!int.TryParse(OnceHourComboBox.SelectedItem?.ToString(), out var hr) ||
                !int.TryParse(OnceMinuteComboBox.SelectedItem?.ToString(), out var min))
            {
                ErrorMsgText.Text = "请设置合法的触发时间。";
                return;
            }

            var date = OnceDatePicker.SelectedDate.Value;
            var dt = new DateTime(date.Year, date.Month, date.Day, hr, min, 0, DateTimeKind.Local);
            var offsetTime = new DateTimeOffset(dt);
            
            if (offsetTime <= DateTimeOffset.Now && _rule == null)
            {
                ErrorMsgText.Text = "触发时间必须在未来。";
                return;
            }

            trigger = new RuleTrigger.Once(offsetTime);
        }
        else if (triggerTypeIndex == 1) // Daily
        {
            if (!int.TryParse(DailyHourComboBox.SelectedItem?.ToString(), out var hr) ||
                !int.TryParse(DailyMinuteComboBox.SelectedItem?.ToString(), out var min))
            {
                ErrorMsgText.Text = "请设置合法的触发时间。";
                return;
            }
            trigger = new RuleTrigger.Daily(new TimeOnly(hr, min));
        }
        else if (triggerTypeIndex == 2) // Weekly
        {
            if (!int.TryParse(WeeklyHourComboBox.SelectedItem?.ToString(), out var hr) ||
                !int.TryParse(WeeklyMinuteComboBox.SelectedItem?.ToString(), out var min))
            {
                ErrorMsgText.Text = "请设置合法的触发时间。";
                return;
            }

            var days = new HashSet<DayOfWeek>();
            if (MonCheck.IsChecked == true) days.Add(DayOfWeek.Monday);
            if (TueCheck.IsChecked == true) days.Add(DayOfWeek.Tuesday);
            if (WedCheck.IsChecked == true) days.Add(DayOfWeek.Wednesday);
            if (ThuCheck.IsChecked == true) days.Add(DayOfWeek.Thursday);
            if (FriCheck.IsChecked == true) days.Add(DayOfWeek.Friday);
            if (SatCheck.IsChecked == true) days.Add(DayOfWeek.Saturday);
            if (SunCheck.IsChecked == true) days.Add(DayOfWeek.Sunday);

            if (days.Count == 0)
            {
                ErrorMsgText.Text = "请至少选择每周触发的一个星期数。";
                return;
            }

            trigger = new RuleTrigger.Weekly(new TimeOnly(hr, min), days);
        }
        else if (triggerTypeIndex == 3) // NetworkChange
        {
            var condAdapterId = ConditionAdapterComboBox.SelectedValue?.ToString();
            if (string.IsNullOrEmpty(condAdapterId))
            {
                ErrorMsgText.Text = "请选择监听断开的探测网卡。";
                return;
            }
            if (!int.TryParse(DebounceTextBox.Text, out var debounce) || debounce < 1)
            {
                ErrorMsgText.Text = "离线防抖判定时间必须为大于0的整数。";
                return;
            }

            var condition = new AdapterOfflineCondition(condAdapterId);
            trigger = new RuleTrigger.NetworkChange(condition, debounce);
        }
        else
        {
            ErrorMsgText.Text = "未知的触发器类型。";
            return;
        }

        // 4. Construct Recovery Policy
        RecoveryPolicy? recovery = null;
        if (RecoveryEnabledCheck.IsChecked == true)
        {
            if (!int.TryParse(RecoveryDelayTextBox.Text, out var delayMins) || delayMins < 1)
            {
                ErrorMsgText.Text = "自动恢复延迟分钟数必须为大于0的整数。";
                return;
            }
            recovery = new RecoveryPolicy(true, delayMins);
        }

        // 5. Construct Pre-Notifications
        var preNotifications = new List<PreNotification>();
        if (PreNotifyEnabledCheck.IsChecked == true)
        {
            if (!int.TryParse(PreNotifyMinutesTextBox.Text, out var minutesBefore) || minutesBefore < 1)
            {
                ErrorMsgText.Text = "提前通知分钟数必须为大于0的整数。";
                return;
            }
            if (!int.TryParse(PreNotifyDelayTextBox.Text, out var delayMins) || delayMins < 1)
            {
                ErrorMsgText.Text = "预警延迟分钟数必须为大于0的整数。";
                return;
            }

            preNotifications.Add(new PreNotification(
                Guid.NewGuid(),
                minutesBefore,
                AllowDelay: true,
                DelayMinutes: delayMins,
                AllowCancelOccurrence: AllowCancelCheck.IsChecked == true
            ));
        }

        // 6. Build final Rule
        var action = ActionComboBox.SelectedIndex == 0 ? RuleAction.Disable : RuleAction.Enable;
        var ruleId = _rule?.Id ?? Guid.NewGuid();
        var enabled = _rule?.Enabled ?? true;

        ResultRule = new AutomationRule(
            ruleId,
            name,
            enabled,
            targetAdapterId,
            action,
            trigger,
            Conditions: new List<RuleCondition>(),
            preNotifications,
            recovery,
            RequireBackupCheck.IsChecked == true,
            cooldown
        );

        DialogResult = true;
        Close();
    }

    private static string? FindMatchingAdapterId(System.Collections.IEnumerable? itemsSource, string? id)
    {
        if (itemsSource is null || string.IsNullOrEmpty(id)) return id;
        foreach (var item in itemsSource)
        {
            if (item is NetworkAdapterInfo adapter && AdapterIdentity.AreEqual(adapter.Id, id))
            {
                return adapter.Id;
            }
        }
        return id;
    }
}
