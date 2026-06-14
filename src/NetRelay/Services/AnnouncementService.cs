using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using NetRelay.Contracts;
using NetRelay.Contracts.Security;
using NetRelay.Dialogs;

namespace NetRelay.Services;

public sealed class AnnouncementService
{
    private readonly ConfigurationService _configService;

    public AnnouncementService(ConfigurationService configService)
    {
        _configService = configService;
    }

    public async Task CheckAndDisplayAnnouncementsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // 1. Fetch active announcements
            using var client = new HttpClient();
            var backendUrl = ActivationService.GetBackendUrl();
            var request = new HttpRequestMessage(HttpMethod.Get, $"{backendUrl}/api/v1/announcements/active?clientVersion=1.0.0");
            request.Headers.Add(Protocol.VersionHeader, Protocol.CurrentVersion.ToString());
            request.Headers.Add(Protocol.ClientVersionHeader, "1.0.0");
            request.Headers.Add(Protocol.RequestIdHeader, Guid.NewGuid().ToString("N"));

            var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return; // Silence backend communication errors during normal startup
            }

            var apiResponse = await response.Content.ReadFromJsonAsync<ApiResponse<AnnouncementCheckResponse>>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);

            if (apiResponse?.Data == null)
            {
                return;
            }

            var checkRes = apiResponse.Data;

            // 2. Double-verify signatures
            // First: Verify certificate using Root Public Key
            using var rootKey = ECDsa.Create();
            rootKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(OperationalKeyCertificate.DefaultRootPublicKeyBase64), out _);
            if (!checkRes.Certificate.Verify(DateTimeOffset.UtcNow, rootKey, "announcement"))
            {
                Logger.Warn("公告签名证书校验未通过，已忽略。");
                return;
            }

            // Second: Verify envelope signature using Certificate's Public Key
            using var opKey = ECDsa.Create();
            opKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(checkRes.Certificate.PublicKey), out _);
            if (!checkRes.Envelope.Verify("announcement", checkRes.Envelope.Nonce, DateTimeOffset.UtcNow, opKey))
            {
                Logger.Warn("公告数据包数字签名验证失败，已忽略。");
                return;
            }

            // 3. Deserialize Payload
            var payload = JsonSerializer.Deserialize<ActiveAnnouncementsPayload>(
                checkRes.Envelope.PayloadJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (payload == null || payload.Announcements == null || payload.Announcements.Count == 0)
            {
                return;
            }

            // 4. Process and display active announcements
            var displayedIds = _configService.Current.DisplayedAnnouncementIds;
            var newDisplayedIds = new List<string>();

            // Sort so critical announcements display first/last or process sequentially
            foreach (var announcement in payload.Announcements.OrderByDescending(a => a.Severity == "critical" ? 2 : a.Severity == "important" ? 1 : 0))
            {
                var idStr = announcement.Id.ToString("D");

                // Filter out once_per_device announcements already displayed
                if (announcement.DisplayTrigger == "once_per_device" && displayedIds.Contains(idStr))
                {
                    continue;
                }

                // Show on UI Thread
                var displayed = await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var dialog = new AnnouncementDialog(announcement);
                    
                    // Try to attach owner
                    if (System.Windows.Application.Current.MainWindow != null && System.Windows.Application.Current.MainWindow.IsVisible)
                    {
                        dialog.Owner = System.Windows.Application.Current.MainWindow;
                    }
                    
                    dialog.ShowDialog();
                    return dialog.DialogResult == true;
                });

                // Record display
                if (announcement.DisplayTrigger == "once_per_device")
                {
                    newDisplayedIds.Add(idStr);
                }

                // Critical blocks application execution completely: shutdown immediately upon closing
                if (announcement.Severity == "critical")
                {
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        System.Windows.Application.Current.Shutdown();
                    });
                    return;
                }
            }

            if (newDisplayedIds.Count > 0)
            {
                _configService.Current.DisplayedAnnouncementIds.AddRange(newDisplayedIds);
                _configService.Save();
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"公告服务运行错误: {ex.Message}");
        }
    }

    private sealed class ActiveAnnouncementsPayload
    {
        public List<AnnouncementDto> Announcements { get; set; } = [];
    }

    // Simple fallback logging helpers (NetRelay might have its own logger, but let's make it self-contained)
    private static class Logger
    {
        public static void Warn(string msg) => System.Diagnostics.Debug.WriteLine($"[AnnouncementService] WARN: {msg}");
        public static void Error(string msg) => System.Diagnostics.Debug.WriteLine($"[AnnouncementService] ERROR: {msg}");
    }
}
