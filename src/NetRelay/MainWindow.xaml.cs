using System.Windows;
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
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

