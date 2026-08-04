using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using NetRelay.Contracts;
using NetRelay.Services;
using Omnexa.Core;

namespace NetRelay.Dialogs;

public partial class FeedbackDialog : Window
{
    private const long MaximumOmnexaAttachmentBytes = 10L * 1024 * 1024;
    private CancellationTokenSource? _cts;
    private bool _isUploading;
    private readonly OmnexaIntegrationService _omnexa;
    private List<MyFeedbackItem> _historyItems = new();

    public FeedbackDialog()
    {
        InitializeComponent();
        _omnexa = new OmnexaIntegrationService(
            ConfigurationService.Instance ?? new ConfigurationService());
        Loaded += FeedbackDialog_Loaded;
    }

    private async void FeedbackDialog_Loaded(object sender, RoutedEventArgs e)
    {
        await CalculateLogSizeAsync();
    }

    private async void IncludeLogsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            await CalculateLogSizeAsync();
        }
    }

    private async Task CalculateLogSizeAsync()
    {
        if (IncludeLogsCheckBox.IsChecked != true)
        {
            LogSizeTextBlock.Text = "(不附带)";
            return;
        }

        LogSizeTextBlock.Text = "(计算中...)";
        try
        {
            var tempFile = Path.Combine(Path.GetTempPath(), "netrelay_diag_temp.zip");
            var logService = new LogService();
            await logService.CreateDiagnosticZipAsync(tempFile);
            if (File.Exists(tempFile))
            {
                var size = new FileInfo(tempFile).Length;
                File.Delete(tempFile);
                LogSizeTextBlock.Text = $"({FormatSize(size)})";
            }
            else
            {
                LogSizeTextBlock.Text = "(无可用日志)";
            }
        }
        catch
        {
            LogSizeTextBlock.Text = "(计算失败)";
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isUploading)
        {
            DialogResult = false;
        }
    }

    private void CancelUploadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isUploading)
        {
            ProgressStatusTextBlock.Text = "正在取消上传...";
            _cts?.Cancel();
        }
    }

    private async void SubmitButton_Click(object sender, RoutedEventArgs e)
    {
        var title = TitleTextBox.Text.Trim();
        var content = ContentTextBox.Text.Trim();
        var contact = ContactTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(title))
        {
            ModernMessageBox.Show(this, "请输入反馈标题。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            TitleTextBox.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            ModernMessageBox.Show(this, "请输入问题描述或建议详情。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            ContentTextBox.Focus();
            return;
        }

        string type = "other";
        if (TypeComboBox.SelectedIndex == 0) type = "bug";
        else if (TypeComboBox.SelectedIndex == 1) type = "suggestion";

        // Enter uploading state
        SetUploadingState(true);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        string? tempZipPath = null;

        try
        {
            // 1. If logs are requested, zip them up
            if (IncludeLogsCheckBox.IsChecked == true)
            {
                ProgressStatusTextBlock.Text = "正在打包诊断日志...";
                tempZipPath = Path.Combine(Path.GetTempPath(), $"netrelay_diag_{Guid.NewGuid():N}.zip");
                var logService = new LogService();
                await logService.CreateDiagnosticZipAsync(tempZipPath);
                token.ThrowIfCancellationRequested();
                if (File.Exists(tempZipPath) &&
                    new FileInfo(tempZipPath).Length > MaximumOmnexaAttachmentBytes)
                {
                    throw new InvalidOperationException(
                        "诊断日志超过 Omnexa 单附件 10 MB 限制，请先清理旧日志后重试。");
                }
            }

            // Omnexa requires JSON thread creation first, followed by an
            // attachment upload authenticated with the returned one-time token.
            ProgressStatusTextBlock.Text = "正在通过 Omnexa 提交反馈...";
            UploadProgressBar.IsIndeterminate = true;
            var requestId = Guid.NewGuid().ToString("N");
            var created = await _omnexa.SubmitFeedbackAsync(
                type,
                title,
                content,
                contact,
                token);

            if (tempZipPath is not null && File.Exists(tempZipPath))
            {
                ProgressStatusTextBlock.Text = "反馈已创建，正在上传诊断日志...";
                await _omnexa.UploadFeedbackAttachmentAsync(
                    created.Id,
                    created.UploadToken,
                    tempZipPath,
                    token);
            }

            await new DiagnosticLogService().InfoAsync(
                "feedback",
                "submit-response",
                "success",
                requestId,
                201,
                tempZipPath is not null && File.Exists(tempZipPath) ? new FileInfo(tempZipPath).Length : null);
            ModernMessageBox.Show(this, "感谢您的反馈，我们已收到并会认真阅读！", "提交成功", MessageBoxButton.OK, MessageBoxImage.Information);

            TitleTextBox.Text = string.Empty;
            ContentTextBox.Text = string.Empty;
            ContactTextBox.Text = string.Empty;
            SetUploadingState(false);
            SwitchToHistoryTab();
        }
        catch (OperationCanceledException)
        {
            await new DiagnosticLogService().InfoAsync("feedback", "submit", "cancelled");
            ModernMessageBox.Show(this, "已取消反馈提交与日志上传。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            SetUploadingState(false);
        }
        catch (Exception ex)
        {
            await new DiagnosticLogService().ErrorAsync("feedback", "submit", ex);
            ModernMessageBox.Show(this, $"发生错误: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            SetUploadingState(false);
        }
        finally
        {
            if (tempZipPath != null && File.Exists(tempZipPath))
            {
                try { File.Delete(tempZipPath); } catch { }
            }
            if (_cts != null)
            {
                _cts.Dispose();
                _cts = null;
            }
        }
    }

    private void SetUploadingState(bool uploading)
    {
        _isUploading = uploading;
        TypeComboBox.IsEnabled = !uploading;
        TitleTextBox.IsEnabled = !uploading;
        ContentTextBox.IsEnabled = !uploading;
        ContactTextBox.IsEnabled = !uploading;
        IncludeLogsCheckBox.IsEnabled = !uploading;
        TabFeedbackBtn.IsEnabled = !uploading;
        TabHistoryBtn.IsEnabled = !uploading;

        ProgressPanel.Visibility = uploading ? Visibility.Visible : Visibility.Collapsed;
        ButtonPanel.Visibility = uploading ? Visibility.Collapsed : Visibility.Visible;

        UploadProgressBar.Value = 0;
        UploadProgressBar.IsIndeterminate = false;
        ProgressPercentTextBlock.Text = "0%";
    }

    // Tab Navigation Logic
    private void TabFeedbackBtn_Click(object sender, RoutedEventArgs e)
    {
        SwitchToFeedbackTab();
    }

    private void TabHistoryBtn_Click(object sender, RoutedEventArgs e)
    {
        SwitchToHistoryTab();
    }

    private void SwitchToFeedbackTab()
    {
        TabFeedbackBtn.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White);
        TabFeedbackBtn.FontWeight = FontWeights.SemiBold;
        TabHistoryBtn.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Transparent);
        TabHistoryBtn.FontWeight = FontWeights.Normal;

        SubmissionFormPanel.Visibility = Visibility.Visible;
        HistoryPanel.Visibility = Visibility.Collapsed;
        SubmitButton.Visibility = Visibility.Visible;
    }

    private async void SwitchToHistoryTab()
    {
        TabFeedbackBtn.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Transparent);
        TabFeedbackBtn.FontWeight = FontWeights.Normal;
        TabHistoryBtn.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White);
        TabHistoryBtn.FontWeight = FontWeights.SemiBold;

        SubmissionFormPanel.Visibility = Visibility.Collapsed;
        HistoryPanel.Visibility = Visibility.Visible;
        SubmitButton.Visibility = Visibility.Collapsed;

        await LoadHistoryAsync();
    }

    private async Task LoadHistoryAsync()
    {
        HistoryListBox.ItemsSource = null;
        HistoryLoadingPanel.Visibility = Visibility.Visible;
        NoSelectionTextBlock.Visibility = Visibility.Visible;
        NoSelectionTextBlock.Text = "正在加载反馈历史...";
        DetailsGrid.Visibility = Visibility.Collapsed;

        try
        {
            var history = await _omnexa.GetFeedbackHistoryAsync(CancellationToken.None);
            _historyItems = history.Select(ToHistoryItem).ToList();
            HistoryListBox.ItemsSource = _historyItems;
            NoSelectionTextBlock.Text = _historyItems.Count == 0 ? "暂无反馈历史" : "选择上方反馈以查看详情";
        }
        catch
        {
            NoSelectionTextBlock.Text = "反馈历史暂时无法加载，请稍后重试";
        }
        finally
        {
            HistoryLoadingPanel.Visibility = Visibility.Collapsed;
        }
    }

    private static MyFeedbackItem ToHistoryItem(ClientFeedbackThread thread) =>
        new()
        {
            Id = thread.Id,
            Type = thread.Type,
            Title = thread.Title,
            Content = string.Join(
                Environment.NewLine + Environment.NewLine,
                thread.Messages.Select(message =>
                    $"[{(message.IsOperatorReply ? "管理员回复" : "我的反馈")} · " +
                    $"{message.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}]" +
                    Environment.NewLine +
                    message.Content)),
            Status = thread.Status,
            CreatedAt = thread.CreatedAt,
            StatusUpdatedAt = thread.UpdatedAt
        };

    private void HistoryListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryListBox.SelectedItem is MyFeedbackItem selected)
        {
            NoSelectionTextBlock.Visibility = Visibility.Collapsed;
            DetailsGrid.Visibility = Visibility.Visible;

            var sb = new StringBuilder();
            sb.AppendLine($"[反馈正文]");
            sb.AppendLine(selected.Content);
            sb.AppendLine();
            sb.AppendLine($"[处理进度]: {selected.StatusLabel}");
            if (selected.StatusUpdatedAt.HasValue)
            {
                sb.AppendLine($"[更新时间]: {selected.StatusUpdatedAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            }
            DetailsContentTextBox.Text = sb.ToString();
        }
        else
        {
            NoSelectionTextBlock.Visibility = Visibility.Visible;
            DetailsGrid.Visibility = Visibility.Collapsed;
        }
    }

    public class MyFeedbackItem
    {
        public Guid Id { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? StatusUpdatedAt { get; set; }

        public string CreatedAtLabel => CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        public string StatusLabel => Status switch
        {
            "open" => "待处理",
            "inprogress" or "in_progress" => "处理中",
            "pending" => "处理中",
            "closed" => "已关闭",
            "resolved" => "已解决",
            "ignored" => "已忽略",
            _ => "未知"
        };

        public string StatusBg => Status switch
        {
            "open" or "inprogress" or "in_progress" or "pending" => "#E0ECFF",
            "closed" => "#F0F0F0",
            "resolved" => "#E2FBE7",
            "ignored" => "#FEECEB",
            _ => "#F0F0F0"
        };

        public string StatusFg => Status switch
        {
            "open" or "inprogress" or "in_progress" or "pending" => "#465fdc",
            "closed" => "#666666",
            "resolved" => "#28a745",
            "ignored" => "#dc3545",
            _ => "#666666"
        };
    }

    private sealed class ProgressableStreamContent : HttpContent
    {
        private readonly Stream _stream;
        private readonly int _bufferSize;
        private readonly Action<long, long> _onProgress;

        public ProgressableStreamContent(Stream stream, int bufferSize, Action<long, long> onProgress)
        {
            _stream = stream;
            _bufferSize = bufferSize;
            _onProgress = onProgress;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var buffer = new byte[_bufferSize];
            var totalBytes = _stream.Length;
            long uploadedBytes = 0;
            _stream.Position = 0;

            while (true)
            {
                var read = await _stream.ReadAsync(buffer, 0, buffer.Length);
                if (read <= 0) break;

                await stream.WriteAsync(buffer, 0, read);
                uploadedBytes += read;
                _onProgress(uploadedBytes, totalBytes);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _stream.Length;
            return true;
        }
    }
}
