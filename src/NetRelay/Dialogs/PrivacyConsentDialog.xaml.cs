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
            var accepted = AgreeCheckBox.IsChecked == true;
            AcceptButton.IsEnabled = accepted;
            AcceptButton.Content = accepted ? "同意并激活" : "请先勾选同意";
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
}
