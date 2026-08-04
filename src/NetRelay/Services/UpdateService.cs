using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NetRelay.Contracts;
using NetRelay.Models;
using Omnexa.Sdk;

namespace NetRelay.Services;

public sealed record DownloadedUpdatePackage(string PackagePath, string ManifestPath);

public sealed class UpdateService
{
    private const string StableChannel = "stable";
    private readonly ConfigurationService _configService;
    private readonly OmnexaIntegrationService _omnexa;
    private readonly DiagnosticLogService _diagnosticLog = new();
    private UpdateStatusSnapshot _status = UpdateStatusSnapshot.Empty;

    public event EventHandler<UpdateStatusSnapshot>? StatusChanged;

    public UpdateStatusSnapshot GetStatusSnapshot() => _status;

    public UpdateService(ConfigurationService configService)
    {
        _configService = configService;
        _omnexa = new OmnexaIntegrationService(configService);
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
                        config.ClientConfigurationVersion = 3;
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
        var now = DateTimeOffset.UtcNow;
        await _diagnosticLog.InfoAsync(
            "update",
            "check",
            "started",
            detail: $"source=omnexa; channel={StableChannel}; currentVersion={currentVersion}");
        UpdateStatus(
            lastCheckedAt: now,
            lastCheckOutcome: "checking",
            lastCheckMessage: "正在通过 Omnexa 检查更新...",
            lastCheckSource: UpdateSourceKind.Primary);

        try
        {
            // A manual or scheduled update check must query a fresh signed
            // control snapshot. CurrentControl is only the last policy that
            // was applied at startup, so reusing it hides versions published
            // while the application remains open.
            var policyService = App.PolicyService;
            UpdateSourceKind checkSource;
            Omnexa.Core.ControlSnapshot control;
            if (policyService is not null)
            {
                await policyService.CheckPolicyAsync(cancellationToken);
                control = policyService.CurrentControl
                    ?? throw new InvalidOperationException("Omnexa 未返回控制快照。");
                checkSource = ToUpdateSourceKind(policyService.CurrentControlSource);
            }
            else
            {
                var syncResult = await _omnexa.SyncWithSourceAsync(cancellationToken);
                control = syncResult.Snapshot;
                checkSource = ToUpdateSourceKind(syncResult.Source);
            }

            if (control.Release is null)
            {
                var message = checkSource switch
                {
                    UpdateSourceKind.CachedOffline => "当前处于离线状态，无法确认是否有新版本。",
                    UpdateSourceKind.GitHubFallback => "备用更新源未发现可用更新。",
                    _ => $"当前已是最新版本 (v{currentVersion})"
                };
                await _diagnosticLog.InfoAsync("update", "check", "not-available");
                UpdateStatus(
                    lastCheckedAt: DateTimeOffset.Now,
                    lastCheckOutcome: checkSource == UpdateSourceKind.CachedOffline ? "offline" : "up-to-date",
                    lastCheckMessage: message,
                    lastCheckSource: checkSource,
                    availableVersion: null,
                    lastCheckFoundUpdate: false);
                return null;
            }

            if (!ReleaseVersionPolicy.IsStrictlyNewer(control.Release.Version, currentVersion))
            {
                var message = checkSource switch
                {
                    UpdateSourceKind.CachedOffline =>
                        $"离线可信缓存中的版本不高于当前 v{currentVersion}，无法确认服务器是否另有新版本。",
                    UpdateSourceKind.GitHubFallback =>
                        $"备用更新源未发现高于当前 v{currentVersion} 的更新。",
                    _ => $"当前已是最新版本 (v{currentVersion})"
                };
                await _diagnosticLog.InfoAsync(
                    "update",
                    "check",
                    "not-newer",
                    detail: $"candidate={control.Release.Version}; current={currentVersion}; source={checkSource}");
                UpdateStatus(
                    lastCheckedAt: DateTimeOffset.Now,
                    lastCheckOutcome: checkSource == UpdateSourceKind.CachedOffline ? "offline" : "up-to-date",
                    lastCheckMessage: message,
                    lastCheckSource: checkSource,
                    availableVersion: null,
                    lastCheckFoundUpdate: false);
                return null;
            }

            var manifest = ToLegacyManifest(control.Release);
            manifest.SourceKind = checkSource;
            await _diagnosticLog.InfoAsync(
                "update",
                "check",
                "available",
                detail: $"version={manifest.Version}; mandatory={manifest.IsMandatory}");
            UpdateStatus(
                lastCheckedAt: DateTimeOffset.Now,
                lastCheckOutcome: "available",
                lastCheckMessage: checkSource == UpdateSourceKind.CachedOffline
                    ? $"离线可信缓存显示新版本 v{manifest.Version}。"
                    : $"发现新版本 v{manifest.Version}（Omnexa 已验签）",
                lastCheckSource: checkSource,
                availableVersion: manifest.Version,
                lastCheckFoundUpdate: true);
            return manifest;
        }
        catch (Exception exception)
        {
            await _diagnosticLog.ErrorAsync("update", "check-omnexa", exception);
            var status = UpdateStatus(
                lastCheckedAt: DateTimeOffset.Now,
                lastCheckOutcome: "failed",
                lastCheckMessage: "Omnexa 更新服务和可信缓存暂时不可用，请稍后重试。",
                lastCheckSource: UpdateSourceKind.Primary,
                availableVersion: null,
                lastCheckFoundUpdate: false);
            throw new InvalidOperationException(status.LastCheckMessage, exception);
        }
    }

    public async Task<IReadOnlyList<UpdateHistoryItem>> GetHistoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var history = await _omnexa.GetReleaseHistoryAsync(cancellationToken);
            return history
                .Select(release => new UpdateHistoryItem
                {
                    Version = release.Version,
                    Channel = release.Channel,
                    Architecture = release.Architecture,
                    ReleaseDate = release.PublishedAt,
                    Changelog = release.ReleaseNotes,
                    IsMandatory = release.IsMandatory
                })
                .ToList();
        }
        catch (Exception exception)
        {
            await _diagnosticLog.ErrorAsync("update", "history-omnexa", exception);
            throw;
        }
    }

    private static UpdateManifest ToLegacyManifest(
        Omnexa.Core.ReleaseManifest release) =>
        new()
        {
            Id = release.Id,
            Version = release.Version,
            Channel = release.Channel,
            OperatingSystem = release.OperatingSystem,
            Architecture = release.Architecture,
            PackageFormat = release.PackageFormat,
            MinUpgradableVersion = release.MinimumUpgradableVersion,
            PackageSize = release.PackageSize,
            Sha256 = release.Sha256,
            ReleaseDate = release.PublishedAt,
            Changelog = release.ReleaseNotes,
            IsMandatory = release.IsMandatory,
            DownloadUrl = release.DownloadUrl,
            FallbackDownloadUrls = new[] { release.FallbackDownloadUrl }
                .Concat(release.FallbackDownloadUrls ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };

    private static Omnexa.Core.ReleaseManifest ToOmnexaManifest(
        UpdateManifest manifest) =>
        new(
            manifest.Id,
            manifest.Version,
            manifest.Channel,
            manifest.OperatingSystem,
            manifest.Architecture,
            manifest.PackageFormat,
            manifest.MinUpgradableVersion,
            manifest.PackageSize,
            manifest.Sha256,
            manifest.IsMandatory,
            manifest.Changelog,
            manifest.ReleaseDate,
            manifest.DownloadUrl,
            manifest.FallbackDownloadUrls.FirstOrDefault(),
            manifest.FallbackDownloadUrls);

    private static UpdateSourceKind ToUpdateSourceKind(ControlSnapshotSource source) => source switch
    {
        ControlSnapshotSource.Primary => UpdateSourceKind.Primary,
        ControlSnapshotSource.GitHubFallback => UpdateSourceKind.GitHubFallback,
        _ => UpdateSourceKind.CachedOffline
    };

    public async Task<DownloadedUpdatePackage> DownloadPackageAsync(
        UpdateManifest manifest,
        Action<UpdateDownloadProgress>? progressCallback,
        CancellationToken cancellationToken)
    {
        await _diagnosticLog.InfoAsync(
            "update",
            "download",
            "started",
            detail: $"version={manifest.Version}; expectedBytes={manifest.PackageSize}; source=omnexa");
        UpdateStatus(
            lastDownloadAt: DateTimeOffset.Now,
            lastDownloadOutcome: "started",
            lastDownloadMessage: $"开始下载 v{manifest.Version}（Omnexa 主源 → 签名备用源）",
            lastDownloadSource: UpdateSourceKind.Primary,
            lastDownloadedBytes: 0);

        var tempDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetRelay", "updates");
        Directory.CreateDirectory(tempDirectory);
        var tempFilePath = Path.Combine(tempDirectory, $"update-{manifest.Version}.zip");
        var downloadFilePath = Path.Combine(tempDirectory, $"update-{manifest.Version}.{Guid.NewGuid():N}.download");

        try
        {
            progressCallback?.Invoke(new UpdateDownloadProgress(
                "connecting",
                "正在连接 Omnexa 更新源...",
                null,
                UpdateSourceKind.Primary,
                0,
                manifest.PackageSize));
            var omnexaProgress = new Progress<PackageDownloadProgress>(progress =>
            {
                var source = progress.SourceIndex == 0
                    ? UpdateSourceKind.Primary
                    : UpdateSourceKind.GitHubFallback;
                var message = progress.SourceIndex == 0
                    ? "正在从 Omnexa 更新源下载更新包..."
                    : "主更新源不可用，正在从备用更新源下载...";
                double? percent = progress.TotalBytes > 0
                    ? Math.Clamp((double)progress.BytesReceived / progress.TotalBytes, 0, 1)
                    : null;
                progressCallback?.Invoke(new UpdateDownloadProgress(
                    "downloading",
                    message,
                    percent,
                    source,
                    progress.BytesReceived,
                    progress.TotalBytes));
            });
            await using (var destination = new FileStream(
                             downloadFilePath,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await _omnexa.DownloadAndVerifyAsync(
                    ToOmnexaManifest(manifest),
                    destination,
                    omnexaProgress,
                    cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            progressCallback?.Invoke(new UpdateDownloadProgress(
                "verifying",
                "下载完成，正在准备独立更新器的签名复核...",
                0.98,
                UpdateSourceKind.Primary,
                manifest.PackageSize,
                manifest.PackageSize));

            var signedSnapshot = await _omnexa.GetLatestVerifiedSnapshotAsync(cancellationToken)
                ?? throw new CryptographicException("缺少 Omnexa 已验签控制快照，无法启动独立更新器。");
            var signedControl = signedSnapshot.Envelope.Payload.Deserialize<Omnexa.Core.ControlSnapshot>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new CryptographicException("Omnexa 控制快照载荷为空。");
            if (signedControl.Release?.Id != manifest.Id)
            {
                throw new CryptographicException("已验签控制快照不包含当前更新版本。");
            }

            await MoveFileWithRetryAsync(downloadFilePath, tempFilePath, cancellationToken);
            var manifestPath = Path.Combine(tempDirectory, $"update-{manifest.Version}.omnexa.json");
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(
                    new
                    {
                        snapshot = signedSnapshot.Envelope,
                        certificate = signedSnapshot.Certificate,
                        releaseId = manifest.Id
                    },
                    new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8,
                cancellationToken);

            await _diagnosticLog.InfoAsync(
                "update",
                "download",
                "completed",
                bytes: manifest.PackageSize,
                detail: $"version={manifest.Version}; verifier=omnexa-root");
            UpdateStatus(
                lastDownloadAt: DateTimeOffset.Now,
                lastDownloadOutcome: "completed",
                lastDownloadMessage: $"已完成下载并校验 v{manifest.Version}（Omnexa）",
                lastDownloadSource: UpdateSourceKind.Primary,
                lastDownloadedBytes: manifest.PackageSize);
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
                bytes: File.Exists(downloadFilePath)
                    ? new FileInfo(downloadFilePath).Length
                    : null);
            UpdateStatus(
                lastDownloadAt: DateTimeOffset.Now,
                lastDownloadOutcome: "failed",
                lastDownloadMessage: ClassifyDownloadFailure(exception),
                lastDownloadSource: UpdateSourceKind.Primary,
                lastDownloadedBytes: File.Exists(downloadFilePath)
                    ? new FileInfo(downloadFilePath).Length
                    : 0);
            throw;
        }
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

    private sealed class BootstrapConfig
    {
        [JsonPropertyName("primaryApiBaseUrl")]
        public string PrimaryApiBaseUrl { get; set; } = string.Empty;

        [JsonPropertyName("githubFallback")]
        public GithubFallbackOptions? GithubFallback { get; set; }
    }
}
