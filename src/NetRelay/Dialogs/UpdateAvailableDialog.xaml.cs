using System.ComponentModel;
using System.Windows;
using NetRelay.Contracts;
using NetRelay.Models;

namespace NetRelay.Dialogs;

public partial class UpdateAvailableDialog : Window
{
    private readonly bool _isMandatory;
    private bool _allowClose;

    public UpdateAvailableDialog(UpdateManifest manifest, UpdatePreflightResult? preflight = null)
    {
        InitializeComponent();
        _isMandatory = manifest.IsMandatory;
        VersionText.Text = $"v{manifest.Version} · {(manifest.IsMandatory ? "必须安装后才能继续使用" : "可选择稍后安装")}";
        ReleaseDateText.Text = $"发布时间：{manifest.ReleaseDate.ToLocalTime():yyyy-MM-dd HH:mm}";
        SourceText.Text = $"更新来源：{manifest.SourceLabel}";
        PackageText.Text = $"安装包大小：{FormatBytes(manifest.PackageSize)}";
        ChangelogText.Text = string.IsNullOrWhiteSpace(manifest.Changelog) ? "本次更新未提供更新日志。" : manifest.Changelog;
        MandatoryBadge.Visibility = manifest.IsMandatory ? Visibility.Visible : Visibility.Collapsed;
        LaterButton.Visibility = manifest.IsMandatory ? Visibility.Collapsed : Visibility.Visible;
        if (preflight != null)
        {
            PreflightPanel.Visibility = Visibility.Visible;
            PreflightText.Text = preflight.Summary;
        }
        Closing += OnClosing;
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        _allowClose = true;
        DialogResult = true;
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        _allowClose = true;
        DialogResult = false;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isMandatory && !_allowClose)
        {
            e.Cancel = true;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.##} {units[unitIndex]}";
    }
}
