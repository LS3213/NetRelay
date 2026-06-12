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

        DebounceSlider.Value = _config.DebounceSeconds;
        CooldownSlider.Value = _config.CooldownMinutes;
        KeepDaysSlider.Value = _config.KeepDays;

        DebounceValueText.Text = $"{_config.DebounceSeconds} 秒";
        CooldownValueText.Text = $"{_config.CooldownMinutes} 分钟";
        KeepDaysValueText.Text = $"{_config.KeepDays} 天";
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
        _config.DebounceSeconds = (int)DebounceSlider.Value;
        _config.CooldownMinutes = (int)CooldownSlider.Value;
        _config.KeepDays = (int)KeepDaysSlider.Value;

        DialogResult = true;
    }

    private void DebounceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DebounceValueText != null)
        {
            DebounceValueText.Text = $"{(int)e.NewValue} 秒";
        }
    }

    private void CooldownSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CooldownValueText != null)
        {
            CooldownValueText.Text = $"{(int)e.NewValue} 分钟";
        }
    }

    private void KeepDaysSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (KeepDaysValueText != null)
        {
            KeepDaysValueText.Text = $"{(int)e.NewValue} 天";
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
