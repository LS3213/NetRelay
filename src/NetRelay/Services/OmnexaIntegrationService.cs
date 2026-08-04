using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using NetRelay.Contracts;
using Omnexa.Core;
using Omnexa.Sdk;

namespace NetRelay.Services;

public sealed class OmnexaIntegrationService
{
    private readonly ConfigurationService _configurationService;
    private readonly FileOmnexaCache _cache;

    public OmnexaIntegrationService(ConfigurationService configurationService)
    {
        _configurationService = configurationService;
        _cache = new FileOmnexaCache(GetCacheDirectory(GetInstallationId()));
    }

    public ClientRuntime Runtime => new(
        Protocol.ProductVersion,
        OmnexaProduct.OperatingSystem,
        OmnexaProduct.Architecture,
        CurrentTermsVersion,
        CurrentPrivacyVersion);

    public string CurrentTermsVersion =>
        string.IsNullOrWhiteSpace(_configurationService.Current.AcceptedTermsVersion)
            ? "1.0"
            : _configurationService.Current.AcceptedTermsVersion;

    public string CurrentPrivacyVersion =>
        string.IsNullOrWhiteSpace(_configurationService.Current.AcceptedPrivacyVersion)
            ? "1.0"
            : _configurationService.Current.AcceptedPrivacyVersion;

    public async Task<bool> HasCachedActivationAsync(
        CancellationToken cancellationToken = default)
    {
        using var httpClient = CreateHttpClient();
        var activation = await CreateClient(httpClient)
            .GetCachedActivationAsync(cancellationToken);
        return activation is not null &&
               activation.InstallationId == GetInstallationId();
    }

    public async Task<ActivateClientResponse> ActivateAsync(
        CancellationToken cancellationToken = default)
    {
        var installationId = GetInstallationId();
        using var httpClient = CreateHttpClient();
        var result = await CreateClient(httpClient).ActivateAsync(
            installationId,
            new WindowsFingerprintProvider(OmnexaProduct.FingerprintProtocolSalt),
            Runtime,
            cancellationToken);
        return result;
    }

    public async Task<ControlSnapshot> SyncAsync(
        CancellationToken cancellationToken = default)
    {
        using var httpClient = CreateHttpClient();
        return await CreateClient(httpClient).SyncAsync(Runtime, cancellationToken);
    }

    public async Task<ControlSyncResult> SyncWithSourceAsync(
        CancellationToken cancellationToken = default)
    {
        using var httpClient = CreateHttpClient();
        return await CreateClient(httpClient).SyncWithSourceAsync(Runtime, cancellationToken);
    }

    public async Task<ClientHeartbeatResponse> HeartbeatAsync(
        CancellationToken cancellationToken = default)
    {
        using var httpClient = CreateHttpClient();
        return await CreateClient(httpClient).HeartbeatAsync(Runtime, cancellationToken);
    }

    public async Task<ConnectivityChallengePayload> ChallengeConnectivityAsync(
        CancellationToken cancellationToken = default)
    {
        using var httpClient = CreateHttpClient();
        return await CreateClient(httpClient).ChallengeConnectivityAsync(cancellationToken);
    }

    public async Task<CachedControlSnapshot?> GetLatestVerifiedSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        using var httpClient = CreateHttpClient();
        return await CreateClient(httpClient)
            .GetLatestVerifiedSourceSnapshotAsync(cancellationToken);
    }

    public async Task DownloadAndVerifyAsync(
        ReleaseManifest release,
        Stream destination,
        IProgress<PackageDownloadProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        using var httpClient = CreateHttpClient();
        httpClient.Timeout = TimeSpan.FromMinutes(10);
        await CreateClient(httpClient).DownloadAndVerifyAsync(
            release,
            destination,
            progress,
            cancellationToken);
    }

    public async Task<SubmitFeedbackResponse> SubmitFeedbackAsync(
        string type,
        string title,
        string content,
        string? contact,
        CancellationToken cancellationToken = default)
    {
        using var httpClient = CreateHttpClient();
        return await CreateClient(httpClient).SubmitFeedbackAsync(
            new SubmitFeedbackRequest
            {
                Type = type,
                Title = title,
                Content = content,
                Contact = string.IsNullOrWhiteSpace(contact) ? null : contact.Trim(),
                InstallationId = GetInstallationId(),
                Metadata = JsonSerializer.SerializeToElement(new
                {
                    clientVersion = Protocol.ProductVersion,
                    operatingSystem = OmnexaProduct.OperatingSystem,
                    architecture = OmnexaProduct.Architecture
                })
            },
            cancellationToken);
    }

    public async Task<FeedbackAttachmentResponse> UploadFeedbackAttachmentAsync(
        Guid feedbackId,
        string uploadToken,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        using var httpClient = CreateHttpClient();
        return await CreateClient(httpClient).UploadFeedbackAttachmentAsync(
            feedbackId,
            uploadToken,
            filePath,
            "application/zip",
            cancellationToken);
    }

    public async Task<IReadOnlyList<ClientFeedbackThread>> GetFeedbackHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        using var httpClient = CreateHttpClient();
        return await CreateClient(httpClient).GetFeedbackHistoryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ReleaseHistoryItem>> GetReleaseHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        using var httpClient = CreateHttpClient();
        return await CreateClient(httpClient).GetReleaseHistoryAsync(Runtime, cancellationToken);
    }

    public static string GetBaseAddress()
    {
        var overrideValue = Environment.GetEnvironmentVariable("NETRELAY_OMNEXA_BASE_URL");
        overrideValue ??= Environment.GetEnvironmentVariable("NETRELAY_BACKEND_URL");
        if (string.IsNullOrWhiteSpace(overrideValue))
        {
            return OmnexaProduct.BaseAddress;
        }

        if (!Uri.TryCreate(overrideValue, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps &&
             !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            throw new InvalidOperationException(
                "NETRELAY_OMNEXA_BASE_URL 必须是 HTTPS 地址；仅本机环回测试允许 HTTP。");
        }

        return uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? uri.AbsoluteUri
            : uri.AbsoluteUri + "/";
    }

    public static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"NetRelay/{Protocol.ProductVersion}");
        return client;
    }

    private OmnexaClient CreateClient(HttpClient httpClient) =>
        new(
            httpClient,
            new OmnexaClientOptions
            {
                BaseAddress = new Uri(GetBaseAddress(), UriKind.Absolute),
                ApiId = OmnexaProduct.ApiId,
                ApplicationId = OmnexaProduct.ApplicationId,
                EnvironmentId = OmnexaProduct.EnvironmentId,
                RootPublicKey = OmnexaProduct.RootPublicKey,
                Channel = OmnexaProduct.Channel,
                RequestTimeout = TimeSpan.FromSeconds(30)
            },
            _cache);

    private Guid GetInstallationId()
        => _configurationService.GetOrCreateRuntimeSlotId();

    private static string GetCacheDirectory(Guid runtimeSlotId) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetRelay",
            "omnexa",
            OmnexaProduct.EnvironmentId,
            runtimeSlotId.ToString("N"));
}
