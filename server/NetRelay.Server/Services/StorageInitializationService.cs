namespace NetRelay.Server.Services;

public sealed class StorageInitializationService(
    ManagedFileStorage storage,
    ILogger<StorageInitializationService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        storage.EnsureDirectories();
        logger.LogInformation("Managed storage roots initialized.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
