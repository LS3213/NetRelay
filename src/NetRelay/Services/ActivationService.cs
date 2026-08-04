using System.Net.Http;
using System.Text.Json;
using NetRelay.Contracts;
using Omnexa.Sdk;

namespace NetRelay.Services;

public sealed class ActivationService
{
    private readonly ConfigurationService _configService;
    private readonly OmnexaIntegrationService _omnexa;

    public string? LastFailureCode { get; private set; }
    public string? LastFailureMessage { get; private set; }

    public ActivationService(ConfigurationService configService)
    {
        _configService = configService;
        _omnexa = new OmnexaIntegrationService(configService);
    }

    public static string GetBackendUrl() =>
        OmnexaIntegrationService.GetBaseAddress().TrimEnd('/');

    public static HttpClient CreateHttpClient() =>
        OmnexaIntegrationService.CreateHttpClient();

    public Task<bool> HasCachedActivationAsync(
        CancellationToken cancellationToken = default) =>
        _omnexa.HasCachedActivationAsync(cancellationToken);

    public async Task<bool> ActivateAsync(CancellationToken cancellationToken)
    {
        LastFailureCode = null;
        LastFailureMessage = null;
        try
        {
            var activation = await _omnexa.ActivateAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var config = _configService.Current;
            config.MachineCode = activation.DeviceCode;
            config.ActivationReceipt = JsonSerializer.Serialize(activation);
            config.PrivacyConsentAccepted = true;
            config.AcceptedTermsVersion = _omnexa.CurrentTermsVersion;
            config.AcceptedPrivacyVersion = _omnexa.CurrentPrivacyVersion;
            config.PrivacyConsentTimestamp ??= now;
            config.NextHeartbeatTimestamp = null;
            _configService.Save();
            return true;
        }
        catch (OmnexaApiException exception)
        {
            LastFailureCode = exception.Code;
            LastFailureMessage = exception.Message;
            await new DiagnosticLogService().ErrorAsync(
                "omnexa",
                "activation",
                exception,
                requestId: exception.RequestId,
                httpStatus: exception.StatusCode,
                detail:
                    $"code={exception.Code}; retryable={exception.Retryable}; " +
                    $"retryAfterSeconds={exception.RetryAfterSeconds}; message={exception.Message}");
            return false;
        }
        catch (Exception exception)
        {
            LastFailureCode = exception.GetType().Name;
            LastFailureMessage = exception.Message;
            await new DiagnosticLogService().ErrorAsync(
                "omnexa",
                "activation",
                exception);
            return false;
        }
    }

    public async Task<bool> SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        var config = _configService.Current;
        var now = DateTimeOffset.UtcNow;
        if (config.NextHeartbeatTimestamp is { } nextHeartbeat &&
            nextHeartbeat > now &&
            string.Equals(
                config.LastHeartbeatClientVersion,
                Protocol.ProductVersion,
                StringComparison.Ordinal))
        {
            return true;
        }

        if (!await HasCachedActivationAsync(cancellationToken) &&
            !await ActivateAsync(cancellationToken))
        {
            return false;
        }

        try
        {
            return await SendHeartbeatCoreAsync(cancellationToken);
        }
        catch (OmnexaApiException exception) when (
            exception.Code is "INSTALLATION_REQUIRED" or "ACTIVATION_RECEIPT_INVALID")
        {
            if (!await ActivateAsync(cancellationToken))
            {
                return false;
            }

            return await SendHeartbeatCoreAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            await new DiagnosticLogService().ErrorAsync(
                "omnexa",
                "heartbeat",
                exception);
            return false;
        }
    }

    private async Task<bool> SendHeartbeatCoreAsync(
        CancellationToken cancellationToken)
    {
        var response = await _omnexa.HeartbeatAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var interval = TimeSpan.FromSeconds(Math.Clamp(
            response.NextHeartbeatSeconds,
            60,
            7 * 24 * 60 * 60));
        var jitter = 0.9 + Random.Shared.NextDouble() * 0.2;

        var config = _configService.Current;
        config.LastHeartbeatTimestamp = now;
        config.LastHeartbeatClientVersion = Protocol.ProductVersion;
        config.NextHeartbeatTimestamp = now.AddSeconds(interval.TotalSeconds * jitter);
        _configService.Save();

        // 激活与心跳均应静默维护离线基线。同步失败不否定已经
        // 成功的心跳；Omnexa.Sdk 会优先尝试备用源和本地签名快照。
        try
        {
            await _omnexa.SyncAsync(cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            await new DiagnosticLogService().ErrorAsync(
                "omnexa",
                "heartbeat-control-sync",
                exception);
        }

        return true;
    }
}
