using System.Windows;
using NetRelay.Models;
using NetRelay.Services;

namespace NetRelay.Dialogs;

public partial class SettingsDialog : Window
{
    private readonly AppConfiguration _config;
    private readonly AutoStartService _autoStartService = new();
    private bool _actualAutoStartEnabled;

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

        _actualAutoStartEnabled = _autoStartService.IsEnabled();
        AutoStartCheckBox.IsChecked = _actualAutoStartEnabled;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        bool autoStartEnabled = AutoStartCheckBox.IsChecked == true;
        if (autoStartEnabled || _actualAutoStartEnabled != autoStartEnabled)
        {
            var result = _autoStartService.SetEnabled(autoStartEnabled);
            if (!result.Success)
            {
                System.Windows.MessageBox.Show(
                    $"设置开机自启失败：{result.ErrorMessage}",
                    "NetRelay",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }
        }

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

        _config.AutoStart = autoStartEnabled;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
