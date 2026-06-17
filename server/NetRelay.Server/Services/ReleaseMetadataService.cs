using System.Globalization;
using NetRelay.Contracts;
using NetRelay.Server.Data;

namespace NetRelay.Server.Services;

public sealed class ReleaseMetadataService(KeyManagementService keyManagementService)
{
    public const string StableChannel = "stable";
    public const string SupportedArchitecture = "win-x64";
    private static readonly TimeSpan PrimaryResponseLifetime = TimeSpan.FromMinutes(5);

    public bool IsSupportedPublicChannel(string channel) =>
        string.Equals(channel, StableChannel, StringComparison.OrdinalIgnoreCase);

    public bool IsSupportedArchitecture(string architecture) =>
        string.Equals(architecture, SupportedArchitecture, StringComparison.OrdinalIgnoreCase);

    public Release? SelectLatestPrimaryRelease(IEnumerable<Release> releases, string channel, string architecture)
    {
        if (!IsSupportedPublicChannel(channel) || !IsSupportedArchitecture(architecture))
        {
            return null;
        }

        return releases
            .Where(r =>
                r.Status == "published" &&
                r.PackageDeletedAt == null &&
                string.Equals(r.Channel, StableChannel, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.Architecture, SupportedArchitecture, StringComparison.OrdinalIgnoreCase))
            .MaxBy(release => release.Version, Comparer<string>.Create(CompareReleaseVersions));
    }

    public Release? SelectLatestMirrorRelease(IEnumerable<Release> releases)
    {
        return releases
            .Where(r =>
                r.Status == "published" &&
                string.Equals(r.Channel, StableChannel, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.Architecture, SupportedArchitecture, StringComparison.OrdinalIgnoreCase))
            .MaxBy(release => release.Version, Comparer<string>.Create(CompareReleaseVersions));
    }

    public IReadOnlyList<UpdateHistoryItem> BuildPublishedHistory(IEnumerable<Release> releases)
    {
        var items = releases
            .Where(r =>
                r.Status == "published" &&
                string.Equals(r.Channel, StableChannel, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.Architecture, SupportedArchitecture, StringComparison.OrdinalIgnoreCase))
            .Select(r => new UpdateHistoryItem
            {
                Version = r.Version,
                Channel = StableChannel,
                Architecture = SupportedArchitecture,
                ReleaseDate = r.ReleaseDate,
                Changelog = r.Changelog,
                IsMandatory = r.IsMandatory
            })
            .ToList();

        items.Sort((left, right) => CompareReleaseVersions(right.Version, left.Version));
        return items;
    }

    public UpdateCheckResponse CreateSignedUpdateResponse(Release release)
    {
        var issuedAt = DateTimeOffset.UtcNow;
        return CreateSignedUpdateResponseCore(release, issuedAt, issuedAt.Add(PrimaryResponseLifetime));
    }

    public UpdateCheckResponse CreateSignedMirrorUpdateResponse(Release release)
    {
        var certificate = keyManagementService.OperationCertificate;
        var issuedAt = DateTimeOffset.UtcNow;
        return CreateSignedUpdateResponseCore(release, issuedAt, certificate.NotAfter);
    }

    private UpdateCheckResponse CreateSignedUpdateResponseCore(Release release, DateTimeOffset issuedAt, DateTimeOffset expiresAt)
    {
        var manifest = new UpdateManifest
        {
            Version = release.Version,
            Channel = StableChannel,
            Architecture = SupportedArchitecture,
            MinUpgradableVersion = release.MinUpgradableVersion,
            PackageSize = release.PackageSize,
            Sha256 = release.Sha256,
            ReleaseDate = release.ReleaseDate,
            Changelog = release.Changelog,
            IsMandatory = release.IsMandatory
        };

        var payload = new Dictionary<string, object?>
        {
            ["version"] = manifest.Version,
            ["channel"] = manifest.Channel,
            ["architecture"] = manifest.Architecture,
            ["minUpgradableVersion"] = manifest.MinUpgradableVersion,
            ["packageSize"] = manifest.PackageSize,
            ["sha256"] = manifest.Sha256,
            ["releaseDate"] = manifest.ReleaseDate.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            ["changelog"] = manifest.Changelog,
            ["isMandatory"] = manifest.IsMandatory
        };

        var envelope = keyManagementService.Sign(
            "update-manifest",
            Guid.NewGuid().ToString("N"),
            issuedAt,
            expiresAt,
            payload);

        return new UpdateCheckResponse
        {
            Envelope = envelope,
            Certificate = keyManagementService.OperationCertificate
        };
    }

    public static int CompareReleaseVersions(string? left, string? right)
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
}
