using System.Security.Principal;
using System.Threading;
using System.Windows;
using NetRelay.Services;

namespace NetRelay;

public partial class App : System.Windows.Application
{
    private SingleInstanceService? _singleInstanceService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceService = new SingleInstanceService(GetInstanceScopeName());
        if (!_singleInstanceService.TryAcquire())
        {
            _singleInstanceService.SignalPrimaryInstance();
            Shutdown();
            return;
        }

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        _singleInstanceService.StartListening(() => Dispatcher.Invoke(mainWindow.RestoreWindow));
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
}
