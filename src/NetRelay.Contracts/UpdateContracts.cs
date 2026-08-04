using System;
using System.Text.Json.Serialization;
using NetRelay.Contracts.Security;

namespace NetRelay.Contracts;

public enum UpdateSourceKind
{
    Unknown = 0,
    Primary = 1,
    GitHubFallback = 2,
    CachedOffline = 3
}

public sealed class UpdateManifest
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("architecture")]
    public string Architecture { get; set; } = string.Empty;

    [JsonPropertyName("operatingSystem")]
    public string OperatingSystem { get; set; } = string.Empty;

    [JsonPropertyName("packageFormat")]
    public string PackageFormat { get; set; } = string.Empty;

    [JsonPropertyName("minUpgradableVersion")]
    public string MinUpgradableVersion { get; set; } = string.Empty;

    [JsonPropertyName("packageSize")]
    public long PackageSize { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("releaseDate")]
    public DateTimeOffset ReleaseDate { get; set; }

    [JsonPropertyName("changelog")]
    public string Changelog { get; set; } = string.Empty;

    [JsonPropertyName("isMandatory")]
    public bool IsMandatory { get; set; }

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("fallbackDownloadUrls")]
    public IReadOnlyList<string> FallbackDownloadUrls { get; set; } = [];

    [JsonIgnore]
    public UpdateCheckResponse? VerifiedResponse { get; set; }

    [JsonIgnore]
    public UpdateSourceKind SourceKind { get; set; }

    [JsonIgnore]
    public string SourceLabel => SourceKind switch
    {
        UpdateSourceKind.Primary => "主更新源",
        UpdateSourceKind.GitHubFallback => "备用源",
        UpdateSourceKind.CachedOffline => "本地可信缓存",
        _ => "未知来源"
    };
}

public sealed class UpdateHistoryItem
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("architecture")]
    public string Architecture { get; set; } = string.Empty;

    [JsonPropertyName("releaseDate")]
    public DateTimeOffset ReleaseDate { get; set; }

    [JsonPropertyName("changelog")]
    public string Changelog { get; set; } = string.Empty;

    [JsonPropertyName("isMandatory")]
    public bool IsMandatory { get; set; }
}

public sealed record ReleaseCleanupRequest(int KeepLatestPublished);

public sealed record ReleaseCleanupResponse(
    int Scanned,
    int DeletedFiles,
    long FreedBytes,
    IReadOnlyList<string> DeletedVersions);

public sealed class UpdateCheckResponse
{
    [JsonPropertyName("envelope")]
    public SignedEnvelope Envelope { get; set; } = new();

    [JsonPropertyName("certificate")]
    public OperationalKeyCertificate Certificate { get; set; } = new();
}
