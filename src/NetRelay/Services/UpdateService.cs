using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NetRelay.Contracts;
using NetRelay.Contracts.Security;
using NetRelay.Models;

namespace NetRelay.Services;

public sealed record DownloadedUpdatePackage(string PackagePath, string ManifestPath);

public sealed class UpdateService
{
    private const string StableChannel = "stable";
    private const string UpdateArchitecture = "win-x64";
    private readonly ConfigurationService _configService;
    private readonly DiagnosticLogService _diagnosticLog = new();
    private static readonly string RootPublicKey = OperationalKeyCertificate.DefaultRootPublicKeyBase64;
    private UpdateStatusSnapshot _status = UpdateStatusSnapshot.Empty;

    public event EventHandler<UpdateStatusSnapshot>? StatusChanged;

    public UpdateStatusSnapshot GetStatusSnapshot() => _status;

    public UpdateService(ConfigurationService configService)
    {
        _configService = configService;
        InitializeDefaultsFromBootstrap();
    }

    private void InitializeDefaultsFromBootstrap()
    {
        var config = _configService.Current;
        if (config.ClientConfigurationVersion == 0)
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream("NetRelay.bootstrap-config.json");
                if (stream != null)
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var json = reader.ReadToEnd();
                    var bootstrap = JsonSerializer.Deserialize<BootstrapConfig>(json);
                    if (bootstrap != null)
                    {
                        config.PrimaryApiBaseUrl = bootstrap.PrimaryApiBaseUrl;
                        config.GithubFallback = bootstrap.GithubFallback ?? new GithubFallbackOptions();
                        config.ClientConfigurationVersion = 2;
                        _configService.Save();
                    }
                }
            }
            catch
            {
                // Ignored, fallback to hardcoded defaults in AppConfiguration
            }
        }
    }

    public async Task<UpdateManifest?> CheckForUpdatesAsync(string currentVersion, CancellationToken cancellationToken)
    {
        var config = _configService.Current;
        var now = DateTimeOffset.UtcNow;
        Exception? primaryException = null;
        Exception? fallbackException = null;
        UpdateStatusSnapshot status;
        await _diagnosticLog.InfoAsync("update", "check", "started", detail: $"source=primary; channel={StableChannel}; currentVersion={currentVersion}");
        UpdateStatus(lastCheckedAt: now, lastCheckOutcome: "checking", lastCheckMessage: "正在检查主更新源...", lastCheckSource: UpdateSourceKind.Primary);

        // Try primary source first
        try
        {
            var manifest = await CheckPrimarySourceAsync(config.PrimaryApiBaseUrl, StableChannel, currentVersion, now, cancellationToken);
            if (manifest != null)
            {
                manifest.SourceKind = UpdateSourceKind.Primary;
                await _diagnosticLog.InfoAsync("update", "check", "available", detail: $"version={manifest.Version}; mandatory={manifest.IsMandatory}");
                UpdateStatus(
                    lastCheckedAt: DateTimeOffset.Now,
                    lastCheckOutcome: "available",
                    lastCheckMessage: $"发现新版本 v{manifest.Version}（来源：主更新源）",
                    lastCheckSource: UpdateSourceKind.Primary,
                    availableVersion: manifest.Version,
                    lastCheckFoundUpdate: true);
                return manifest;
            }
        }
        catch (NoUpdateAvailableException noUpdateException)
        {
            await _diagnosticLog.InfoAsync("update", "check", "not-available", detail: noUpdateException.Message);
            UpdateStatus(
                lastCheckedAt: DateTimeOffset.Now,
                lastCheckOutcome: "up-to-date",
                lastCheckMessage: $"当前已是最新版本 (v{currentVersion})",
                lastCheckSource: UpdateSourceKind.Primary,
                availableVersion: null,
                lastCheckFoundUpdate: false);
            return null;
        }
        catch (Exception ex)
        {
            primaryException = ex;
            await _diagnosticLog.ErrorAsync("update", "check-primary", ex);
        }

        // Fallback source if enabled.
        if (config.GithubFallback.Enabled && !string.IsNullOrWhiteSpace(config.GithubFallback.Repository))
        {
            try
            {
                var manifest = await CheckGitHubSourceAsync(config.GithubFallback, currentVersion, now, cancellationToken);
                await _diagnosticLog.InfoAsync("update", "check-fallback", "available");
                manifest.SourceKind = UpdateSourceKind.GitHubFallback;
                UpdateStatus(
                    lastCheckedAt: DateTimeOffset.Now,
                    lastCheckOutcome: "available",
                    lastCheckMessage: $"发现新版本 v{manifest.Version}（来源：备用源）",
                    lastCheckSource: UpdateSourceKind.GitHubFallback,
                    availableVersion: manifest.Version,
                    lastCheckFoundUpdate: true);
                return manifest;
            }
            catch (NoUpdateAvailableException noUpdateException)
            {
                await _diagnosticLog.InfoAsync("update", "check-fallback", "not-available", detail: noUpdateException.Message);
                UpdateStatus(
                    lastCheckedAt: DateTimeOffset.Now,
                    lastCheckOutcome: "up-to-date",
                    lastCheckMessage: $"当前已是最新版本 (v{currentVersion}，备用源确认)",
                    lastCheckSource: UpdateSourceKind.GitHubFallback,
                    availableVersion: null,
                    lastCheckFoundUpdate: false);
                return null;
            }
            catch (Exception ex)
            {
                fallbackException = ex;
                await _diagnosticLog.ErrorAsync("update", "check-fallback", ex);
            }
        }

        if (primaryException != null || fallbackException != null)
        {
            status = UpdateStatus(
                lastCheckedAt: DateTimeOffset.Now,
                lastCheckOutcome: "failed",
                lastCheckMessage: "版本更新服务暂时不可用，请稍后重试。",
                lastCheckSource: fallbackException != null ? UpdateSourceKind.GitHubFallback : UpdateSourceKind.Primary,
                availableVersion: null,
                lastCheckFoundUpdate: false);
            throw new InvalidOperationException(status.LastCheckMessage, fallbackException ?? primaryException);
        }

        await _diagnosticLog.InfoAsync("update", "check", "not-available");
        UpdateStatus(
            lastCheckedAt: DateTimeOffset.Now,
            lastCheckOutcome: "up-to-date",
            lastCheckMessage: $"当前已是最新版本 (v{currentVersion})",
            lastCheckSource: UpdateSourceKind.Primary,
            availableVersion: null,
            lastCheckFoundUpdate: false);
        return null;
    }

    private async Task<UpdateManifest?> CheckPrimarySourceAsync(
        string baseUrl,
        string channel,
        string currentVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var client = ActivationService.CreateHttpClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        
        var requestUrl = $"{baseUrl.TrimEnd('/')}/api/v1/updates/latest?channel={channel}&architecture=win-x64&currentVersion={Uri.EscapeDataString(currentVersion)}";
        var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        request.Headers.Add(Protocol.VersionHeader, Protocol.CurrentVersion.ToString());
        request.Headers.Add(Protocol.ClientVersionHeader, currentVersion);
        var requestId = Guid.NewGuid().ToString("N");
        request.Headers.Add(Protocol.RequestIdHeader, requestId);

        var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await _diagnosticLog.InfoAsync("update", "check-primary-response", "not-success", requestId, (int)response.StatusCode);
            var apiError = await TryReadApiErrorAsync(response, cancellationToken);
            if (IsUpdateNotAvailableResponse(response.StatusCode, apiError))
            {
                throw new NoUpdateAvailableException(apiError?.Error.Message ?? "当前已是最新版本。");
            }

            throw new HttpRequestException(
                $"主更新源检查失败（HTTP {(int)response.StatusCode} {response.ReasonPhrase}）。",
                null,
                response.StatusCode);
        }
        await _diagnosticLog.InfoAsync("update", "check-primary-response", "success", requestId, (int)response.StatusCode);

        var apiResponse = await response.Content.ReadFromJsonAsync<ApiResponse<UpdateCheckResponse>>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, 
            cancellationToken);

        if (apiResponse?.Data == null)
        {
            return null;
        }

        return VerifyAndExtractManifest(apiResponse.Data, now);
    }

    private static async Task<ApiErrorResponse?> TryReadApiErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ApiErrorResponse>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsUpdateNotAvailableResponse(HttpStatusCode statusCode, ApiErrorResponse? apiError) =>
        statusCode == HttpStatusCode.NotFound &&
        string.Equals(apiError?.Error.Code, ErrorCodes.UpdateNotAvailable, StringComparison.Ordinal);

    public async Task<IReadOnlyList<UpdateHistoryItem>> GetHistoryAsync(CancellationToken cancellationToken)
    {
        var config = _configService.Current;
        using var client = ActivationService.CreateHttpClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        var requestId = Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{config.PrimaryApiBaseUrl.TrimEnd('/')}/api/v1/updates/history?channel={Uri.EscapeDataString(StableChannel)}&architecture={UpdateArchitecture}");
        request.Headers.Add(Protocol.VersionHeader, Protocol.CurrentVersion.ToString());
        request.Headers.Add(Protocol.ClientVersionHeader, Protocol.ProductVersion);
        request.Headers.Add(Protocol.RequestIdHeader, requestId);

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            await _diagnosticLog.InfoAsync("update", "history-response", response.IsSuccessStatusCode ? "success" : "not-success", requestId, (int)response.StatusCode);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<ApiResponse<List<UpdateHistoryItem>>>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);
            return payload?.Data ?? [];
        }
        catch (Exception exception)
        {
            await _diagnosticLog.ErrorAsync("update", "history", exception, requestId: requestId);
            if (!config.GithubFallback.Enabled || string.IsNullOrWhiteSpace(config.GithubFallback.Repository))
            {
                throw;
            }

            try
            {
                var fallback = await GetGitHubHistoryAsync(config.GithubFallback, cancellationToken);
                await _diagnosticLog.InfoAsync("update", "history-fallback", "success", requestId);
                return fallback;
            }
            catch (Exception fallbackException)
            {
                await _diagnosticLog.ErrorAsync("update", "history-fallback", fallbackException, requestId: requestId);
                throw;
            }
        }
    }

    private async Task<UpdateManifest> CheckGitHubSourceAsync(
        GithubFallbackOptions fallback,
        string currentVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NetRelay-Client");

        var requestUrl = $"{BuildGitHubMetadataBaseUrl(fallback)}/stable/{UpdateArchitecture}/latest.json";
        var response = await client.GetAsync(requestUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"备用源检查失败（HTTP {(int)response.StatusCode} {response.ReasonPhrase}）。",
                null,
                response.StatusCode);
        }

        var manifestJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var checkResponse = JsonSerializer.Deserialize<UpdateCheckResponse>(manifestJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (checkResponse == null)
        {
            throw new InvalidDataException("备用源更新信息无效。");
        }

        var manifest = VerifyAndExtractManifest(checkResponse, now);
        if (manifest is null)
        {
            throw new InvalidDataException("备用源更新信息为空。");
        }

        if (CompareVersions(manifest.Version, currentVersion) <= 0)
        {
            throw new NoUpdateAvailableException("备用源确认当前已是最新版本。");
        }

        return manifest;
    }

    private UpdateManifest? VerifyAndExtractManifest(UpdateCheckResponse checkResponse, DateTimeOffset now)
    {
        // 1. Verify operational certificate using offline Root public key
        using var rootKey = ECDsa.Create();
        rootKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(RootPublicKey), out _);

        if (!checkResponse.Certificate.Verify(now, rootKey, "update-manifest"))
        {
            throw new CryptographicException("在线操作证书验证失败（签名不符或已过期）。");
        }

        // 2. Verify signature envelope
        using var operationalKey = ECDsa.Create();
        operationalKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(checkResponse.Certificate.PublicKey), out _);

        if (!checkResponse.Envelope.Verify("update-manifest", checkResponse.Envelope.Nonce, now, operationalKey))
        {
            throw new CryptographicException("更新清单签名校验失败。");
        }

        // 3. Extract UpdateManifest payload
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(checkResponse.Envelope.PayloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (manifest != null)
        {
            manifest.VerifiedResponse = checkResponse;
        }
        return manifest;
    }

    public async Task<DownloadedUpdatePackage> DownloadPackageAsync(
        UpdateManifest manifest,
        Action<UpdateDownloadProgress>? progressCallback,
        CancellationToken cancellationToken)
    {
        var config = _configService.Current;
        var filename = config.GithubFallback.AssetName;
        var primaryUrl = $"{config.PrimaryApiBaseUrl.TrimEnd('/')}/api/v1/updates/{manifest.Version}/download/{filename}";
        await _diagnosticLog.InfoAsync("update", "download", "started", detail: $"version={manifest.Version}; expectedBytes={manifest.PackageSize}");
        UpdateStatus(
            lastDownloadAt: DateTimeOffset.Now,
            lastDownloadOutcome: "started",
            lastDownloadMessage: $"开始下载 v{manifest.Version}（优先使用主更新源）",
            lastDownloadSource: manifest.SourceKind == UpdateSourceKind.Unknown ? UpdateSourceKind.Primary : manifest.SourceKind,
            lastDownloadedBytes: 0);

        // Execute download with progress reporting and SHA256 hashing
        var tempDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetRelay", "updates");
        Directory.CreateDirectory(tempDirectory);
        var tempFilePath = Path.Combine(tempDirectory, $"update-{manifest.Version}.zip");
        var downloadFilePath = Path.Combine(tempDirectory, $"update-{manifest.Version}.{Guid.NewGuid():N}.download");

        using var primaryClient = ActivationService.CreateHttpClient();
        primaryClient.Timeout = TimeSpan.FromMinutes(5);
        using var primaryRequest = CreatePrimaryDownloadRequest(primaryUrl);
        using var primaryResponse = await primaryClient.SendAsync(
            primaryRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        HttpResponseMessage response = primaryResponse;
        HttpClient? fallbackClient = null;
        HttpResponseMessage? fallbackResponse = null;
        var downloadSource = UpdateSourceKind.Primary;
        if (!primaryResponse.IsSuccessStatusCode)
        {
            if (!config.GithubFallback.Enabled || string.IsNullOrWhiteSpace(config.GithubFallback.Repository))
            {
                throw new HttpRequestException(
                    $"主更新源下载失败（HTTP {(int)primaryResponse.StatusCode} {primaryResponse.ReasonPhrase}），且未启用可用的备用源。",
                    null,
                    primaryResponse.StatusCode);
            }

            var githubUrl = BuildGitHubReleaseAssetUrl(config.GithubFallback, manifest.Version);
            fallbackClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            fallbackClient.DefaultRequestHeaders.UserAgent.ParseAdd("NetRelay-Client");
            fallbackResponse = await fallbackClient.GetAsync(githubUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response = fallbackResponse;
            downloadSource = UpdateSourceKind.GitHubFallback;
        }

        try
        {
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? manifest.PackageSize;

            using var sha256 = SHA256.Create();
            long totalRead = 0;
            try
            {
                progressCallback?.Invoke(new UpdateDownloadProgress(
                    "connecting",
                    $"已连接{GetSourceLabel(downloadSource)}，正在接收更新包...",
                    0,
                    downloadSource,
                    0,
                    totalBytes));
                await using (var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var destStream = new FileStream(
                    downloadFilePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[81920];
                    int read;

                    while ((read = await sourceStream.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await destStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        sha256.TransformBlock(buffer, 0, read, null, 0);

                        totalRead += read;
                        if (totalBytes > 0)
                        {
                            progressCallback?.Invoke(new UpdateDownloadProgress(
                                "downloading",
                                $"正在从{GetSourceLabel(downloadSource)}下载更新包...",
                                (double)totalRead / totalBytes,
                                downloadSource,
                                totalRead,
                                totalBytes));
                        }
                    }

                    await destStream.FlushAsync(cancellationToken);
                }

                progressCallback?.Invoke(new UpdateDownloadProgress(
                    "verifying",
                    "下载完成，正在校验哈希...",
                    0.98,
                    downloadSource,
                    totalRead,
                    totalBytes));
                sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var downloadedHash = Convert.ToHexString(sha256.Hash!).ToLower();

                if (!string.Equals(downloadedHash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    await _diagnosticLog.InfoAsync("update", "download-verify", "hash-mismatch", bytes: totalRead);
                    throw new CryptographicException($"下载包哈希值不匹配。预期: {manifest.Sha256}，实际: {downloadedHash}");
                }

                await MoveFileWithRetryAsync(downloadFilePath, tempFilePath, cancellationToken);

                var verifiedResponse = manifest.VerifiedResponse
                    ?? throw new CryptographicException("缺少已验证的更新清单，无法启动独立更新器。");
                var manifestPath = Path.Combine(tempDirectory, $"update-{manifest.Version}.manifest.json");
                await File.WriteAllTextAsync(
                    manifestPath,
                    JsonSerializer.Serialize(verifiedResponse, new JsonSerializerOptions { WriteIndented = true }),
                    Encoding.UTF8,
                    cancellationToken);

                await _diagnosticLog.InfoAsync("update", "download", "completed", bytes: totalRead, detail: $"version={manifest.Version}");
                UpdateStatus(
                    lastDownloadAt: DateTimeOffset.Now,
                    lastDownloadOutcome: "completed",
                    lastDownloadMessage: $"已完成下载并校验 v{manifest.Version}（来源：{GetSourceLabel(downloadSource)}）",
                    lastDownloadSource: downloadSource,
                    lastDownloadedBytes: totalRead);
                return new DownloadedUpdatePackage(tempFilePath, manifestPath);
            }
            catch (Exception exception)
            {
                if (File.Exists(downloadFilePath))
                {
                    try
                    {
                        File.Delete(downloadFilePath);
                    }
                    catch (IOException)
                    {
                        // A scanner may briefly retain the failed temporary file.
                    }
                }
                await _diagnosticLog.ErrorAsync(
                    "update",
                    "download",
                    exception,
                    bytes: File.Exists(downloadFilePath) ? new FileInfo(downloadFilePath).Length : null);
                UpdateStatus(
                    lastDownloadAt: DateTimeOffset.Now,
                    lastDownloadOutcome: "failed",
                    lastDownloadMessage: ClassifyDownloadFailure(exception),
                    lastDownloadSource: downloadSource,
                    lastDownloadedBytes: File.Exists(downloadFilePath) ? new FileInfo(downloadFilePath).Length : totalRead);
                throw;
            }
        }
        finally
        {
            fallbackResponse?.Dispose();
            fallbackClient?.Dispose();
        }
    }

    private static HttpRequestMessage CreatePrimaryDownloadRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Protocol.VersionHeader, Protocol.CurrentVersion.ToString());
        request.Headers.Add(Protocol.ClientVersionHeader, Protocol.ProductVersion);
        request.Headers.Add(Protocol.RequestIdHeader, Guid.NewGuid().ToString("N"));
        return request;
    }

    private static async Task MoveFileWithRetryAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        const int maxAttempts = 20;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                File.Move(sourcePath, destinationPath, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                await Task.Delay(250, cancellationToken);
            }
        }
    }

    private async Task<IReadOnlyList<UpdateHistoryItem>> GetGitHubHistoryAsync(
        GithubFallbackOptions fallback,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NetRelay-Client");
        var requestUrl = $"{BuildGitHubMetadataBaseUrl(fallback)}/stable/{UpdateArchitecture}/history.json";
        var response = await client.GetAsync(requestUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<List<UpdateHistoryItem>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }

    private static string BuildGitHubMetadataBaseUrl(GithubFallbackOptions fallback)
    {
        var parts = fallback.Repository.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            throw new InvalidOperationException("备用源配置无效。");
        }

        return $"https://{parts[0]}.github.io/{parts[1]}/updates";
    }

    private static string BuildGitHubReleaseAssetUrl(GithubFallbackOptions fallback, string version)
    {
        var tag = $"{fallback.ReleaseTagPrefix}{version}";
        return $"https://github.com/{fallback.Repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(fallback.AssetName)}";
    }

    private UpdateStatusSnapshot UpdateStatus(
        DateTimeOffset? lastCheckedAt = null,
        string? lastCheckOutcome = null,
        string? lastCheckMessage = null,
        UpdateSourceKind? lastCheckSource = null,
        string? availableVersion = null,
        bool? lastCheckFoundUpdate = null,
        DateTimeOffset? lastDownloadAt = null,
        string? lastDownloadOutcome = null,
        string? lastDownloadMessage = null,
        UpdateSourceKind? lastDownloadSource = null,
        long? lastDownloadedBytes = null)
    {
        _status = _status with
        {
            LastCheckedAt = lastCheckedAt ?? _status.LastCheckedAt,
            LastCheckOutcome = lastCheckOutcome ?? _status.LastCheckOutcome,
            LastCheckMessage = lastCheckMessage ?? _status.LastCheckMessage,
            LastCheckSource = lastCheckSource ?? _status.LastCheckSource,
            AvailableVersion = availableVersion,
            LastCheckFoundUpdate = lastCheckFoundUpdate ?? _status.LastCheckFoundUpdate,
            LastDownloadAt = lastDownloadAt ?? _status.LastDownloadAt,
            LastDownloadOutcome = lastDownloadOutcome ?? _status.LastDownloadOutcome,
            LastDownloadMessage = lastDownloadMessage ?? _status.LastDownloadMessage,
            LastDownloadSource = lastDownloadSource ?? _status.LastDownloadSource,
            LastDownloadedBytes = lastDownloadedBytes ?? _status.LastDownloadedBytes
        };
        StatusChanged?.Invoke(this, _status);
        return _status;
    }

    private static string ClassifyDownloadFailure(Exception exception)
    {
        return exception switch
        {
            CryptographicException => "更新包校验失败：签名或哈希不匹配。",
            HttpRequestException httpEx when httpEx.StatusCode is not null => $"更新包下载失败：HTTP {(int)httpEx.StatusCode}。",
            IOException => "更新包写入失败：文件被占用、磁盘不可写或空间不足。",
            UnauthorizedAccessException => "更新包写入失败：当前目录需要管理员权限。",
            _ => $"更新包下载失败：{exception.Message}"
        };
    }

    private static string GetSourceLabel(UpdateSourceKind source)
    {
        return source switch
        {
            UpdateSourceKind.Primary => "主更新源",
            UpdateSourceKind.GitHubFallback => "备用源",
            _ => "更新源"
        };
    }

    private static int CompareVersions(string? left, string? right)
    {
        var leftIsVersion = Version.TryParse(left, out var leftVersion);
        var rightIsVersion = Version.TryParse(right, out var rightVersion);
        if (leftIsVersion && rightIsVersion)
        {
            return leftVersion!.CompareTo(rightVersion);
        }
        if (leftIsVersion)
        {
            return 1;
        }
        if (rightIsVersion)
        {
            return -1;
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class BootstrapConfig
    {
        [JsonPropertyName("primaryApiBaseUrl")]
        public string PrimaryApiBaseUrl { get; set; } = string.Empty;

        [JsonPropertyName("githubFallback")]
        public GithubFallbackOptions? GithubFallback { get; set; }
    }

    private sealed class NoUpdateAvailableException(string message) : Exception(message);
}
