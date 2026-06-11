using System.Windows;
using System.Windows.Media;
using NetRelay.Dialogs;
using NetRelay.Native;
using NetRelay.Services;
using NetRelay.ViewModels;

namespace NetRelay;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly NativeNetworkConnectionService _connectionService;
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private bool _isForceExiting;

    public MainWindow()
    {
        InitializeComponent();
        var configService = new ConfigurationService();
        var connectivityService = new ConnectivityService();
        _connectionService = new NativeNetworkConnectionService();
        _viewModel = new MainViewModel(
            new NetworkAdapterService(_connectionService),
            configService,
            connectivityService);
        DataContext = _viewModel;
        SourceInitialized += (_, _) => WindowBackdrop.Apply(this);
        StateChanged += (_, _) => UpdateMaximizeIcon();
        UpdateMaximizeIcon();

        InitializeNotifyIcon();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(_viewModel.ConfigService.Current)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            _viewModel.ConfigService.Save();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void EnableAdapterButton_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.SetSelectedAdapterEnabledAsync(_connectionService, true);
    }

    private async void DisableAdapterButton_Click(object sender, RoutedEventArgs e)
    {
        var adapter = _viewModel.SelectedAdapter;
        if (adapter is null)
        {
            return;
        }

        var dialog = new ConfirmDisableDialog(adapter)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            await _viewModel.SetSelectedAdapterEnabledAsync(_connectionService, false);
        }
    }

    private void UpdateMaximizeIcon()
    {
        MaximizeIcon.Data = Geometry.Parse(
            WindowState == WindowState.Maximized
                ? "M 3,1 L 10,1 L 10,8 M 1,3 L 8,3 L 8,10 L 1,10 Z"
                : "M 1,1 L 10,1 L 10,10 L 1,10 Z");
    }

    private void InitializeNotifyIcon()
    {
        _notifyIcon = new System.Windows.Forms.NotifyIcon();
        
        try
        {
            var resourceUri = new Uri("pack://application:,,,/Assets/NetRelay.ico");
            var streamInfo = System.Windows.Application.GetResourceStream(resourceUri);
            if (streamInfo != null)
            {
                using (var stream = streamInfo.Stream)
                {
                    _notifyIcon.Icon = new System.Drawing.Icon(stream);
                }
            }
        }
        catch
        {
            try
            {
                var mainModuleFile = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(mainModuleFile))
                {
                    _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(mainModuleFile);
                }
                else
                {
                    var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                    _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                }
            }
            catch
            {
                _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            }
        }

        _notifyIcon.Text = "NetRelay 原生网络切换助手";
        _notifyIcon.Visible = true;

        _notifyIcon.DoubleClick += (s, e) => RestoreWindow();

        var contextMenu = new System.Windows.Forms.ContextMenuStrip();
        contextMenu.Items.Add("打开主窗口", null, (s, e) => RestoreWindow());
        contextMenu.Items.Add("-");
        contextMenu.Items.Add("退出程序", null, (s, e) => ExitApplication());
        _notifyIcon.ContextMenuStrip = contextMenu;
    }

    private void RestoreWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }

    private void ExitApplication()
    {
        _notifyIcon?.Dispose();
        _notifyIcon = null;
        _isForceExiting = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_isForceExiting)
        {
            base.OnClosing(e);
            return;
        }

        var config = _viewModel.ConfigService.Current;

        if (config.DoNotRemindClose)
        {
            if (config.CloseAction == "HideToTray")
            {
                e.Cancel = true;
                Hide();
            }
            else // Exit
            {
                _notifyIcon?.Dispose();
                base.OnClosing(e);
            }
        }
        else
        {
            e.Cancel = true;

            var dialog = new ConfirmCloseDialog()
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                config.CloseAction = dialog.CloseActionResult;
                config.DoNotRemindClose = dialog.DoNotRemindMe;
                
                _viewModel.ConfigService.Save();

                if (dialog.CloseActionResult == "HideToTray")
                {
                    Hide();
                }
                else
                {
                    _notifyIcon?.Dispose();
                    _isForceExiting = true;
                    Close();
                }
            }
        }
    }
}
