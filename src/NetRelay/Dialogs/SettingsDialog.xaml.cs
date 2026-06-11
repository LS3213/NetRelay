using System.Windows;
using NetRelay.Models;

namespace NetRelay.Dialogs;

public partial class SettingsDialog : Window
{
    private readonly AppConfiguration _config;

    public SettingsDialog(AppConfiguration config)
    {
        InitializeComponent();
        _config = config;

        LoadSettings();
    }

    private void LoadSettings()
    {
        if (!_config.DoNotRemindClose)
        {
            AskOnCloseRadio.IsChecked = true;
        }
        else if (_config.CloseAction == "HideToTray")
        {
            HideToTrayRadio.IsChecked = true;
        }
        else
        {
            ExitProgramRadio.IsChecked = true;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (AskOnCloseRadio.IsChecked == true)
        {
            _config.DoNotRemindClose = false;
            _config.CloseAction = "Ask";
        }
        else if (HideToTrayRadio.IsChecked == true)
        {
            _config.DoNotRemindClose = true;
            _config.CloseAction = "HideToTray";
        }
        else if (ExitProgramRadio.IsChecked == true)
        {
            _config.DoNotRemindClose = true;
            _config.CloseAction = "Exit";
        }

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
