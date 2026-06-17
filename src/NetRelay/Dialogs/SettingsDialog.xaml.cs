using System;
using System.Threading.Tasks;
using System.Windows;
using NetRelay.Models;
using NetRelay.Services;

namespace NetRelay.Dialogs;

public partial class SettingsDialog : Window
{
    private enum SettingsSection
    {
        General,
        Diagnostics,
        Defaults
    }

    private readonly AppConfiguration _config;
    private readonly AutoStartService _autoStartService = new();
    private readonly UpdateStatusSnapshot _updateStatus;
    private readonly bool _automationEnabled;
    private readonly string? _automationDisabledReason;
    private readonly Func<Task>? _exportDiagnosticsAsync;
    private readonly Action? _openLogsDirectory;
    private readonly Action? _openUpdateCacheDirectory;
    private readonly Func<Task>? _clearUpdateCacheAsync;
    private bool _actualAutoStartEnabled;

    public SettingsDialog(
        AppConfiguration config,
        UpdateStatusSnapshot updateStatus,
        bool automationEnabled,
        string? automationDisabledReason,
        Func<Task>? exportDiagnosticsAsync = null,
        Action? openLogsDirectory = null,
        Action? openUpdateCacheDirectory = null,
        Func<Task>? clearUpdateCacheAsync = null)
    {
        InitializeComponent();
        _config = config;
        _updateStatus = updateStatus;
        _automationEnabled = automationEnabled;
        _automationDisabledReason = automationDisabledReason;
        _exportDiagnosticsAsync = exportDiagnosticsAsync;
        _openLogsDirectory = openLogsDirectory;
        _openUpdateCacheDirectory = openUpdateCacheDirectory;
        _clearUpdateCacheAsync = clearUpdateCacheAsync;

        LoadSettings();
        ShowSection(SettingsSection.General);
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
        StartupCheckUpdatesCheckBox.IsChecked = _config.AutoCheckUpdatesOnStartup;

        DebounceSlider.Value = _config.DebounceSeconds;
        CooldownSlider.Value = _config.CooldownMinutes;
        KeepDaysSlider.Value = _config.KeepDays;

        DebounceValueText.Text = $"{_config.DebounceSeconds} 秒";
        CooldownValueText.Text = $"{_config.CooldownMinutes} 分钟";
        KeepDaysValueText.Text = $"{_config.KeepDays} 天";
        AutomationStatusText.Text = _automationEnabled
            ? $"自动化规则已启用，共 {_config.Rules.Count} 条"
            : $"自动化规则已暂停：{_automationDisabledReason ?? "探测配置无效"}";
        LastCheckStatusText.Text = _updateStatus.LastCheckedAt.HasValue
            ? $"{_updateStatus.LastCheckedAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {_updateStatus.LastCheckMessage}"
            : "尚未执行版本检查";
        LastDownloadStatusText.Text = _updateStatus.LastDownloadAt.HasValue
            ? $"{_updateStatus.LastDownloadAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {_updateStatus.LastDownloadMessage}"
            : "尚未下载更新包";
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        bool autoStartEnabled = AutoStartCheckBox.IsChecked == true;
        if (autoStartEnabled || _actualAutoStartEnabled != autoStartEnabled)
        {
            var result = _autoStartService.SetEnabled(autoStartEnabled);
            if (!result.Success)
            {
                ModernMessageBox.Show(
                    this,
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
        _config.AutoCheckUpdatesOnStartup = StartupCheckUpdatesCheckBox.IsChecked == true;
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

    private void GeneralSectionButton_Click(object sender, RoutedEventArgs e) => ShowSection(SettingsSection.General);

    private void DiagnosticsSectionButton_Click(object sender, RoutedEventArgs e) => ShowSection(SettingsSection.Diagnostics);

    private void DefaultsSectionButton_Click(object sender, RoutedEventArgs e) => ShowSection(SettingsSection.Defaults);

    private async void ExportDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_exportDiagnosticsAsync == null)
        {
            return;
        }

        try
        {
            await _exportDiagnosticsAsync();
        }
        catch (Exception ex)
        {
            ModernMessageBox.Show(this, $"导出诊断包失败：{ex.Message}", "NetRelay", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _openLogsDirectory?.Invoke();
        }
        catch (Exception ex)
        {
            ModernMessageBox.Show(this, $"打开日志目录失败：{ex.Message}", "NetRelay", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenUpdateCacheButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _openUpdateCacheDirectory?.Invoke();
        }
        catch (Exception ex)
        {
            ModernMessageBox.Show(this, $"打开更新缓存目录失败：{ex.Message}", "NetRelay", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ClearUpdateCacheButton_Click(object sender, RoutedEventArgs e)
    {
        if (_clearUpdateCacheAsync == null)
        {
            return;
        }

        var result = ModernMessageBox.Show(
            this,
            "将尝试删除已下载的更新缓存与中间文件，当前运行中的程序文件不会被删除。",
            "清理更新缓存",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            await _clearUpdateCacheAsync();
        }
        catch (Exception ex)
        {
            ModernMessageBox.Show(this, $"清理更新缓存失败：{ex.Message}", "NetRelay", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowSection(SettingsSection section)
    {
        if (GeneralSection == null)
        {
            return;
        }

        GeneralSection.Visibility = section == SettingsSection.General ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsSection.Visibility = section == SettingsSection.Diagnostics ? Visibility.Visible : Visibility.Collapsed;
        DefaultsSection.Visibility = section == SettingsSection.Defaults ? Visibility.Visible : Visibility.Collapsed;

        ApplySectionButtonState(GeneralSectionButton, section == SettingsSection.General);
        ApplySectionButtonState(DiagnosticsSectionButton, section == SettingsSection.Diagnostics);
        ApplySectionButtonState(DefaultsSectionButton, section == SettingsSection.Defaults);
    }

    private static void ApplySectionButtonState(System.Windows.Controls.Button button, bool active)
    {
        button.Background = active
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF));
        button.BorderBrush = active
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x90, 0xC7, 0xD5, 0xEE))
            : System.Windows.Media.Brushes.Transparent;
        button.Foreground = active
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x23, 0x31, 0x4A))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x58, 0x67, 0x7E));
    }
}
