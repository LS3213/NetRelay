using System.ComponentModel.DataAnnotations;

namespace NetRelay.Server.Configuration;

public sealed record class ServerOptions
{
    public const string SectionName = "NetRelay";

    [Required]
    public string PublicBaseUrl { get; init; } = string.Empty;

    [Required]
    public string GithubRepository { get; init; } = string.Empty;

    public bool GithubSyncEnabled { get; init; }

    public string GithubToken { get; init; } = string.Empty;

    [Required]
    public string GithubPagesBranch { get; init; } = "gh-pages";

    [Required]
    public string GithubReleaseTagPrefix { get; init; } = "v";

    [Required]
    public string GithubAssetName { get; init; } = "win-x64.zip";

    [Required]
    public string ReleasesRoot { get; init; } = string.Empty;

    [Required]
    public string FeedbackRoot { get; init; } = string.Empty;

    [Required]
    public string StagingRoot { get; init; } = string.Empty;

    [Required]
    public string QuarantineRoot { get; init; } = string.Empty;

    public bool AutoMigrate { get; init; }

    [Range(5, 1440)]
    public int AdminSessionMinutes { get; init; } = 480;

    [Range(1, 60)]
    public int AdminReauthenticationMinutes { get; init; } = 10;

    [Range(1, 30)]
    public int LoginChallengeMinutes { get; init; } = 5;

    [Range(1, 20)]
    public int MaximumLoginFailures { get; init; } = 5;

    [Range(1, 1440)]
    public int AccountLockoutMinutes { get; init; } = 15;

    [Required]
    public string KeysRoot { get; init; } = string.Empty;

    [Range(1, 10)]
    public int MinCoreEvidenceMatches { get; init; } = 2;

    [Range(1, 100)]
    public int MinEvidenceScore { get; init; } = 40;

    [Range(1, 100)]
    public int CoreEvidenceWeight { get; init; } = 20;

    [Range(1, 100)]
    public int NetworkEvidenceWeight { get; init; } = 2;
}

public static class ServerOptionsValidator
{
    public static string? Validate(ServerOptions options)
    {
        if (!Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out var publicUri) ||
            !string.Equals(publicUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(publicUri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            publicUri.Host.EndsWith(".example", StringComparison.OrdinalIgnoreCase))
        {
            return "NetRelay:PublicBaseUrl must be a non-placeholder absolute HTTPS URL.";
        }

        var repositoryParts = options.GithubRepository.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (repositoryParts.Length != 2 ||
            repositoryParts.Any(static part => part is "." or "..") ||
            string.Equals(options.GithubRepository, "owner/repository", StringComparison.OrdinalIgnoreCase))
        {
            return "NetRelay:GithubRepository must use owner/repository format.";
        }

        if (options.GithubSyncEnabled && string.IsNullOrWhiteSpace(options.GithubToken))
        {
            return "NetRelay:GithubToken is required when GitHub sync is enabled.";
        }

        if (string.IsNullOrWhiteSpace(options.GithubPagesBranch))
        {
            return "NetRelay:GithubPagesBranch is required.";
        }

        if (string.IsNullOrWhiteSpace(options.GithubAssetName))
        {
            return "NetRelay:GithubAssetName is required.";
        }

        string[] storageRoots;
        try
        {
            storageRoots =
            [
                NormalizeRoot(options.ReleasesRoot),
                NormalizeRoot(options.FeedbackRoot),
                NormalizeRoot(options.StagingRoot),
                NormalizeRoot(options.QuarantineRoot),
                NormalizeRoot(options.KeysRoot)
            ];
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return "All NetRelay storage roots must be valid absolute paths.";
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (var left = 0; left < storageRoots.Length; left++)
        {
            for (var right = left + 1; right < storageRoots.Length; right++)
            {
                if (storageRoots[left].StartsWith(storageRoots[right], comparison) ||
                    storageRoots[right].StartsWith(storageRoots[left], comparison))
                {
                    return "NetRelay storage roots must be distinct and must not contain one another.";
                }
            }
        }

        return null;
    }

    private static string NormalizeRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Storage root must be absolute.", nameof(path));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
    }
}
