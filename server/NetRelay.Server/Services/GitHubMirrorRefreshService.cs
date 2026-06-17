using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NetRelay.Server.Configuration;
using NetRelay.Server.Data;

namespace NetRelay.Server.Services;

public sealed class GitHubMirrorRefreshService(
    IServiceProvider serviceProvider,
    IOptions<ServerOptions> options,
    ReleaseMetadataService metadataService,
    GitHubReleaseMirrorService gitHubReleaseMirrorService,
    ManagedFileStorage storage,
    ILogger<GitHubMirrorRefreshService> logger) : BackgroundService
{
    private static readonly TimeSpan SuccessInterval = TimeSpan.FromHours(12);
    private static readonly TimeSpan FailureInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.GithubSyncEnabled)
        {
            logger.LogInformation("GitHub mirror refresh is disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = SuccessInterval;
            try
            {
                await SyncAsync(stoppingToken);
            }
            catch (Exception exception)
            {
                delay = FailureInterval;
                logger.LogWarning(exception, "GitHub mirror refresh failed. Will retry later.");
            }

            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task SyncAsync(CancellationToken cancellationToken)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NetRelayDbContext>();
        var publishedReleases = await dbContext.Releases
            .Where(r =>
                r.Status == "published" &&
                r.Channel == ReleaseMetadataService.StableChannel &&
                r.Architecture == ReleaseMetadataService.SupportedArchitecture)
            .ToListAsync(cancellationToken);

        var latestRelease = metadataService.SelectLatestMirrorRelease(publishedReleases);
        var history = metadataService.BuildPublishedHistory(publishedReleases);
        if (latestRelease is null)
        {
            await gitHubReleaseMirrorService.SyncRevokedStateAsync(null, history, cancellationToken);
            logger.LogInformation("GitHub mirror refreshed with no published stable releases.");
            return;
        }

        var latestResponse = metadataService.CreateSignedMirrorUpdateResponse(latestRelease);
        var packagePath = storage.Resolve(StorageArea.Releases, latestRelease.AssetPath);
        await gitHubReleaseMirrorService.SyncPublishedReleaseAsync(latestRelease, packagePath, latestResponse, history, cancellationToken);
        logger.LogInformation("GitHub mirror refreshed for version {Version}.", latestRelease.Version);
    }
}
