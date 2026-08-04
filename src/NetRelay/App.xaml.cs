using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using NetRelay.Infrastructure;
using NetRelay.Services;
using NetRelay.Dialogs;

namespace NetRelay;

public partial class App : System.Windows.Application
{
    private static int _fatalStartupFailureReported;
    private SingleInstanceService? _singleInstanceService;
    private BackgroundRuntime? _runtime;
    private OmnexaControlRuntime? _omnexaRuntime;
    public static PolicyService PolicyService { get; private set; } = null!;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            OnStartupCore(e);
        }
        catch (Exception exception)
        {
            ReportFatalStartupFailure(exception);
            Shutdown(1);
        }
    }

    private void OnStartupCore(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var startupArgumentSummary = SummarizeStartupArguments(e.Args);
        var startupDiagnostics = new DiagnosticLogService();
        _ = startupDiagnostics.InfoAsync(
            "startup",
            "application",
            "started",
            detail: $"version={NetRelay.Contracts.Protocol.ProductVersion}; args={e.Args.Length}; knownArgs={startupArgumentSummary}");
        SmoothScrollBehavior.Enable();

        if (TryConfigureAutoStart(e.Args, out var autoStartExitCode))
        {
            Shutdown(autoStartExitCode);
            return;
        }

        if (TryRunUpdateVerification(e.Args, out var verifyExitCode))
        {
            Shutdown(verifyExitCode);
            return;
        }

        if (TryRunAdapterDiagnostic(e.Args, out var diagnosticExitCode))
        {
            Shutdown(diagnosticExitCode);
            return;
        }

        _singleInstanceService = new SingleInstanceService(GetInstanceScopeName());
        _ = startupDiagnostics.InfoAsync("startup", "single-instance", "checking");
        if (!_singleInstanceService.TryAcquire())
        {
            _ = startupDiagnostics.InfoAsync("startup", "single-instance", "secondary");
            _singleInstanceService.SignalPrimaryInstance(e.Args);
            Shutdown();
            return;
        }
        _ = startupDiagnostics.InfoAsync("startup", "single-instance", "primary");

        // 隐私与首次运行激活检查
        var configService = new ConfigurationService();
        PolicyService = new PolicyService(configService);
        var config = configService.Current;
        var activationService = new ActivationService(configService);
        var completedInitialActivation = false;
        _ = startupDiagnostics.InfoAsync(
            "startup",
            "configuration",
            "loaded",
            detail: $"schema={config.SchemaVersion}; privacyAccepted={config.PrivacyConsentAccepted}");

        if (!config.PrivacyConsentAccepted)
        {
            var consentDialog = new Dialogs.PrivacyConsentDialog();
            if (consentDialog.ShowDialog() != true)
            {
                Shutdown();
                return;
            }

            var activated = false;
            try
            {
                _ = startupDiagnostics.InfoAsync("startup", "activation", "started");
                activated = Task.Run(
                        () => activationService.ActivateAsync(
                            CancellationToken.None))
                    .WaitAsync(TimeSpan.FromSeconds(45))
                    .GetAwaiter()
                    .GetResult();
                _ = startupDiagnostics.InfoAsync(
                    "startup",
                    "activation",
                    activated ? "completed" : "rejected");
            }
            catch (Exception ex)
            {
                _ = startupDiagnostics.ErrorAsync(
                    "startup",
                    "activation",
                    ex);
                ModernMessageBox.Show(
                    $"设备激活过程中发生异常：{ex.InnerException?.Message ?? ex.Message}",
                    "NetRelay 激活失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            if (!activated)
            {
                ModernMessageBox.Show(
                    BuildActivationFailureMessage(activationService),
                    "NetRelay 激活失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Shutdown();
                return;
            }

            completedInitialActivation = true;
        }
        else
        {
            _ = startupDiagnostics.InfoAsync("startup", "activation-cache", "checking");
            bool hasCachedActivation;
            try
            {
                hasCachedActivation = Task.Run(
                        () => activationService.HasCachedActivationAsync(
                            CancellationToken.None))
                    .WaitAsync(TimeSpan.FromSeconds(10))
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exception)
            {
                _ = startupDiagnostics.ErrorAsync(
                    "startup",
                    "activation-cache",
                    exception);
                ModernMessageBox.Show(
                    $"无法读取 Omnexa 激活缓存：{exception.InnerException?.Message ?? exception.Message}",
                    "NetRelay 启动失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown();
                return;
            }

            _ = startupDiagnostics.InfoAsync(
                "startup",
                "activation-cache",
                hasCachedActivation ? "available" : "missing");
            if (!hasCachedActivation)
            {
            var activated = false;
            try
            {
                _ = startupDiagnostics.InfoAsync("startup", "activation", "started");
                activated = Task.Run(
                        () => activationService.ActivateAsync(
                            CancellationToken.None))
                    .WaitAsync(TimeSpan.FromSeconds(45))
                    .GetAwaiter()
                    .GetResult();
                _ = startupDiagnostics.InfoAsync(
                    "startup",
                    "activation",
                    activated ? "completed" : "rejected");
            }
            catch (Exception ex)
            {
                _ = startupDiagnostics.ErrorAsync(
                    "startup",
                    "activation",
                    ex);
                ModernMessageBox.Show(
                    $"设备激活过程中发生异常：{ex.InnerException?.Message ?? ex.Message}",
                    "NetRelay 激活失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            if (!activated)
            {
                ModernMessageBox.Show(
                    BuildActivationFailureMessage(activationService),
                    "NetRelay 激活失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Shutdown();
                return;
            }

            completedInitialActivation = true;
            }
            else
            {
                // 已激活，后台发送心跳
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await activationService.SendHeartbeatAsync(CancellationToken.None);
                    }
                    catch {}
                });
            }
        }

        try
        {
            _ = startupDiagnostics.InfoAsync("startup", "control-sync", "started");
            Task.Run(
                    () => PolicyService.CheckPolicyAsync(
                        CancellationToken.None))
                .WaitAsync(TimeSpan.FromSeconds(60))
                .GetAwaiter()
                .GetResult();
            _ = startupDiagnostics.InfoAsync("startup", "control-sync", "completed");
        }
        catch (Exception exception)
        {
            _ = startupDiagnostics.ErrorAsync(
                "startup",
                "control-sync",
                exception);
            ModernMessageBox.Show(
                $"无法取得可信的 Omnexa 云控策略：{exception.InnerException?.Message ?? exception.Message}\n\n" +
                "请检查网络后重试。若此前已有有效签名缓存，客户端会自动使用缓存和离线宽限。",
                "NetRelay 云控同步失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        _runtime = new BackgroundRuntime(configService);
        _runtime.Start();
        _ = startupDiagnostics.InfoAsync("startup", "runtime", "started");
        PolicyService.BlockStateChanged += (_, _) =>
        {
            if (!PolicyService.IsBlocked || _runtime is null)
            {
                return;
            }

            _ = Dispatcher.InvokeAsync(() =>
            {
                if (!Current.Windows.OfType<Dialogs.BlockWarningDialog>().Any())
                {
                    _runtime.ShowBlockedDialog();
                }
            });
        };
        _omnexaRuntime = new OmnexaControlRuntime(
            PolicyService,
            activationService);
        _omnexaRuntime.Start();
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _singleInstanceService.StartListening(args => Dispatcher.Invoke(() => _runtime.HandleCommandLineArgs(args)));
        var startInTray = e.Args.Any(arg => string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase));
        if (e.Args.Length > 0)
        {
            _runtime.HandleCommandLineArgs(e.Args);
        }
        if (completedInitialActivation)
        {
            _runtime.ShowMainWindow();
        }
        else if (startInTray)
        {
            _ = _runtime.CheckForUpdatesOnStartupOnceAsync();
        }
        else if (e.Args.Length == 0)
        {
            _runtime.ShowMainWindow();
        }

        _runtime.ShowBlockedDialogIfNeeded();

        // Check and display active announcements asynchronously
        _ = Task.Run(async () =>
        {
            try
            {
                var announcementService = new AnnouncementService(configService);
                await announcementService.CheckAndDisplayAnnouncementsAsync(CancellationToken.None);
            }
            catch
            {
                // Ignore unexpected issues to protect main app startup
            }
        });
    }

    private static string BuildActivationFailureMessage(
        ActivationService activationService)
    {
        var code = string.IsNullOrWhiteSpace(activationService.LastFailureCode)
            ? "UNKNOWN"
            : activationService.LastFailureCode;
        var message = string.IsNullOrWhiteSpace(activationService.LastFailureMessage)
            ? "请检查网络连接并重试。"
            : activationService.LastFailureMessage;
        return $"首次使用需要完成 Omnexa 激活。\n\n错误码：{code}\n{message}";
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _ = new DiagnosticLogService().InfoAsync("startup", "application", "exited", detail: $"exitCode={e.ApplicationExitCode}");
        _omnexaRuntime?.Dispose();
        _runtime?.Dispose();
        _singleInstanceService?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        ReportFatalStartupFailure(e.Exception);
        e.Handled = true;
        Shutdown(1);
    }

    private static void OnAppDomainUnhandledException(
        object sender,
        UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            ReportFatalStartupFailure(exception);
        }
    }

    private static void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        _ = new DiagnosticLogService().ErrorAsync(
            "runtime",
            "unobserved-task",
            e.Exception);
        e.SetObserved();
    }

    private static void ReportFatalStartupFailure(Exception exception)
    {
        if (Interlocked.Exchange(ref _fatalStartupFailureReported, 1) != 0)
        {
            return;
        }

        var rootCause = exception is AggregateException { InnerException: not null }
            ? exception.InnerException
            : exception;
        try
        {
            new DiagnosticLogService()
                .ErrorAsync("startup", "unhandled-exception", rootCause)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            // The native error box below must still be shown if diagnostics cannot be written.
        }

        try
        {
            System.Windows.MessageBox.Show(
                $"NetRelay 启动失败：{rootCause.Message}\n\n" +
                "已记录启动诊断。请将此提示或诊断文件发送给管理员。",
                "NetRelay 启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // Nothing reliable remains if the desktop message loop itself is unavailable.
        }
    }

    private static string GetInstanceScopeName()
    {
        var userSid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        return $"NetRelay-{userSid}";
    }

    private static string SummarizeStartupArguments(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return "none";
        }

        var known = new List<string>();
        foreach (var arg in args)
        {
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            known.Add(arg switch
            {
                "--startup" => "--startup",
                "--configure-autostart" => "--configure-autostart",
                "--verify-update" => "--verify-update",
                "--diagnose-adapters" => "--diagnose-adapters",
                "--protocol-launch" => "--protocol-launch",
                "--quiet" => "--quiet",
                _ => "--unknown"
            });
        }

        return known.Count == 0 ? "none" : string.Join(",", known.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static bool TryRunAdapterDiagnostic(IReadOnlyList<string> args, out int exitCode)
    {
        exitCode = 0;
        var diagnosticIndex = -1;
        for (var index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], "--diagnose-adapters", StringComparison.OrdinalIgnoreCase))
            {
                diagnosticIndex = index;
                break;
            }
        }

        if (diagnosticIndex < 0)
        {
            return false;
        }

        var quiet = args.Any(arg => string.Equals(arg, "--quiet", StringComparison.OrdinalIgnoreCase));
        var requestedPath = diagnosticIndex + 1 < args.Count
            && !args[diagnosticIndex + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[diagnosticIndex + 1]
                : null;
        try
        {
            var reportPath = AdapterDiagnosticService.WriteReport(requestedPath);
            if (!quiet)
            {
                ModernMessageBox.Show(
                    $"只读网卡诊断报告已生成：\n{reportPath}",
                    "NetRelay 网卡诊断",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception exception)
        {
            exitCode = 1;
            if (!quiet)
            {
                ModernMessageBox.Show(
                    $"生成只读网卡诊断报告失败：\n{exception.Message}",
                    "NetRelay 网卡诊断",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        return true;
    }

    private static bool TryRunUpdateVerification(IReadOnlyList<string> args, out int exitCode)
    {
        exitCode = 0;
        var verifyIndex = -1;
        for (var index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], "--verify-update", StringComparison.OrdinalIgnoreCase))
            {
                verifyIndex = index;
                break;
            }
        }

        if (verifyIndex < 0)
        {
            return false;
        }

        try
        {
            var configService = new ConfigurationService();
            _ = configService.Current;
            exitCode = 0;
        }
        catch
        {
            exitCode = 1;
        }

        return true;
    }

    private static bool TryConfigureAutoStart(IReadOnlyList<string> args, out int exitCode)
    {
        exitCode = 0;
        var optionIndex = -1;
        for (var index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], "--configure-autostart", StringComparison.OrdinalIgnoreCase))
            {
                optionIndex = index;
                break;
            }
        }

        if (optionIndex < 0)
        {
            return false;
        }

        if (optionIndex + 1 >= args.Count
            || !bool.TryParse(args[optionIndex + 1], out var enabled))
        {
            exitCode = 1;
            return true;
        }

        var result = new AutoStartService().SetEnabled(enabled);
        if (!result.Success)
        {
            exitCode = 2;
            return true;
        }

        try
        {
            var configService = new ConfigurationService();
            configService.Current.AutoStart = enabled;
            configService.Save();
        }
        catch
        {
            exitCode = 3;
        }

        return true;
    }
}
