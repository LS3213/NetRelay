using System.Windows;

namespace NetRelay.Dialogs;

public partial class ConfirmCloseDialog : Window
{
    public string CloseActionResult { get; private set; } = "HideToTray";
    public bool DoNotRemindMe => DoNotRemindCheckBox.IsChecked == true;

    public ConfirmCloseDialog()
    {
        InitializeComponent();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void HideToTrayButton_Click(object sender, RoutedEventArgs e)
    {
        CloseActionResult = "HideToTray";
        DialogResult = true;
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        CloseActionResult = "Exit";
        DialogResult = true;
    }
}
