using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetRelay.Contracts;
using NetRelay.Contracts.Security;

namespace NetRelay.Services;

public sealed class PolicyService
{
    private readonly ConfigurationService _configService;
    private readonly CompositeDeviceFingerprintProvider _fingerprintProvider = new();
    
    public bool IsBlocked { get; private set; }
    public string? Reason { get; private set; }
    public string? AppealUrl { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public bool AllowUpdate { get; private set; } = true;

    public event EventHandler? BlockStateChanged;

    public PolicyService(ConfigurationService configService)
    {
        _configService = configService;
        LoadPersistedBlockState();
    }

    private void LoadPersistedBlockState()
    {
        var state = _configService.Current.PersistedBlockState;
        if (state == null)
        {
            return;
        }

        try
        {
            if (VerifyStateEnvelope(state))
            {
                var payload = JsonSerializer.Deserialize<PolicyEvaluationResult>(
                    state.Envelope.PayloadJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (payload != null && payload.IsBlocked)
                {
                    var now = DateTimeOffset.UtcNow;
                    if (payload.ExpiresAt == null || payload.ExpiresAt.Value > now)
                    {
                        IsBlocked = true;
                        Reason = payload.Reason;
                        AppealUrl = payload.AppealUrl;
                        ExpiresAt = payload.ExpiresAt;
                        AllowUpdate = payload.AllowUpdate;
                    }
                    else
                    {
                        IsBlocked = false;
                        Reason = null;
                        AppealUrl = null;
                        ExpiresAt = null;
                        AllowUpdate = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PolicyService] Failed to load/verify persisted block state: {ex.Message}");
        }
    }

    public bool VerifyStateEnvelope(PolicyEvaluateResponse response)
    {
        try
        {
            using var rootKey = ECDsa.Create();
            rootKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(OperationalKeyCertificate.DefaultRootPublicKeyBase64), out _);

            if (!response.Certificate.Verify(DateTimeOffset.UtcNow, rootKey, "policy"))
            {
                return false;
            }

            using var opKey = ECDsa.Create();
            opKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(response.Certificate.PublicKey), out _);

            if (!response.Envelope.Verify("policy", response.Envelope.Nonce, DateTimeOffset.UtcNow, opKey))
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task CheckPolicyAsync(CancellationToken cancellationToken = default)
    {
        bool wasBlocked = IsBlocked;
        string? oldReason = Reason;

        try
        {
            var fingerprintEvidence = await _fingerprintProvider.CollectAsync(cancellationToken);
            var combinedString = string.Join("|", fingerprintEvidence.Evidence
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .SelectMany(p => p.Value));
            var deviceIdHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(combinedString)));

            using var client = new HttpClient();
            var backendUrl = ActivationService.GetBackendUrl();
            var request = new PolicyEvaluateRequest
            {
                DeviceId = deviceIdHash,
                InstallationId = Guid.Parse(_configService.Current.InstallationId),
                ClientVersion = "1.0.0"
            };

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, $"{backendUrl}/api/v1/policies/evaluate")
            {
                Content = JsonContent.Create(request)
            };
            requestMessage.Headers.Add(Protocol.VersionHeader, Protocol.CurrentVersion.ToString());
            requestMessage.Headers.Add(Protocol.ClientVersionHeader, "1.0.0");
            requestMessage.Headers.Add(Protocol.RequestIdHeader, Guid.NewGuid().ToString("N"));

            var response = await client.SendAsync(requestMessage, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // Network error: maintain existing state (from persisted configuration)
                return;
            }

            var apiResponse = await response.Content.ReadFromJsonAsync<ApiResponse<PolicyEvaluateResponse>>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);

            if (apiResponse?.Data == null)
            {
                return;
            }

            var policyResponse = apiResponse.Data;

            if (!VerifyStateEnvelope(policyResponse))
            {
                System.Diagnostics.Debug.WriteLine("[PolicyService] Policy signature verification failed.");
                return;
            }

            var payload = JsonSerializer.Deserialize<PolicyEvaluationResult>(
                policyResponse.Envelope.PayloadJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (payload == null)
            {
                return;
            }

            IsBlocked = payload.IsBlocked;
            Reason = payload.Reason;
            AppealUrl = payload.AppealUrl;
            ExpiresAt = payload.ExpiresAt;
            AllowUpdate = payload.AllowUpdate;

            if (IsBlocked)
            {
                _configService.Current.PersistedBlockState = policyResponse;
            }
            else
            {
                _configService.Current.PersistedBlockState = null;
            }
            _configService.Save();

            if (wasBlocked != IsBlocked || oldReason != Reason)
            {
                BlockStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PolicyService] Error checking policy: {ex.Message}");
        }
    }
}
