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
using NetRelay.Contracts;
using NetRelay.Services;

namespace NetRelay.Dialogs;

public partial class FeedbackDialog : Window
{
    private CancellationTokenSource? _cts;
    private bool _isUploading;

    public FeedbackDialog()
    {
        InitializeComponent();
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
            System.Windows.MessageBox.Show(this, "请输入反馈标题。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            TitleTextBox.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            System.Windows.MessageBox.Show(this, "请输入问题描述或建议详情。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            }

            // 2. Prepare HTTP Multipart Form Content
            var multipartContent = new MultipartFormDataContent();
            multipartContent.Add(new StringContent(type), "type");
            multipartContent.Add(new StringContent(title), "title");
            multipartContent.Add(new StringContent(content), "content");
            if (!string.IsNullOrWhiteSpace(contact))
            {
                multipartContent.Add(new StringContent(contact), "contact");
            }

            FileStream? fileStream = null;
            if (tempZipPath != null && File.Exists(tempZipPath))
            {
                fileStream = File.OpenRead(tempZipPath);
                var streamContent = new ProgressableStreamContent(fileStream, 8192, (uploaded, total) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        var percent = (double)uploaded / total * 100;
                        UploadProgressBar.Value = percent;
                        ProgressPercentTextBlock.Text = $"{percent:F0}%";
                        ProgressStatusTextBlock.Text = $"正在上传日志包... ({FormatSize(uploaded)} / {FormatSize(total)})";
                    });
                });
                streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                multipartContent.Add(streamContent, "file", "logs.zip");
            }
            else
            {
                ProgressStatusTextBlock.Text = "正在提交反馈数据...";
                UploadProgressBar.IsIndeterminate = true;
            }

            // 3. Send HTTP Request
            using var client = ActivationService.CreateHttpClient();
            var backendUrl = ActivationService.GetBackendUrl();
            var request = new HttpRequestMessage(HttpMethod.Post, $"{backendUrl}/api/v1/feedback")
            {
                Content = multipartContent
            };
            request.Headers.Add(Protocol.VersionHeader, Protocol.CurrentVersion.ToString());
            request.Headers.Add(Protocol.ClientVersionHeader, Protocol.ProductVersion);
            request.Headers.Add(Protocol.RequestIdHeader, Guid.NewGuid().ToString("N"));

            var response = await client.SendAsync(request, token);
            if (fileStream != null)
            {
                await fileStream.DisposeAsync();
            }

            if (response.IsSuccessStatusCode)
            {
                System.Windows.MessageBox.Show(this, "感谢您的反馈，我们已收到并会认真阅读！", "提交成功", MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
            }
            else
            {
                var errorBody = await response.Content.ReadAsStringAsync(token);
                string errorMsg = "网络请求失败，请稍后重试。";
                try
                {
                    var doc = JsonDocument.Parse(errorBody);
                    if (doc.RootElement.TryGetProperty("error", out var errorEl) && errorEl.TryGetProperty("message", out var msgEl))
                    {
                        errorMsg = msgEl.GetString() ?? errorMsg;
                    }
                }
                catch { }

                System.Windows.MessageBox.Show(this, $"提交失败: {errorMsg}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                SetUploadingState(false);
            }
        }
        catch (OperationCanceledException)
        {
            System.Windows.MessageBox.Show(this, "已取消反馈提交与日志上传。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            SetUploadingState(false);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"发生错误: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            SetUploadingState(false);
        }
        finally
        {
            if (tempZipPath != null && File.Exists(tempZipPath))
            {
                try { File.Delete(tempZipPath); } catch { }
            }
            _cts.Dispose();
            _cts = null;
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

        ProgressPanel.Visibility = uploading ? Visibility.Visible : Visibility.Collapsed;
        ButtonPanel.Visibility = uploading ? Visibility.Collapsed : Visibility.Visible;

        UploadProgressBar.Value = 0;
        UploadProgressBar.IsIndeterminate = false;
        ProgressPercentTextBlock.Text = "0%";
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
