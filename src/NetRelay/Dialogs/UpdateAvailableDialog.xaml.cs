using System.ComponentModel;
using System.Windows;
using NetRelay.Contracts;

namespace NetRelay.Dialogs;

public partial class UpdateAvailableDialog : Window
{
    private readonly bool _isMandatory;
    private bool _allowClose;

    public UpdateAvailableDialog(UpdateManifest manifest)
    {
        InitializeComponent();
        _isMandatory = manifest.IsMandatory;
        VersionText.Text = $"v{manifest.Version} · {(manifest.IsMandatory ? "必须安装后才能继续使用" : "可选择稍后安装")}";
        ReleaseDateText.Text = $"发布时间：{manifest.ReleaseDate.ToLocalTime():yyyy-MM-dd HH:mm}";
        ChangelogText.Text = string.IsNullOrWhiteSpace(manifest.Changelog) ? "本次更新未提供更新日志。" : manifest.Changelog;
        MandatoryBadge.Visibility = manifest.IsMandatory ? Visibility.Visible : Visibility.Collapsed;
        LaterButton.Visibility = manifest.IsMandatory ? Visibility.Collapsed : Visibility.Visible;
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
}
