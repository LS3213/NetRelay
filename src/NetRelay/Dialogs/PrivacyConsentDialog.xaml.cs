using System;
using System.Diagnostics;
using System.Windows;

namespace NetRelay.Dialogs;

public partial class PrivacyConsentDialog : Window
{
    public bool ConsentAccepted { get; private set; }

    public PrivacyConsentDialog()
    {
        InitializeComponent();
        
        // 允许无边框窗口拖动
        MouseDown += (sender, e) =>
        {
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            {
                DragMove();
            }
        };
    }

    private void AgreeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (AcceptButton != null)
        {
            AcceptButton.IsEnabled = AgreeCheckBox.IsChecked == true;
        }
    }

    private void RejectButton_Click(object sender, RoutedEventArgs e)
    {
        ConsentAccepted = false;
        DialogResult = false;
    }

    private void AcceptButton_Click(object sender, RoutedEventArgs e)
    {
        ConsentAccepted = true;
        DialogResult = true;
    }

    private void Terms_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl("https://netrelay.473700.xyz/terms/");
    }

    private void Privacy_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl("https://netrelay.473700.xyz/privacy/");
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"无法打开浏览器：{ex.Message}",
                "NetRelay",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
