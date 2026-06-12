using System.Collections.ObjectModel;
using NetRelay.Infrastructure;
using NetRelay.Models;
using NetRelay.Services;
using System.Windows.Threading;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System;

namespace NetRelay.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly NetworkAdapterService _adapterService;
    private readonly ConfigurationService _configService;
    private readonly ConnectivityService _connectivityService;
    private readonly RuleSchedulerService? _ruleScheduler;
    private readonly LogService _logService = new();
    private readonly RuleEngine _ruleEngine;

    private NetworkAdapterInfo? _selectedAdapter;
    private string? _errorMessage;
    private bool _isLoading;
    private bool _isOperating;
    private string? _operationMessage;
    private readonly DispatcherTimer _trafficTimer;
    private readonly DispatcherTimer _countdownTimer;
    private CancellationTokenSource? _probeCts;
    private int _probeTickCount = 0;
    private bool _isProbing;

    // Navigation and Page Tabs
    private int _currentTabIndex = 0;

    // Pre-Notification Countdown Overlay
    private AutomationRule? _pendingRule;
    private PreNotification? _pendingNotification;
    private DateTimeOffset _pendingCountdownTime;
    private int _pendingCountdownSeconds;
    private string _pendingCountdownLabel = string.Empty;
    private bool _isPendingOverlayVisible;

    // Events
    public event Action<AutomationRule?>? RequestEditRule;

    public MainViewModel(
        NetworkAdapterService adapterService,
        ConfigurationService configService,
        ConnectivityService connectivityService,
        RuleSchedulerService? ruleScheduler = null)
    {
        _adapterService = adapterService;
        _configService = configService;
        _connectivityService = connectivityService;
        _ruleScheduler = ruleScheduler;
        
        // Retrieve RuleEngine from scheduler if possible, or instantiate a transient one
        _ruleEngine = new RuleEngine(adapterService.ConnectionService, connectivityService, configService);

        RefreshCommand = new RelayCommand(RefreshAdapters, () => !IsLoading);
        RefreshAdapters();

        // 1s Traffic Sampling Timer
        _trafficTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _trafficTimer.Tick += (_, _) => SampleTraffic();
        _trafficTimer.Start();

        // 1s Countdown Timer for Pre-Notifications
        _countdownTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _countdownTimer.Tick += CountdownTick;

        // Commands
        RefreshLogsCommand = new RelayCommand(async () => await LoadLogsAsync());
        ClearLogsCommand = new RelayCommand(async () => await ClearLogsAsync());
        
        AddRuleCommand = new RelayCommand(() => RequestEditRule?.Invoke(null));
        EditRuleCommand = new RelayCommand<AutomationRuleViewModel>(vm => { if (vm != null) RequestEditRule?.Invoke(vm.Rule); });
        DeleteRuleCommand = new RelayCommand<AutomationRuleViewModel>(vm => { if (vm != null) DeleteRule(vm.Rule.Id); });
        RunRuleNowCommand = new RelayCommand<AutomationRuleViewModel>(async vm => { if (vm != null) await RunRuleNowAsync(vm.Rule); });

        ExecutePendingNowCommand = new RelayCommand(async () => await ExecutePendingNowAsync());
        DelayPendingCommand = new RelayCommand(DelayPending);
        CancelPendingCommand = new RelayCommand(CancelPending);

        // Bind Scheduler Events
        if (_ruleScheduler != null)
        {
            _ruleScheduler.PreNotificationTriggered += OnSchedulerPreNotificationTriggered;
            _ruleScheduler.RuleExecuted += OnSchedulerRuleExecuted;
        }

        LoadRules();
        _ = LoadLogsAsync();
    }

    // Collections
    public ObservableCollection<NetworkAdapterInfo> Adapters { get; } = [];
    public ObservableCollection<AutomationRuleViewModel> Rules { get; } = [];
    public ObservableCollection<ExecutionRecordViewModel> Logs { get; } = [];

    // Commands
    public RelayCommand RefreshCommand { get; }
    public RelayCommand RefreshLogsCommand { get; }
    public RelayCommand ClearLogsCommand { get; }
    public RelayCommand AddRuleCommand { get; }
    public RelayCommand<AutomationRuleViewModel> EditRuleCommand { get; }
    public RelayCommand<AutomationRuleViewModel> DeleteRuleCommand { get; }
    public RelayCommand<AutomationRuleViewModel> RunRuleNowCommand { get; }
    public RelayCommand ExecutePendingNowCommand { get; }
    public RelayCommand DelayPendingCommand { get; }
    public RelayCommand CancelPendingCommand { get; }

    public ConfigurationService ConfigService => _configService;

    public int CurrentTabIndex
    {
        get => _currentTabIndex;
        set => SetProperty(ref _currentTabIndex, value);
    }

    public NetworkAdapterInfo? SelectedAdapter
    {
        get => _selectedAdapter;
        set
        {
            if (SetProperty(ref _selectedAdapter, value))
            {
                RaisePropertyChanged(nameof(CanOperateSelectedAdapter));
                TriggerAllAdaptersProbe(force: true);
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                RaisePropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasOperationMessage => !string.IsNullOrWhiteSpace(OperationMessage);
    public bool CanOperateSelectedAdapter => SelectedAdapter?.CanToggle == true && !IsOperating;

    public string? OperationMessage
    {
        get => _operationMessage;
        private set
        {
            if (SetProperty(ref _operationMessage, value))
            {
                RaisePropertyChanged(nameof(HasOperationMessage));
            }
        }
    }

    public bool IsOperating
    {
        get => _isOperating;
        private set
        {
            if (SetProperty(ref _isOperating, value))
            {
                RaisePropertyChanged(nameof(CanOperateSelectedAdapter));
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public int ConnectedCount => Adapters.Count(adapter => adapter.IsConnected);
    public string NetworkSummary => ConnectedCount > 0 ? "网络已连接" : "当前无连接";
    public string ConnectedDescription => $"{ConnectedCount} 个接口处于连接状态";

    // Pre-Notification Properties
    public string PendingRuleName => _pendingRule?.Name ?? string.Empty;

    public int PendingDelayMinutes => _pendingNotification?.DelayMinutes ?? 10;

    public bool AllowDelay => _pendingNotification?.AllowDelay ?? true;

    public bool AllowCancel => _pendingNotification?.AllowCancelOccurrence ?? true;

    public string PendingCountdownLabel
    {
        get => _pendingCountdownLabel;
        private set => SetProperty(ref _pendingCountdownLabel, value);
    }

    public bool IsPendingOverlayVisible
    {
        get => _isPendingOverlayVisible;
        private set => SetProperty(ref _isPendingOverlayVisible, value);
    }

    public void ReloadRules()
    {
        _ruleScheduler?.Reload();
    }
    public bool HasNoRules => Rules.Count == 0;
    public bool HasNoLogs => Logs.Count == 0;

    public void LoadRules()
    {
        Rules.Clear();
        foreach (var rule in _configService.Current.Rules)
        {
            Rules.Add(new AutomationRuleViewModel(rule, _configService, _adapterService.ConnectionService, ReloadRules));
        }
        RaisePropertyChanged(nameof(HasNoRules));
    }

    public async Task LoadLogsAsync()
    {
        try
        {
            var records = await _logService.LoadLogsAsync();
            Logs.Clear();
            foreach (var r in records)
            {
                Logs.Add(new ExecutionRecordViewModel(r, _adapterService.ConnectionService));
            }
            RaisePropertyChanged(nameof(HasNoLogs));
        }
        catch
        {
            // Ignore UI exceptions
        }
    }

    private async Task ClearLogsAsync()
    {
        await _logService.ClearAllLogsAsync();
        Logs.Clear();
        RaisePropertyChanged(nameof(HasNoLogs));
    }
    private void DeleteRule(Guid ruleId)
    {
        var currentRules = _configService.Current.Rules;
        var ruleIndex = currentRules.FindIndex(r => r.Id == ruleId);
        if (ruleIndex >= 0)
        {
            currentRules.RemoveAt(ruleIndex);
            _configService.Save();
            LoadRules();
            ReloadRules();
        }
    }

    private async Task RunRuleNowAsync(AutomationRule rule)
    {
        IsOperating = true;
        OperationMessage = $"正在立即执行规则“{rule.Name}”…";
        try
        {
            var record = await _ruleEngine.ExecuteRuleAsync(rule, RuleSource.Manual);
            OperationMessage = record.Outcome == "SUCCESS" ? "规则执行成功" : $"规则未成功完成 ({record.ReasonCode})";
            await Task.Delay(1000);
            OperationMessage = null;
            RefreshAdapters();
            _ = LoadLogsAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"立即执行失败: {ex.Message}";
        }
        finally
        {
            IsOperating = false;
        }
    }

    // Pre-Notification Interactive Handlers
    private void OnSchedulerPreNotificationTriggered(object? sender, PreNotificationEventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null)
        {
            dispatcher.Invoke(() => TriggerPendingRule(e));
        }
        else
        {
            TriggerPendingRule(e);
        }
    }

    private void OnSchedulerRuleExecuted(object? sender, ExecutionRecord record)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null)
        {
            dispatcher.Invoke(() =>
            {
                _ = LoadLogsAsync();
                LoadRules(); // Update "Once" rules enabled state from config
            });
        }
        else
        {
            _ = LoadLogsAsync();
            LoadRules();
        }
    }

    private void TriggerPendingRule(PreNotificationEventArgs e)
    {
        _pendingRule = e.Rule;
        _pendingNotification = e.Notification;
        _pendingCountdownTime = e.TargetTime;
        _pendingCountdownSeconds = (int)Math.Max(0, (e.TargetTime - DateTimeOffset.Now).TotalSeconds);
        PendingCountdownLabel = $"将在 {_pendingCountdownSeconds} 秒后执行";
        IsPendingOverlayVisible = true;
        
        RaisePropertyChanged(nameof(PendingRuleName));
        RaisePropertyChanged(nameof(PendingDelayMinutes));
        RaisePropertyChanged(nameof(AllowDelay));
        RaisePropertyChanged(nameof(AllowCancel));

        _countdownTimer.Start();
    }

    private void CountdownTick(object? sender, EventArgs e)
    {
        var secs = (int)Math.Max(0, (_pendingCountdownTime - DateTimeOffset.Now).TotalSeconds);
        _pendingCountdownSeconds = secs;
        PendingCountdownLabel = $"将在 {secs} 秒后执行";

        if (secs <= 0)
        {
            _countdownTimer.Stop();
            IsPendingOverlayVisible = false;
            _pendingRule = null;
            _pendingNotification = null;
        }
    }

    private async Task ExecutePendingNowAsync()
    {
        if (_pendingRule == null) return;
        
        _countdownTimer.Stop();
        IsPendingOverlayVisible = false;

        var ruleToRun = _pendingRule;
        _pendingRule = null;
        _pendingNotification = null;

        // Skip in scheduler so it doesn't double run when scheduled time arrives
        _ruleScheduler?.SkipRuleOccurrence(ruleToRun.Id);

        // Run now
        await RunRuleNowAsync(ruleToRun);
    }

    private void DelayPending()
    {
        if (_pendingRule == null || _pendingNotification == null) return;

        _countdownTimer.Stop();
        IsPendingOverlayVisible = false;

        _ruleScheduler?.DelayRule(_pendingRule.Id, TimeSpan.FromMinutes(_pendingNotification.DelayMinutes));

        _pendingRule = null;
        _pendingNotification = null;
    }

    private void CancelPending()
    {
        if (_pendingRule == null) return;

        _countdownTimer.Stop();
        IsPendingOverlayVisible = false;

        _ruleScheduler?.SkipRuleOccurrence(_pendingRule.Id);

        _pendingRule = null;
        _pendingNotification = null;
    }

    public async Task<AdapterActionResult> SetSelectedAdapterEnabledAsync(
        NativeNetworkConnectionService connectionService,
        bool enabled)
    {
        if (IsOperating)
        {
            return new AdapterActionResult(false, "已有网卡操作正在执行，请稍候。");
        }

        var adapter = SelectedAdapter;
        if (adapter is null)
        {
            return new AdapterActionResult(false, "请先选择目标网卡。");
        }

        if (!adapter.CanToggle)
        {
            return new AdapterActionResult(false, $"“{adapter.Name}”不是可控制的 Windows 网络连接。");
        }

        IsOperating = true;
        OperationMessage = enabled ? $"正在启用“{adapter.Name}”…" : $"正在禁用“{adapter.Name}”…";
        try
        {
            var result = await Task.Run(() => connectionService.SetEnabled(adapter.Id, adapter.Name, enabled));
            OperationMessage = result.Message;
            
            // Record manual execution
            var record = new ExecutionRecord(
                Guid.NewGuid(),
                null,
                RuleSource.Manual,
                adapter.Id,
                enabled ? RuleAction.Enable : RuleAction.Disable,
                DateTimeOffset.Now,
                DateTimeOffset.Now,
                result.Success ? "SUCCESS" : "FAILED",
                result.Success ? "OK" : "ADAPTER_OPERATION_FAILED",
                result.WindowsErrorCode
            );
            await RuleEngine.WriteExecutionRecordAsync(record);
            _ = LoadLogsAsync();

            await Task.Delay(700);
            RefreshAdapters();
            return result;
        }
        finally
        {
            IsOperating = false;
        }
    }

    private void RefreshAdapters()
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var selectedId = SelectedAdapter?.Id;
            var newAdapters = _adapterService.GetAdapters();

            var newIds = newAdapters.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (int i = Adapters.Count - 1; i >= 0; i--)
            {
                if (!newIds.Contains(Adapters[i].Id))
                {
                    Adapters.RemoveAt(i);
                }
            }

            foreach (var newAdapter in newAdapters)
            {
                var existing = Adapters.FirstOrDefault(a => string.Equals(a.Id, newAdapter.Id, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    Adapters.Add(newAdapter);
                }
                else
                {
                    existing.IsEnabled = newAdapter.IsEnabled;
                    existing.OperationalStatus = newAdapter.OperationalStatus;
                    existing.Speed = newAdapter.Speed;
                    existing.MacAddress = newAdapter.MacAddress;
                    existing.IpAddresses = newAdapter.IpAddresses;
                    existing.ClassificationLabel = newAdapter.ClassificationLabel;
                    existing.CanToggle = newAdapter.CanToggle;
                }
            }

            SelectedAdapter = Adapters.FirstOrDefault(adapter => AdapterIdsEqual(adapter.Id, selectedId))
                ?? Adapters.FirstOrDefault();
            _adapterService.UpdateTraffic(Adapters);
            RaisePropertyChanged(nameof(ConnectedCount));
            RaisePropertyChanged(nameof(NetworkSummary));
            RaisePropertyChanged(nameof(ConnectedDescription));

            SortAdapters();
            TriggerAllAdaptersProbe(force: true);
        }
        catch (Exception exception)
        {
            ErrorMessage = $"读取网卡失败：{exception.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void SampleTraffic()
    {
        try
        {
            _adapterService.UpdateTraffic(Adapters);
            SortAdapters();

            _probeTickCount++;
            if (_probeTickCount >= 5)
            {
                _probeTickCount = 0;
                TriggerAllAdaptersProbe(force: false);
            }
        }
        catch
        {
        }
    }

    private async void TriggerAllAdaptersProbe(bool force = false)
    {
        if (_isProbing && !force)
        {
            return;
        }

        _probeCts?.Cancel();
        var cts = new CancellationTokenSource();
        _probeCts = cts;
        _isProbing = true;

        var targetAdapters = Adapters.ToList();
        if (targetAdapters.Count == 0)
        {
            _isProbing = false;
            return;
        }

        try
        {
            await Task.Delay(250, cts.Token);

            var policy = _configService.Current.ProbePolicy;

            var tasks = targetAdapters.Select(async adapter =>
            {
                try
                {
                    var result = await _connectivityService.ProbeAdapterAsync(adapter.Id, policy);

                    if (!cts.IsCancellationRequested)
                    {
                        var dispatcher = System.Windows.Application.Current?.Dispatcher;
                        if (dispatcher != null)
                        {
                            dispatcher.Invoke(() =>
                            {
                                adapter.IsInternetOnline = result.Online;
                                adapter.LastProbeTime = result.CheckedAt;
                                adapter.ProbeReasonCode = result.ReasonCode;
                            });
                        }
                        else
                        {
                            adapter.IsInternetOnline = result.Online;
                            adapter.LastProbeTime = result.CheckedAt;
                            adapter.ProbeReasonCode = result.ReasonCode;
                        }
                    }
                }
                catch
                {
                    // 忽略单个网卡探测的异常，避免影响其他网卡
                }
            });

            await Task.WhenAll(tasks);

            if (!cts.IsCancellationRequested)
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher != null)
                {
                    dispatcher.Invoke(() => SortAdapters());
                }
                else
                {
                    SortAdapters();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ErrorMessage = $"探测发生异常：{exception.Message}\n{exception.StackTrace}";
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                _isProbing = false;
            }
        }
    }

    private void SortAdapters()
    {
        var sorted = Adapters
            .OrderBy(GetSortOrder)
            .ThenBy(a => a.IsLikelyVirtual)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        for (int i = 0; i < sorted.Count; i++)
        {
            var currentIndex = Adapters.IndexOf(sorted[i]);
            if (currentIndex != i)
            {
                Adapters.Move(currentIndex, i);
            }
        }
    }

    private static int GetSortOrder(NetworkAdapterInfo adapter)
    {
        if (adapter.IsConnected)
        {
            if (adapter.IsInternetOnline) return 0;
            if (adapter.HasTraffic) return 1;
            return 2;
        }

        if (adapter.IsEnabled)
        {
            return 3;
        }

        bool hasRecentTraffic = adapter.TrafficHistory.Any(t => t > 0);
        if (hasRecentTraffic) return 4;
        return 5;
    }

    private static bool AdapterIdsEqual(string adapterId, string? selectedId)
    {
        if (selectedId is null)
        {
            return false;
        }

        return Guid.TryParse(adapterId, out var adapterGuid) && Guid.TryParse(selectedId, out var selectedGuid)
            ? adapterGuid == selectedGuid
            : string.Equals(adapterId, selectedId, StringComparison.OrdinalIgnoreCase);
    }
}
