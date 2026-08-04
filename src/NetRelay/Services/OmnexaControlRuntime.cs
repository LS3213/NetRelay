namespace NetRelay.Services;

public sealed class OmnexaControlRuntime : IDisposable
{
    private readonly PolicyService _policyService;
    private readonly ActivationService _activationService;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _syncLoop;
    private Task? _heartbeatLoop;

    public OmnexaControlRuntime(
        PolicyService policyService,
        ActivationService activationService)
    {
        _policyService = policyService;
        _activationService = activationService;
    }

    public void Start()
    {
        _syncLoop ??= Task.Run(() => RunSyncLoopAsync(_cancellation.Token));
        _heartbeatLoop ??= Task.Run(
            () => RunHeartbeatLoopAsync(_cancellation.Token));
    }

    private async Task RunSyncLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(_policyService.GetNextSyncDelay(), cancellationToken);
            try
            {
                await _policyService.CheckPolicyAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                await new DiagnosticLogService().ErrorAsync(
                    "omnexa",
                    "periodic-sync",
                    exception);
            }
        }
    }

    private async Task RunHeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _activationService.SendHeartbeatAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                await new DiagnosticLogService().ErrorAsync(
                    "omnexa",
                    "periodic-heartbeat",
                    exception);
            }

            await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
    }
}
