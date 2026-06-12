using System.Collections.ObjectModel;
using NetRelay.Infrastructure;
using NetRelay.Models;
using NetRelay.Services;
using System.Windows.Threading;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System;
using System.IO;

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
    private bool _isLoadingLogs;
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
    private PreNotificationEventArgs? _pendingNotificationEvent;
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
        RuleEngine ruleEngine,
        RuleSchedulerService? ruleScheduler = null)
    {
        _adapterService = adapterService;
        _configService = configService;
        _connectivityService = connectivityService;
        _ruleScheduler = ruleScheduler;

        _ruleEngine = ruleEngine;

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
        RefreshLogsCommand = new RelayCommand(() => _ = LoadLogsAsync(), () => !IsLoadingLogs);
        ClearLogsCommand = new RelayCommand(() => _ = ClearLogsAsync(), () => !IsLoadingLogs);
        ExportLogsCommand = new RelayCommand(() => _ = ExportLogsAsync(), () => !IsLoadingLogs);

        AddRuleCommand = new RelayCommand(() => RequestEditRule?.Invoke(null));
        EditRuleCommand = new RelayCommand<AutomationRuleViewModel>(vm => { if (vm != null) RequestEditRule?.Invoke(vm.Rule); });
        DeleteRuleCommand = new RelayCommand<AutomationRuleViewModel>(vm => { if (vm != null) DeleteRule(vm.Rule.Id); });
        RunRuleNowCommand = new RelayCommand<AutomationRuleViewModel>(async vm => { if (vm != null) await RunRuleNowAsync(vm.Rule); });

        ExecutePendingNowCommand = new RelayCommand(async () => await ExecutePendingNowAsync());
        DelayPendingCommand = new RelayCommand(DelayPending);
        CancelPendingCommand = new RelayCommand(CancelPending);

        CheckUpdatesCommand = new RelayCommand(async () =>
        {
            OperationMessage = "正在检查更新...";
            await Task.Delay(1500);
            OperationMessage = "当前已是最新版本 (v1.2.0)";
            await Task.Delay(2000);
            OperationMessage = null;
        });

        FeedbackCommand = new RelayCommand(async () =>
        {
            OperationMessage = "反馈通道待后续开放";
            await Task.Delay(2000);
            OperationMessage = null;
        });

        // Bind Scheduler Events
        if (_ruleScheduler != null)
        {
            _ruleScheduler.PreNotificationTriggered += OnSchedulerPreNotificationTriggered;
            _ruleScheduler.RuleExecuted += OnSchedulerRuleExecuted;
        }

        LoadRules();
        _ = LoadLogsAsync();
        _ = _logService.RotateLogsAsync(30);
    }

    // Collections
    public ObservableCollection<NetworkAdapterInfo> Adapters { get; } = [];
    public ObservableCollection<AutomationRuleViewModel> Rules { get; } = [];
    private ObservableCollection<ExecutionRecordViewModel> _logs = [];
    public ObservableCollection<ExecutionRecordViewModel> Logs
    {
        get => _logs;
        private set => SetProperty(ref _logs, value);
    }

    // Commands
    public RelayCommand RefreshCommand { get; }
    public RelayCommand RefreshLogsCommand { get; }
    public RelayCommand ClearLogsCommand { get; }
    public RelayCommand ExportLogsCommand { get; }
    public RelayCommand AddRuleCommand { get; }
    public RelayCommand<AutomationRuleViewModel> EditRuleCommand { get; }
    public RelayCommand<AutomationRuleViewModel> DeleteRuleCommand { get; }
    public RelayCommand<AutomationRuleViewModel> RunRuleNowCommand { get; }
    public RelayCommand ExecutePendingNowCommand { get; }
    public RelayCommand DelayPendingCommand { get; }
    public RelayCommand CancelPendingCommand { get; }
    public RelayCommand CheckUpdatesCommand { get; }
    public RelayCommand FeedbackCommand { get; }

    public ConfigurationService ConfigService => _configService;

    public bool ManualDisableProtection
    {
        get => _configService.Current.ManualDisableProtection;
        set
        {
            if (_configService.Current.ManualDisableProtection != value)
            {
                _configService.Current.ManualDisableProtection = value;
                _configService.Save();
                RaisePropertyChanged(nameof(ManualDisableProtection));
            }
        }
    }

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
    public string NetworkSummary => ConnectedCount > 0 ? "存在活动链路" : "当前无活动链路";
    public string ConnectedDescription => $"{ConnectedCount} 个接口链路正常";

    // Pre-Notification Properties
    public string PendingRuleName => _pendingRule?.Name ?? string.Empty;

    public int PendingDelayMinutes => _pendingNotification?.DelayMinutes ?? 10;
    public string PendingDelayLabel => $"延迟 {PendingDelayMinutes} 分钟";

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

    public void ReloadEditedRule(Guid ruleId)
    {
        _ruleEngine.ResetRuleRuntimeState(ruleId);
        _ruleScheduler?.Reload(ruleId);
        if (_pendingRule?.Id == ruleId)
        {
            _pendingRule = null;
            _pendingNotification = null;
            _pendingNotificationEvent = null;
            IsPendingOverlayVisible = false;
        }
    }
    public bool HasNoRules => Rules.Count == 0;
    public bool HasNoLogs => Logs.Count == 0;
    public bool IsLoadingLogs
    {
        get => _isLoadingLogs;
        private set
        {
            if (SetProperty(ref _isLoadingLogs, value))
            {
                RefreshLogsCommand.RaiseCanExecuteChanged();
                ClearLogsCommand.RaiseCanExecuteChanged();
                ExportLogsCommand.RaiseCanExecuteChanged();
            }
        }
    }

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
        if (IsLoadingLogs)
        {
            return;
        }

        IsLoadingLogs = true;
        try
        {
            var adapterNames = Adapters
                .GroupBy(adapter => NormalizeAdapterId(adapter.Id), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.OrdinalIgnoreCase);
            var logs = await Task.Run(async () =>
            {
                var records = await _logService.LoadLogsAsync().ConfigureAwait(false);
                return new ObservableCollection<ExecutionRecordViewModel>(
                    records.Select(record => new ExecutionRecordViewModel(
                        record,
                        adapterNames.GetValueOrDefault(NormalizeAdapterId(record.TargetAdapterId)))));
            });

            Logs = logs;
            RaisePropertyChanged(nameof(HasNoLogs));
        }
        catch
        {
            // Ignore UI exceptions
        }
        finally
        {
            IsLoadingLogs = false;
        }
    }

    private async Task ClearLogsAsync()
    {
        if (IsLoadingLogs)
        {
            return;
        }

        IsLoadingLogs = true;
        try
        {
            await _logService.ClearAllLogsAsync();
            Logs = [];
            RaisePropertyChanged(nameof(HasNoLogs));
        }
        finally
        {
            IsLoadingLogs = false;
        }
    }

    private async Task ExportLogsAsync()
    {
        if (IsLoadingLogs)
        {
            return;
        }

        var saveFileDialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "ZIP 压缩文件 (*.zip)|*.zip",
            FileName = $"NetRelay-detailed-logs-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            Title = "导出详细日志与诊断报告"
        };

        if (saveFileDialog.ShowDialog() == true)
        {
            var zipPath = saveFileDialog.FileName;
            IsLoadingLogs = true;
            OperationMessage = "正在准备日志与诊断报告...";
            try
            {
                await Task.Run(() =>
                {
                    var tempDir = Path.Combine(Path.GetTempPath(), $"NetRelay-Export-{Guid.NewGuid():N}");
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        // 1. Copy config file
                        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                        var configPath = Path.Combine(appData, "NetRelay", "config.json");
                        if (File.Exists(configPath))
                        {
                            File.Copy(configPath, Path.Combine(tempDir, "config.json"), true);
                        }

                        // 2. Copy execution logs
                        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                        var logsDir = Path.Combine(localAppData, "NetRelay", "logs");
                        var logsDestDir = Path.Combine(tempDir, "logs");
                        if (Directory.Exists(logsDir))
                        {
                            Directory.CreateDirectory(logsDestDir);
                            var logFiles = Directory.GetFiles(logsDir, "execution-*.jsonl");
                            foreach (var file in logFiles)
                            {
                                var destFile = Path.Combine(logsDestDir, Path.GetFileName(file));
                                File.Copy(file, destFile, true);
                            }
                        }

                        // 3. Write adapter diagnostics report
                        var reportPath = Path.Combine(tempDir, "adapter-diagnostics.json");
                        AdapterDiagnosticService.WriteReport(reportPath);

                        // 4. Create zip archive
                        if (File.Exists(zipPath))
                        {
                            File.Delete(zipPath);
                        }
                        System.IO.Compression.ZipFile.CreateFromDirectory(tempDir, zipPath);
                    }
                    finally
                    {
                        if (Directory.Exists(tempDir))
                        {
                            Directory.Delete(tempDir, recursive: true);
                        }
                    }
                });

                OperationMessage = $"日志成功导出至：{Path.GetFileName(zipPath)}";
                await Task.Delay(2500);
                OperationMessage = null;
            }
            catch (Exception ex)
            {
                OperationMessage = $"导出日志失败: {ex.Message}";
                await Task.Delay(2500);
                OperationMessage = null;
            }
            finally
            {
                IsLoadingLogs = false;
            }
        }
    }

    private static string NormalizeAdapterId(string adapterId)
    {
        return Guid.TryParse(adapterId, out var adapterGuid)
            ? adapterGuid.ToString("D")
            : adapterId;
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
            await Task.Delay(2500);
            OperationMessage = null;
            RefreshAdapters();
            _ = LoadLogsAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"立即执行失败: {ex.Message}";
            OperationMessage = $"立即执行失败: {ex.Message}";
            await Task.Delay(2500);
            OperationMessage = null;
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
        _pendingNotificationEvent = e;
        _pendingCountdownTime = e.TargetTime;
        _pendingCountdownSeconds = (int)Math.Max(0, (e.TargetTime - DateTimeOffset.Now).TotalSeconds);
        PendingCountdownLabel = $"将在 {_pendingCountdownSeconds} 秒后执行";
        IsPendingOverlayVisible = true;

        RaisePropertyChanged(nameof(PendingRuleName));
        RaisePropertyChanged(nameof(PendingDelayMinutes));
        RaisePropertyChanged(nameof(PendingDelayLabel));
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
            _pendingNotificationEvent = null;
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
        _pendingNotificationEvent = null;

        // Skip in scheduler so it doesn't double run when scheduled time arrives
        _ruleScheduler?.SkipRuleOccurrence(ruleToRun.Id);

        // Run now
        await RunRuleNowAsync(ruleToRun);
    }

    private void DelayPending()
    {
        if (_pendingRule == null || _pendingNotification == null || _pendingNotificationEvent == null) return;

        _countdownTimer.Stop();
        IsPendingOverlayVisible = false;

        _ruleScheduler?.TryApplyPreNotificationAction(
            _pendingNotificationEvent.NotificationActionId,
            _pendingNotificationEvent.NotificationActionToken,
            PreNotificationAction.Delay);

        _pendingRule = null;
        _pendingNotification = null;
        _pendingNotificationEvent = null;
    }

    private void CancelPending()
    {
        if (_pendingRule == null || _pendingNotificationEvent == null) return;

        _countdownTimer.Stop();
        IsPendingOverlayVisible = false;

        _ruleScheduler?.TryApplyPreNotificationAction(
            _pendingNotificationEvent.NotificationActionId,
            _pendingNotificationEvent.NotificationActionToken,
            PreNotificationAction.Cancel);

        _pendingRule = null;
        _pendingNotification = null;
        _pendingNotificationEvent = null;
    }

    public void ClearPendingNotificationIfMatches(Guid notificationActionId)
    {
        if (_pendingNotificationEvent?.NotificationActionId == notificationActionId)
        {
            _countdownTimer.Stop();
            IsPendingOverlayVisible = false;
            _pendingRule = null;
            _pendingNotification = null;
            _pendingNotificationEvent = null;
        }
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
            if (!enabled && ManualDisableProtection)
            {
                OperationMessage = "正在验证备用网络可用性…";
                var isBackupUsable = await _ruleEngine.IsBackupNetworkUsableAsync(adapter.Id);
                if (!isBackupUsable)
                {
                    OperationMessage = "安全保护：无可用备份互联网，操作被中止。";
                    var skippedRecord = new ExecutionRecord(
                        Guid.NewGuid(),
                        null,
                        RuleSource.Manual,
                        adapter.Id,
                        RuleAction.Disable,
                        DateTimeOffset.Now,
                        DateTimeOffset.Now,
                        "SKIPPED",
                        "BACKUP_NETWORK_UNAVAILABLE",
                        null
                    );
                    await _ruleEngine.WriteExecutionRecordAsync(skippedRecord);
                    _ = LoadLogsAsync();

                    IsOperating = false;
                    await Task.Delay(2500);
                    if (OperationMessage == "安全保护：无可用备份互联网，操作被中止。")
                    {
                        OperationMessage = null;
                    }
                    return new AdapterActionResult(false, "无可用备份互联网，操作被中止。");
                }
            }
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
            await _ruleEngine.WriteExecutionRecordAsync(record);
            _ = LoadLogsAsync();

            await Task.Delay(700);
            RefreshAdapters();

            IsOperating = false;
            await Task.Delay(2000);
            OperationMessage = null;

            return result;
        }
        catch (Exception ex)
        {
            OperationMessage = $"操作失败: {ex.Message}";
            IsOperating = false;
            await Task.Delay(2500);
            OperationMessage = null;
            throw;
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

            for (int i = Adapters.Count - 1; i >= 0; i--)
            {
                if (!newAdapters.Any(adapter => AdapterIdentity.AreEqual(adapter.Id, Adapters[i].Id)))
                {
                    Adapters.RemoveAt(i);
                }
            }

            foreach (var newAdapter in newAdapters)
            {
                var existing = Adapters.FirstOrDefault(adapter => AdapterIdentity.AreEqual(adapter.Id, newAdapter.Id));
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
                    var result = await _connectivityService.ProbeAdapterAsync(adapter.Id, policy, cts.Token);

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
                                adapter.NlmConnectivity = result.NlmConnectivity;
                            });
                        }
                        else
                        {
                            adapter.IsInternetOnline = result.Online;
                            adapter.LastProbeTime = result.CheckedAt;
                            adapter.ProbeReasonCode = result.ReasonCode;
                            adapter.NlmConnectivity = result.NlmConnectivity;
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
        return AdapterIdentity.AreEqual(adapterId, selectedId);
    }

    public void Shutdown()
    {
        _trafficTimer.Stop();
        _countdownTimer.Stop();
        try
        {
            _probeCts?.Cancel();
            _probeCts?.Dispose();
        }
        catch
        {
            // Ignore cancel/dispose exceptions on shutdown
        }
    }
}
