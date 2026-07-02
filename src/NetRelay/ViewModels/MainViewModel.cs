using System.Collections.ObjectModel;
using NetRelay.Contracts;
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
    private readonly LogService _logService;
    private readonly RuleEngine _ruleEngine;
    private readonly UpdateService _updateService;
    private readonly DiagnosticsBundleService _diagnosticsBundleService;

    private NetworkAdapterInfo? _selectedAdapter;
    private string? _errorMessage;
    private bool _isLoading;
    private bool _isOperating;
    private bool _isLoadingLogs;
    private bool _isUpdating;
    private UpdateManifest? _pendingUpdateManifest;
    private bool _activeUpdateWasMandatory;
    private string? _operationMessage;
    private UpdateStatusSnapshot _updateStatus = UpdateStatusSnapshot.Empty;
    private readonly DispatcherTimer _trafficTimer;
    private readonly DispatcherTimer _countdownTimer;
    private CancellationTokenSource? _probeCts;
    private int _probeTickCount = 0;
    private bool _isProbing;
    private bool _logsLoaded;

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
        RuleSchedulerService? ruleScheduler = null,
        LogService? logService = null)
    {
        _adapterService = adapterService;
        _configService = configService;
        _connectivityService = connectivityService;
        _ruleScheduler = ruleScheduler;
        _logService = logService ?? new LogService();
        _diagnosticsBundleService = new DiagnosticsBundleService();

        _ruleEngine = ruleEngine;

        RefreshCommand = new RelayCommand(RefreshAdapters, () => !IsLoading);
        RefreshAdapters();

        // 1s Traffic Sampling Timer
        _trafficTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _trafficTimer.Tick += (_, _) => SampleTraffic();

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

        _updateService = new UpdateService(_configService);
        _updateStatus = _updateService.GetStatusSnapshot();
        _updateService.StatusChanged += (_, snapshot) =>
        {
            _updateStatus = snapshot;
            RaiseUpdateStatusProperties();
        };

        CheckUpdatesCommand = new RelayCommand(async () => await CheckForUpdatesInteractiveAsync(), () => !_isUpdating);

        FeedbackCommand = new RelayCommand(() =>
        {
            var dialog = new NetRelay.Dialogs.FeedbackDialog
            {
                Owner = System.Windows.Application.Current.MainWindow
            };
            dialog.ShowDialog();
        });
        UpdateHistoryCommand = new RelayCommand(async () =>
        {
            try
            {
                var items = await _updateService.GetHistoryAsync(CancellationToken.None);
                new NetRelay.Dialogs.UpdateHistoryDialog(items)
                {
                    Owner = System.Windows.Application.Current.MainWindow
                }.ShowDialog();
            }
            catch (Exception exception)
            {
                await new DiagnosticLogService().ErrorAsync("update", "history-dialog", exception);
                NetRelay.Dialogs.ModernMessageBox.Show("无法加载更新历史，请稍后重试。", "更新历史", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
        });
        HelpCommand = new RelayCommand(() =>
        {
            new NetRelay.Dialogs.HelpDialog
            {
                Owner = System.Windows.Application.Current.MainWindow
            }.ShowDialog();
        });

        // Bind Scheduler Events
        if (_ruleScheduler != null)
        {
            _ruleScheduler.PreNotificationTriggered += OnSchedulerPreNotificationTriggered;
            _ruleScheduler.RuleExecuted += OnSchedulerRuleExecuted;
        }

        if (App.PolicyService != null)
        {
            App.PolicyService.BlockStateChanged += (sender, args) =>
            {
                RaisePropertyChanged(nameof(IsBlocked));
                RaisePropertyChanged(nameof(IsNotBlocked));
                RaisePropertyChanged(nameof(BlockedReason));
                RaisePropertyChanged(nameof(BlockedExpiryText));
                RaisePropertyChanged(nameof(CanOperateSelectedAdapter));
            };
        }

        LoadRules();
        _ = _logService.RotateLogsAsync(_configService.Current.KeepDays);
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
    public RelayCommand UpdateHistoryCommand { get; }
    public RelayCommand HelpCommand { get; }

    public async Task CheckForUpdatesOnStartupAsync()
    {
        if (_isUpdating
            || !_configService.Current.AutoCheckUpdatesOnStartup
            || (App.PolicyService?.IsBlocked == true && App.PolicyService?.AllowUpdate == false))
        {
            return;
        }

        try
        {
            var manifest = await _updateService.CheckForUpdatesAsync(Protocol.ProductVersion, CancellationToken.None);
            if (manifest is null)
            {
                return;
            }

            _pendingUpdateManifest = manifest;
            CheckUpdatesCommand.Execute(null);
        }
        catch (Exception exception)
        {
            await new DiagnosticLogService().ErrorAsync("startup", "update-check", exception);
        }
    }

    public ConfigurationService ConfigService => _configService;
    public UpdateStatusSnapshot UpdateStatusSnapshot => _updateStatus;
    public string ProductVersionText => $"版本：v{Protocol.ProductVersion}";
    public string UpdatePrimarySourceText => "主更新源";
    public string UpdateFallbackSourceText => _configService.Current.GithubFallback.Enabled ? "备用源" : "备用源未启用";
    public string UpdateStartupPolicyText => _configService.Current.AutoCheckUpdatesOnStartup ? "启动时自动静默检查" : "启动时不自动检查";
    public string UpdateLastCheckText => _updateStatus.LastCheckedAt.HasValue
        ? _updateStatus.LastCheckedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        : "尚未检查";
    public string UpdateLastCheckOutcomeText => _updateStatus.LastCheckMessage;
    public string UpdateLastCheckSourceText => _updateStatus.LastCheckSource switch
    {
        UpdateSourceKind.Primary => "最近检查来源：主更新源",
        UpdateSourceKind.GitHubFallback => "最近检查来源：备用源",
        _ => "最近检查来源：尚未确定"
    };
    public string UpdateLastDownloadText => _updateStatus.LastDownloadAt.HasValue
        ? $"{_updateStatus.LastDownloadAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {_updateStatus.LastDownloadMessage}"
        : "尚未下载更新";
    public string UpdateLastAvailableVersionText => string.IsNullOrWhiteSpace(_updateStatus.AvailableVersion)
        ? "暂无候选更新"
        : $"最近发现版本：v{_updateStatus.AvailableVersion}";
    public string AutomationStatusText => _configService.IsAutomationEnabled
        ? $"自动化规则已启用，共 {_configService.Current.Rules.Count} 条"
        : $"自动化规则已暂停：{_configService.AutomationDisabledReason ?? "探测配置无效"}";

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
    public bool CanOperateSelectedAdapter => SelectedAdapter?.CanToggle == true && !IsOperating && App.PolicyService?.IsBlocked != true;

    public bool IsBlocked => App.PolicyService?.IsBlocked == true;
    public bool IsNotBlocked => !IsBlocked;
    public string? BlockedReason => App.PolicyService?.Reason;
    public string BlockedExpiryText => App.PolicyService?.ExpiresAt.HasValue == true
        ? App.PolicyService.ExpiresAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        : "永久封锁";

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

    public void NotifySettingsChanged()
    {
        RaisePropertyChanged(nameof(UpdatePrimarySourceText));
        RaisePropertyChanged(nameof(UpdateFallbackSourceText));
        RaisePropertyChanged(nameof(UpdateStartupPolicyText));
        RaisePropertyChanged(nameof(AutomationStatusText));
    }

    public Task ExportDiagnosticsAsync() => ExportLogsAsync();

    public void OpenLogsDirectory() => _diagnosticsBundleService.OpenLogsDirectory();

    public void OpenUpdateCacheDirectory() => _diagnosticsBundleService.OpenUpdateCacheDirectory();

    public async Task ClearUpdateCacheAsync()
    {
        try
        {
            await Task.Run(() => _diagnosticsBundleService.ClearUpdateCache());
            OperationMessage = "已尝试清理更新缓存目录。";
            await Task.Delay(2200);
            OperationMessage = null;
        }
        catch (Exception exception)
        {
            await new DiagnosticLogService().ErrorAsync("diagnostics", "clear-update-cache", exception);
            OperationMessage = $"清理更新缓存失败: {exception.Message}";
            await Task.Delay(2600);
            OperationMessage = null;
        }
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

        _logsLoaded = true;
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

    private void ReloadLogsIfLoaded()
    {
        if (_logsLoaded)
        {
            _ = LoadLogsAsync();
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
            FileName = $"NetRelay-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            Title = "导出诊断包"
        };

        if (saveFileDialog.ShowDialog() == true)
        {
            var zipPath = saveFileDialog.FileName;
            IsLoadingLogs = true;
            OperationMessage = "正在准备诊断包...";
            try
            {
                await _diagnosticsBundleService.ExportBundleAsync(
                    zipPath,
                    _configService.Current,
                    _configService.IsAutomationEnabled,
                    _configService.AutomationDisabledReason,
                    _updateStatus,
                    CancellationToken.None);

                OperationMessage = $"诊断包已导出：{Path.GetFileName(zipPath)}";
                await Task.Delay(2500);
                OperationMessage = null;
            }
            catch (Exception ex)
            {
                await new DiagnosticLogService().ErrorAsync("diagnostics", "export-bundle", ex);
                OperationMessage = $"导出诊断包失败: {ex.Message}";
                await Task.Delay(2500);
                OperationMessage = null;
            }
            finally
            {
                IsLoadingLogs = false;
            }
        }
    }

    private async Task CheckForUpdatesInteractiveAsync()
    {
        if (_isUpdating)
        {
            return;
        }

        _isUpdating = true;
        CheckUpdatesCommand?.RaiseCanExecuteChanged();
        if (App.PolicyService?.IsBlocked == true && App.PolicyService?.AllowUpdate == false)
        {
            OperationMessage = "更新功能在该受限状态下已被系统管理员禁用。";
            await Task.Delay(2550);
            OperationMessage = null;
            _isUpdating = false;
            CheckUpdatesCommand?.RaiseCanExecuteChanged();
            return;
        }

        try
        {
            OperationMessage = "正在检查更新...";
            var manifest = _pendingUpdateManifest ?? await _updateService.CheckForUpdatesAsync(Protocol.ProductVersion, CancellationToken.None);
            _pendingUpdateManifest = null;
            if (manifest == null)
            {
                OperationMessage = $"当前已是最新版本 (v{Protocol.ProductVersion})";
                await Task.Delay(2000);
                OperationMessage = null;
                return;
            }

            _activeUpdateWasMandatory = manifest.IsMandatory;
            var updaterPath = TryResolveUpdaterPath(out var updaterDirectory);
            var targetDir = AppDomain.CurrentDomain.BaseDirectory;
            var preflight = EvaluateUpdatePreflight(manifest, updaterPath, targetDir);
            OperationMessage = null;

            var updateDialog = new NetRelay.Dialogs.UpdateAvailableDialog(manifest, preflight);
            var ownerWindow = System.Windows.Application.Current.MainWindow;
            if (ownerWindow?.IsVisible == true)
            {
                updateDialog.Owner = ownerWindow;
            }
            else
            {
                updateDialog.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
            }
            if (updateDialog.ShowDialog() != true)
            {
                return;
            }

            if (!preflight.Success)
            {
                throw new InvalidOperationException(preflight.Summary);
            }

            OperationMessage = $"正在从{manifest.SourceLabel}下载更新包...";
            var downloadedPackage = await _updateService.DownloadPackageAsync(manifest, progress =>
            {
                var percentText = progress.Percent.HasValue ? $" {progress.Percent:P0}" : string.Empty;
                var sizeText = progress.TotalBytes.HasValue && progress.TotalBytes.Value > 0
                    ? $" · {FormatBytes(progress.BytesReceived)} / {FormatBytes(progress.TotalBytes.Value)}"
                    : string.Empty;
                OperationMessage = $"{progress.Message}{percentText}{sizeText}";
            }, CancellationToken.None);

            OperationMessage = preflight.RequiresElevation
                ? "下载完成，正在请求管理员权限启动更新器..."
                : "下载完成，正在启动更新器并退出应用...";
            await Task.Delay(1200);

            var updaterRunDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NetRelay",
                "updates",
                "updater-run");
            Directory.CreateDirectory(updaterRunDir);
            foreach (var sourcePath in Directory.EnumerateFiles(updaterDirectory, "NetRelay.Updater*"))
            {
                File.Copy(sourcePath, Path.Combine(updaterRunDir, Path.GetFileName(sourcePath)), overwrite: true);
            }

            var contractsPath = Path.Combine(updaterDirectory, "NetRelay.Contracts.dll");
            if (File.Exists(contractsPath))
            {
                File.Copy(contractsPath, Path.Combine(updaterRunDir, Path.GetFileName(contractsPath)), overwrite: true);
            }

            var runtimeUpdaterPath = Path.Combine(updaterRunDir, "NetRelay.Updater.exe");
            var parentPid = System.Diagnostics.Process.GetCurrentProcess().Id;

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = runtimeUpdaterPath,
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add("--package");
            startInfo.ArgumentList.Add(downloadedPackage.PackagePath);
            startInfo.ArgumentList.Add("--manifest");
            startInfo.ArgumentList.Add(downloadedPackage.ManifestPath);
            startInfo.ArgumentList.Add("--target-dir");
            startInfo.ArgumentList.Add(targetDir);
            startInfo.ArgumentList.Add("--parent-pid");
            startInfo.ArgumentList.Add(parentPid.ToString());
            startInfo.ArgumentList.Add("--executable");
            startInfo.ArgumentList.Add("NetRelay.exe");

            if (preflight.RequiresElevation)
            {
                startInfo.Verb = "runas";
            }

            System.Diagnostics.Process.Start(startInfo);
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            await new DiagnosticLogService().ErrorAsync("update", "apply", ex);
            OperationMessage = $"更新失败: {ex.Message}";
            await Task.Delay(3200);
            OperationMessage = null;
            if (_activeUpdateWasMandatory)
            {
                NetRelay.Dialogs.ModernMessageBox.Show("强制更新未完成，应用将退出。请检查网络、磁盘空间和权限后重新启动并重试。", "必须更新", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                System.Windows.Application.Current.Shutdown();
            }
        }
        finally
        {
            _activeUpdateWasMandatory = false;
            _isUpdating = false;
            CheckUpdatesCommand?.RaiseCanExecuteChanged();
        }
    }

    private static string TryResolveUpdaterPath(out string updaterDirectory)
    {
        updaterDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var updaterPath = Path.Combine(updaterDirectory, "NetRelay.Updater.exe");
        return updaterPath;
    }

    private UpdatePreflightResult EvaluateUpdatePreflight(UpdateManifest manifest, string updaterPath, string targetDirectory)
    {
        var issues = new List<string>();
        var requiredBytes = Math.Max(manifest.PackageSize * 2, 256L * 1024 * 1024);
        var availableBytes = GetMinimumAvailableBytes(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetRelay", "updates"),
            targetDirectory);
        var requiresElevation = !IsDirectoryWritable(targetDirectory);

        if (!Directory.Exists(targetDirectory))
        {
            issues.Add("安装目录不存在，无法覆盖当前程序。");
        }

        if (!File.Exists(updaterPath))
        {
            issues.Add("缺少 NetRelay.Updater.exe，无法执行自更新。");
        }

        if (availableBytes.HasValue && availableBytes.Value < requiredBytes)
        {
            issues.Add($"可用磁盘空间不足，至少需要 {FormatBytes(requiredBytes)}。");
        }

        if (requiresElevation)
        {
            issues.Add("当前安装目录需要管理员权限，启动更新器时会弹出 UAC 确认。");
        }

        var summary = issues.Count == 0
            ? $"已通过预检，可从{manifest.SourceLabel}下载安装包。"
            : string.Join(" ", issues);

        return new UpdatePreflightResult(
            issues.All(issue => issue.Contains("管理员权限", StringComparison.Ordinal)),
            requiresElevation,
            summary,
            issues,
            requiredBytes,
            availableBytes,
            updaterPath,
            targetDirectory);
    }

    private static long? GetMinimumAvailableBytes(params string[] paths)
    {
        long? min = null;
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                var drive = new DriveInfo(root);
                min = min.HasValue ? Math.Min(min.Value, drive.AvailableFreeSpace) : drive.AvailableFreeSpace;
            }
            catch
            {
                // Ignore unknown roots.
            }
        }

        return min;
    }

    private void RaiseUpdateStatusProperties()
    {
        RaisePropertyChanged(nameof(UpdateLastCheckText));
        RaisePropertyChanged(nameof(UpdateLastCheckOutcomeText));
        RaisePropertyChanged(nameof(UpdateLastCheckSourceText));
        RaisePropertyChanged(nameof(UpdateLastDownloadText));
        RaisePropertyChanged(nameof(UpdateLastAvailableVersionText));
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.##} {units[unitIndex]}";
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
        if (App.PolicyService?.IsBlocked == true)
        {
            ErrorMessage = "当前处于受限模式，无法执行规则。";
            OperationMessage = "当前处于受限模式，无法执行规则。";
            await Task.Delay(2550);
            OperationMessage = null;
            return;
        }

        IsOperating = true;
        OperationMessage = $"正在立即执行规则“{rule.Name}”…";
        try
        {
            var record = await _ruleEngine.ExecuteRuleAsync(rule, RuleSource.Manual);
            OperationMessage = record.Outcome == "SUCCESS" ? "规则执行成功" : $"规则未成功完成 ({record.ReasonCode})";
            await Task.Delay(2500);
            OperationMessage = null;
            RefreshAdapters();
            ReloadLogsIfLoaded();
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
                ReloadLogsIfLoaded();
                LoadRules(); // Update "Once" rules enabled state from config
            });
        }
        else
        {
            ReloadLogsIfLoaded();
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
        if (App.PolicyService?.IsBlocked == true)
        {
            return new AdapterActionResult(false, "当前处于受限模式，无法启用或禁用网卡。");
        }

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
                    ReloadLogsIfLoaded();

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
            ReloadLogsIfLoaded();

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
        PauseUiMonitoring();
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

    public void ResumeUiMonitoring()
    {
        if (!_trafficTimer.IsEnabled)
        {
            _trafficTimer.Start();
            RefreshAdapters();
        }
    }

    public void PauseUiMonitoring()
    {
        _trafficTimer.Stop();
        try
        {
            _probeCts?.Cancel();
        }
        catch
        {
            // Ignore cancellation failures while hiding the UI.
        }
    }

    private static bool IsDirectoryWritable(string directoryPath)
    {
        try
        {
            var tempFile = Path.Combine(directoryPath, Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(tempFile, "test");
            File.Delete(tempFile);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
