using NetRelay.Contracts;

namespace NetRelay.Models;

public sealed record UpdateStatusSnapshot(
    DateTimeOffset? LastCheckedAt,
    string LastCheckOutcome,
    string LastCheckMessage,
    UpdateSourceKind LastCheckSource,
    string? AvailableVersion,
    bool LastCheckFoundUpdate,
    DateTimeOffset? LastDownloadAt,
    string LastDownloadOutcome,
    string LastDownloadMessage,
    UpdateSourceKind LastDownloadSource,
    long? LastDownloadedBytes)
{
    public static UpdateStatusSnapshot Empty { get; } = new(
        null,
        "unknown",
        "尚未检查更新",
        UpdateSourceKind.Unknown,
        null,
        false,
        null,
        "unknown",
        "尚未下载更新",
        UpdateSourceKind.Unknown,
        null);
}

public sealed record UpdateDownloadProgress(
    string Stage,
    string Message,
    double? Percent,
    UpdateSourceKind Source,
    long BytesReceived,
    long? TotalBytes);

public sealed record UpdatePreflightResult(
    bool Success,
    bool RequiresElevation,
    string Summary,
    IReadOnlyList<string> Issues,
    long? RequiredBytes,
    long? AvailableBytes,
    string UpdaterPath,
    string TargetDirectory);
