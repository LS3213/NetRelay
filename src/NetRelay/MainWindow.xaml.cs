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

    public MainWindow()
    {
        InitializeComponent();
        _connectionService = new NativeNetworkConnectionService();
        _viewModel = new MainViewModel(new NetworkAdapterService(_connectionService));
        DataContext = _viewModel;
        SourceInitialized += (_, _) => WindowBackdrop.Apply(this);
        StateChanged += (_, _) => UpdateMaximizeIcon();
        UpdateMaximizeIcon();
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
}
