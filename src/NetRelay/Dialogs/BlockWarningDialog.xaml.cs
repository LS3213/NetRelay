using System;
using System.Diagnostics;
using System.Windows;
using NetRelay.Services;

namespace NetRelay.Dialogs;

public partial class BlockWarningDialog : Window
{
    private readonly PolicyService _policyService;

    public BlockWarningDialog(PolicyService policyService)
    {
        _policyService = policyService;
        InitializeComponent();
        ConfigureUI();
    }

    private void ConfigureUI()
    {
        TitleTextBlock.Text = _policyService.IsMandatoryUpdateRequired
            ? "必须先更新 NetRelay"
            : _policyService.IsMaintenance
            ? "Omnexa 正在维护"
            : _policyService.Decision == "deny"
                ? "此安装已被拒绝运行"
                : "NetRelay 已进入受限模式";

        if (_policyService.ExpiresAt.HasValue)
        {
            ExpiryTextBlock.Text = $"策略到期: {_policyService.ExpiresAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        }
        else
        {
            ExpiryTextBlock.Text = _policyService.IsMandatoryUpdateRequired
                ? "更新功能仍可使用"
                : _policyService.IsMaintenance
                ? "维护结束后将自动恢复"
                : "等待下一次 Omnexa 策略同步";
        }

        var reasonMarkdown = string.IsNullOrWhiteSpace(_policyService.Reason)
            ? "您的设备已被系统管理员限制使用。具体原因请联系支持人员。"
            : _policyService.Reason;

        DocViewer.Document = SafeMarkdownParser.Parse(reasonMarkdown);

        if (!string.IsNullOrWhiteSpace(_policyService.AppealUrl) &&
            (_policyService.AppealUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             _policyService.AppealUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            AppealButton.Visibility = Visibility.Visible;
        }
        else
        {
            AppealButton.Visibility = Visibility.Collapsed;
        }
    }

    private void AppealButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_policyService.AppealUrl))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _policyService.AppealUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(this, $"无法打开申诉链接: {ex.Message}", "打开链接失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
