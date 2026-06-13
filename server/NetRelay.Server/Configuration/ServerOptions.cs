using System.ComponentModel.DataAnnotations;

namespace NetRelay.Server.Configuration;

public sealed class ServerOptions
{
    public const string SectionName = "NetRelay";

    [Required]
    public string PublicBaseUrl { get; init; } = string.Empty;

    [Required]
    public string GithubRepository { get; init; } = string.Empty;

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

        return null;
    }
}
