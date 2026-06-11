using System.Windows;
using System.Windows.Media;
using NetRelay.Native;
using NetRelay.Services;
using NetRelay.ViewModels;

namespace NetRelay;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel(new NetworkAdapterService());
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

    private void UpdateMaximizeIcon()
    {
        MaximizeIcon.Data = Geometry.Parse(
            WindowState == WindowState.Maximized
                ? "M 3,1 L 10,1 L 10,8 M 1,3 L 8,3 L 8,10 L 1,10 Z"
                : "M 1,1 L 10,1 L 10,10 L 1,10 Z");
    }
}
