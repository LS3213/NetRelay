using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using NetRelay.Infrastructure;
using NetRelay.Services;
using NetRelay.Dialogs;

namespace NetRelay;

public partial class App : System.Windows.Application
{
    private SingleInstanceService? _singleInstanceService;
    public static PolicyService PolicyService { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _ = new DiagnosticLogService().InfoAsync("startup", "application", "started", detail: $"version={NetRelay.Contracts.Protocol.ProductVersion}; args={e.Args.Length}");
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
        if (!_singleInstanceService.TryAcquire())
        {
            _singleInstanceService.SignalPrimaryInstance(e.Args);
            Shutdown();
            return;
        }

        // 隐私与首次运行激活检查
        var configService = new ConfigurationService();
        PolicyService = new PolicyService(configService);
        var config = configService.Current;
        var activationService = new ActivationService(configService);

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
                activated = Task.Run(async () => await activationService.ActivateAsync(CancellationToken.None)).Result;
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(
                    $"设备激活过程中发生异常：{ex.InnerException?.Message ?? ex.Message}",
                    "NetRelay 激活失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            if (!activated)
            {
                ModernMessageBox.Show(
                    "首次使用需要联网激活，请检查您的网络连接并重试。",
                    "NetRelay 激活失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Shutdown();
                return;
            }
        }
        else if (string.IsNullOrWhiteSpace(config.ActivationReceipt))
        {
            var activated = false;
            try
            {
                activated = Task.Run(async () => await activationService.ActivateAsync(CancellationToken.None)).Result;
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(
                    $"设备激活过程中发生异常：{ex.InnerException?.Message ?? ex.Message}",
                    "NetRelay 激活失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            if (!activated)
            {
                ModernMessageBox.Show(
                    "首次使用需要联网激活，请检查您的网络连接并重试。",
                    "NetRelay 激活失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Shutdown();
                return;
            }
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

        var mainWindow = new MainWindow(configService);
        MainWindow = mainWindow;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        _singleInstanceService.StartListening(args => Dispatcher.Invoke(() => mainWindow.HandleCommandLineArgs(args)));
        var startInTray = e.Args.Any(arg => string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase));
        if (e.Args.Length > 0)
        {
            mainWindow.HandleCommandLineArgs(e.Args);
        }
        if (startInTray)
        {
            _ = mainWindow.CheckForUpdatesOnStartupOnceAsync();
        }
        else
        {
            mainWindow.Show();
        }

        if (PolicyService.IsBlocked)
        {
            var dialog = new Dialogs.BlockWarningDialog(PolicyService)
            {
                Owner = mainWindow
            };
            dialog.ShowDialog();
        }

        // Asynchronously check online policy
        _ = Task.Run(async () =>
        {
            try
            {
                await PolicyService.CheckPolicyAsync(CancellationToken.None);
                if (PolicyService.IsBlocked)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        var alreadyOpen = false;
                        foreach (Window win in Current.Windows)
                        {
                            if (win is Dialogs.BlockWarningDialog)
                            {
                                alreadyOpen = true;
                                break;
                            }
                        }

                        if (!alreadyOpen)
                        {
                            var dialog = new Dialogs.BlockWarningDialog(PolicyService)
                            {
                                Owner = mainWindow
                            };
                            dialog.ShowDialog();
                        }
                    });
                }
            }
            catch
            {
                // Ignore policy check errors during startup
            }
        });

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

    protected override void OnExit(ExitEventArgs e)
    {
        _ = new DiagnosticLogService().InfoAsync("startup", "application", "exited", detail: $"exitCode={e.ApplicationExitCode}");
        _singleInstanceService?.Dispose();
        base.OnExit(e);
    }

    private static string GetInstanceScopeName()
    {
        var userSid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        return $"NetRelay-{userSid}";
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
