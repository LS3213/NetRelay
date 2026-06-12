using System.Security.Principal;
using System.Threading;
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
