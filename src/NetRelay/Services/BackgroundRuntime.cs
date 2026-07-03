using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NetRelay.Contracts;
using NetRelay.Models;
using WpfApplication = System.Windows.Application;

namespace NetRelay.Services;

public sealed class BackgroundRuntime : IDisposable
{
    private readonly ConfigurationService _configService;
    private readonly ConnectivityService _connectivityService;
    private readonly NativeNetworkConnectionService _connectionService;
    private readonly RuleEngine _ruleEngine;
    private readonly RuleSchedulerService _ruleScheduler;
    private readonly LogService _logService;
    private readonly DiagnosticsBundleService _diagnosticsBundleService = new();
    private readonly UpdateService _updateService;
    private readonly TrayIconService _trayIcon;
    private bool _startupUpdateCheckStarted;
    private bool _disposed;
    private MainWindow? _mainWindow;

    public BackgroundRuntime(ConfigurationService configService)
    {
        _configService = configService;
        _connectivityService = new ConnectivityService();
        _connectionService = new NativeNetworkConnectionService();
        _ruleEngine = new RuleEngine(_connectionService, _connectivityService, _configService);
        _ruleScheduler = new RuleSchedulerService(_ruleEngine, _configService, _connectivityService);
        _logService = new LogService();
        _updateService = new UpdateService(_configService);
        _trayIcon = new TrayIconService();

        _trayIcon.OpenRequested += (_, _) => ShowMainWindow();
        _trayIcon.CheckUpdatesRequested += (_, _) => ShowMainWindow(tabIndex: 3, checkUpdates: true);
        _trayIcon.ExportDiagnosticsRequested += async (_, _) => await ExportDiagnosticsAsync();
        _trayIcon.OpenLogsRequested += (_, _) => _diagnosticsBundleService.OpenLogsDirectory();
        _trayIcon.OpenUpdateCacheRequested += (_, _) => _diagnosticsBundleService.OpenUpdateCacheDirectory();
        _trayIcon.ExitRequested += (_, _) => ShutdownApplication();

        _ruleScheduler.PreNotificationTriggered += OnSchedulerPreNotificationTriggered;
        _ruleEngine.ExecutionRecorded += OnRuleExecutionRecorded;
    }

    public void Start()
    {
        RichToastService.Initialize();
        _ruleScheduler.Start();
        if (!_configService.IsAutomationEnabled)
        {
            _trayIcon.ShowBalloonTip(
                8000,
                "NetRelay 自动化已暂停",
                _configService.AutomationDisabledReason ?? "探测配置无效，请检查配置文件。",
                System.Windows.Forms.ToolTipIcon.Warning);
        }
    }

    public async Task CheckForUpdatesOnStartupOnceAsync()
    {
        if (_startupUpdateCheckStarted
            || !_configService.Current.AutoCheckUpdatesOnStartup
            || (App.PolicyService?.IsBlocked == true && App.PolicyService?.AllowUpdate == false))
        {
            return;
        }

        _startupUpdateCheckStarted = true;
        try
        {
            var manifest = await _updateService.CheckForUpdatesAsync(Protocol.ProductVersion, CancellationToken.None);
            if (manifest is not null)
            {
                ShowMainWindow(tabIndex: 3, checkUpdates: true);
            }
        }
        catch (Exception exception)
        {
            await new DiagnosticLogService().ErrorAsync("startup", "update-check", exception);
        }
    }

    public void HandleCommandLineArgs(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--protocol-launch", StringComparison.OrdinalIgnoreCase)
                && index + 1 < args.Length)
            {
                HandleProtocolAction(args[index + 1]);
                return;
            }
        }

        if (args.Any(arg => string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        ShowMainWindow();
    }

    public void ShowMainWindow(int? tabIndex = null, bool checkUpdates = false)
    {
        if (_disposed)
        {
            return;
        }

        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow(
                _configService,
                _connectionService,
                _connectivityService,
                _ruleEngine,
                _ruleScheduler,
                _logService,
                ownsRuntime: false);
            var window = _mainWindow;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_mainWindow, window))
                {
                    _mainWindow = null;
                }

                if (ReferenceEquals(WpfApplication.Current.MainWindow, window))
                {
                    WpfApplication.Current.MainWindow = null;
                }

                _ = Task.Run(() => ReleaseUiMemory("main-window-closed"));
            };
            WpfApplication.Current.MainWindow = window;
        }

        if (tabIndex.HasValue)
        {
            _mainWindow.SelectTab(tabIndex.Value);
        }

        _mainWindow.RestoreWindow();

        if (checkUpdates)
        {
            _mainWindow.CheckUpdatesInteractive();
        }
    }

    public void ShowBlockedDialogIfNeeded()
    {
        if (App.PolicyService?.IsBlocked != true)
        {
            return;
        }

        ShowBlockedDialog();
    }

    public void ShowBlockedDialog()
    {
        var owner = _mainWindow is { IsVisible: true } ? _mainWindow : null;
        var dialog = new Dialogs.BlockWarningDialog(App.PolicyService);
        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
    }

    public void HandleProtocolAction(string rawUrl)
    {
        try
        {
            if (!NotificationProtocolActivation.TryParse(rawUrl, out var activation) || activation is null)
            {
                return;
            }

            var result = _ruleScheduler.TryApplyPreNotificationAction(
                activation.NotificationId,
                activation.Token,
                activation.Action);
            if (!result.Applied)
            {
                return;
            }

            _mainWindow?.ClearPendingNotificationIfMatches(activation.NotificationId);
        }
        catch
        {
            // Fail gracefully.
        }
    }

    public void ShutdownApplication()
    {
        if (_disposed)
        {
            return;
        }

        _mainWindow?.ForceCloseFromRuntime();
        WpfApplication.Current.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ruleScheduler.PreNotificationTriggered -= OnSchedulerPreNotificationTriggered;
        _ruleEngine.ExecutionRecorded -= OnRuleExecutionRecorded;
        _ruleScheduler.Stop();
        _ruleScheduler.Dispose();
        _trayIcon.Dispose();
    }

    private async Task ExportDiagnosticsAsync()
    {
        var saveFileDialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "ZIP 压缩文件 (*.zip)|*.zip",
            FileName = $"NetRelay-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            Title = "导出诊断包"
        };

        if (saveFileDialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await _diagnosticsBundleService.ExportBundleAsync(
                saveFileDialog.FileName,
                _configService.Current,
                _configService.IsAutomationEnabled,
                _configService.AutomationDisabledReason,
                _updateService.GetStatusSnapshot(),
                mainWindowCreated: _mainWindow is not null,
                CancellationToken.None);

            _trayIcon.ShowBalloonTip(
                4000,
                "NetRelay 诊断包已导出",
                Path.GetFileName(saveFileDialog.FileName),
                System.Windows.Forms.ToolTipIcon.Info);
        }
        catch (Exception exception)
        {
            await new DiagnosticLogService().ErrorAsync("diagnostics", "export-bundle", exception);
            _trayIcon.ShowBalloonTip(
                5000,
                "NetRelay 诊断包导出失败",
                exception.Message,
                System.Windows.Forms.ToolTipIcon.Error);
        }
    }

    private void OnSchedulerPreNotificationTriggered(object? sender, PreNotificationEventArgs e)
    {
        WpfApplication.Current.Dispatcher.Invoke(() =>
        {
            var actionName = e.Rule.Action == RuleAction.Enable ? "启用" : "禁用";
            RichToastService.ShowPreNotification(e, actionName);
        });
    }

    private void OnRuleExecutionRecorded(object? sender, ExecutionRecord record)
    {
        if (record.Source == RuleSource.Manual)
        {
            return;
        }

        WpfApplication.Current.Dispatcher.Invoke(() =>
        {
            var title = record.Outcome switch
            {
                "SUCCESS" when record.Source == RuleSource.Recovery => "NetRelay 自动恢复完成",
                "SUCCESS" => "NetRelay 自动操作完成",
                "SKIPPED" => "NetRelay 自动操作已跳过",
                _ => "NetRelay 自动操作失败"
            };
            var message = record.ReasonCode switch
            {
                "OK" => "网卡操作已成功完成。",
                "BACKUP_NETWORK_UNAVAILABLE" => "未找到可联网的备用网卡，已取消禁用操作。",
                "POST_SWITCH_VALIDATION_FAILED" => "切换后备用网络失效，已执行安全回滚。",
                "ROLLBACK_SUCCEEDED" => "目标网卡已重新启用。",
                "ROLLBACK_FAILED" => "安全回滚失败，请手动重新启用目标网卡。",
                "RECOVERY_ALREADY_ENABLED" => "目标网卡已经启用，无需重复恢复。",
                "CONFIG_INVALID" => "探测配置无效，自动化规则已暂停。",
                "CONDITION_NOT_MET" => "规则附加条件不满足，本次操作已跳过。",
                _ => $"执行结果：{record.ReasonCode}"
            };

            _trayIcon.ShowBalloonTip(6000, title, message, GetNotificationIcon(record.Outcome));
        });
    }

    private static System.Windows.Forms.ToolTipIcon GetNotificationIcon(string outcome)
    {
        return outcome switch
        {
            "SUCCESS" => System.Windows.Forms.ToolTipIcon.Info,
            "SKIPPED" => System.Windows.Forms.ToolTipIcon.Warning,
            _ => System.Windows.Forms.ToolTipIcon.Error
        };
    }

    private static void ReleaseUiMemory(string reason)
    {
        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

            using var process = Process.GetCurrentProcess();
            _ = EmptyWorkingSet(process.Handle);
            _ = new DiagnosticLogService().InfoAsync(
                "memory",
                "release-ui",
                "completed",
                detail: $"reason={reason}; workingSet={process.WorkingSet64}; privateMemory={process.PrivateMemorySize64}; managedHeap={GC.GetTotalMemory(false)}");
        }
        catch (Exception exception)
        {
            _ = new DiagnosticLogService().ErrorAsync("memory", "release-ui", exception);
        }
    }

    [DllImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr processHandle);
}
