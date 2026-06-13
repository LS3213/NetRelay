using Microsoft.Extensions.Options;
using NetRelay.Server.Configuration;

namespace NetRelay.Server.Services;

public enum StorageArea
{
    Releases,
    Feedback,
    Staging,
    Quarantine
}

public sealed class ManagedFileStorage
{
    private readonly IReadOnlyDictionary<StorageArea, string> _roots;
    private readonly StringComparison _pathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public ManagedFileStorage(IOptions<ServerOptions> options)
    {
        _roots = new Dictionary<StorageArea, string>
        {
            [StorageArea.Releases] = NormalizeRoot(options.Value.ReleasesRoot),
            [StorageArea.Feedback] = NormalizeRoot(options.Value.FeedbackRoot),
            [StorageArea.Staging] = NormalizeRoot(options.Value.StagingRoot),
            [StorageArea.Quarantine] = NormalizeRoot(options.Value.QuarantineRoot)
        };
    }

    public void EnsureDirectories()
    {
        foreach (var root in _roots.Values)
        {
            Directory.CreateDirectory(root);
        }
    }

    public string Resolve(StorageArea area, params string[] segments)
    {
        if (segments.Length == 0 || segments.Any(static segment => string.IsNullOrWhiteSpace(segment)))
        {
            throw new ArgumentException("At least one non-empty relative path segment is required.", nameof(segments));
        }

        var root = _roots[area];
        var candidate = root;
        foreach (var segment in segments)
        {
            if (Path.IsPathFullyQualified(segment))
            {
                throw new InvalidOperationException("Absolute storage path segments are not allowed.");
            }

            candidate = Path.Combine(candidate, segment);
        }

        var resolved = Path.GetFullPath(candidate);
        if (!resolved.StartsWith(root, _pathComparison))
        {
            throw new InvalidOperationException("Storage path escapes its managed root.");
        }

        return resolved;
    }

    public async Task MoveFromStagingAsync(
        string stagingRelativePath,
        StorageArea destinationArea,
        string destinationRelativePath,
        CancellationToken cancellationToken = default)
    {
        if (destinationArea is StorageArea.Staging)
        {
            throw new ArgumentException("Destination must not be the staging area.", nameof(destinationArea));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var source = Resolve(StorageArea.Staging, stagingRelativePath);
        var destination = Resolve(destinationArea, destinationRelativePath);
        var destinationDirectory = Path.GetDirectoryName(destination) ??
            throw new InvalidOperationException("Destination directory is unavailable.");
        Directory.CreateDirectory(destinationDirectory);

        await Task.Run(() => File.Move(source, destination, overwrite: false), cancellationToken);
    }

    private static string NormalizeRoot(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
}
