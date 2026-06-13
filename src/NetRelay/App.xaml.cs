using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using NetRelay.Infrastructure;
using NetRelay.Services;

namespace NetRelay;

public partial class App : System.Windows.Application
{
    private SingleInstanceService? _singleInstanceService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        SmoothScrollBehavior.Enable();

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
                System.Windows.MessageBox.Show(
                    $"设备激活过程中发生异常：{ex.InnerException?.Message ?? ex.Message}",
                    "NetRelay 激活失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            if (!activated)
            {
                System.Windows.MessageBox.Show(
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
                System.Windows.MessageBox.Show(
                    $"设备激活过程中发生异常：{ex.InnerException?.Message ?? ex.Message}",
                    "NetRelay 激活失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            if (!activated)
            {
                System.Windows.MessageBox.Show(
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

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        _singleInstanceService.StartListening(args => Dispatcher.Invoke(() => mainWindow.HandleCommandLineArgs(args)));
        if (e.Args.Length > 0)
        {
            mainWindow.HandleCommandLineArgs(e.Args);
        }
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
                System.Windows.MessageBox.Show(
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
                System.Windows.MessageBox.Show(
                    $"生成只读网卡诊断报告失败：\n{exception.Message}",
                    "NetRelay 网卡诊断",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        return true;
    }
}
