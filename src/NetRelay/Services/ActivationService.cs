using System;
using System.Collections.Generic;
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

public sealed class ActivationService
{
    private readonly ConfigurationService _configService;
    private readonly CompositeDeviceFingerprintProvider _fingerprintProvider = new();

    public ActivationService(ConfigurationService configService)
    {
        _configService = configService;
    }

    public static string GetBackendUrl()
    {
        var envUrl = Environment.GetEnvironmentVariable("NETRELAY_BACKEND_URL");
        if (!string.IsNullOrWhiteSpace(envUrl))
        {
            return envUrl.TrimEnd('/');
        }
        return "https://netrelay.473700.xyz";
    }

    public async Task<bool> ActivateAsync(CancellationToken cancellationToken)
    {
        var config = _configService.Current;
        var now = DateTimeOffset.Now;

        // 1. 收集指纹证据
        var fingerprintEvidence = await _fingerprintProvider.CollectAsync(cancellationToken);
        
        // 计算聚合 DeviceIdHash
        var combinedString = string.Join("|", fingerprintEvidence.Evidence
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .SelectMany(p => p.Value));
        var deviceIdHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(combinedString)));

        // 2. 构造请求 DTO
        var request = new DeviceActivationRequest
        {
            InstallationId = Guid.Parse(config.InstallationId),
            FingerprintVersion = fingerprintEvidence.Version,
            DeviceId = deviceIdHash,
            Evidence = fingerprintEvidence.Evidence.ToDictionary(p => p.Key, p => p.Value.ToList()),
            AcceptedTermsVersion = "1.0",
            AcceptedPrivacyVersion = "1.0",
            ClientVersion = "1.0.0",
            OsVersion = Environment.OSVersion.ToString(),
            ProtocolVersion = Protocol.CurrentVersion
        };

        // 3. 发送请求
        using var client = new HttpClient();
        var requestMessage = new HttpRequestMessage(HttpMethod.Post, GetBackendUrl() + "/api/v1/devices/activate")
        {
            Content = JsonContent.Create(request)
        };
        requestMessage.Headers.Add(Protocol.VersionHeader, Protocol.CurrentVersion.ToString());
        requestMessage.Headers.Add(Protocol.ClientVersionHeader, "1.0.0");
        requestMessage.Headers.Add(Protocol.RequestIdHeader, Guid.NewGuid().ToString("N"));

        var response = await client.SendAsync(requestMessage, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var apiResponse = await response.Content.ReadFromJsonAsync<ApiResponse<DeviceActivationResponse>>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, 
            cancellationToken);

        if (apiResponse?.Data == null)
        {
            return false;
        }

        var activationResponse = apiResponse.Data;

        // 4. 验证在线证书
        using var rootKey = ECDsa.Create();
        rootKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(OperationalKeyCertificate.DefaultRootPublicKeyBase64), out _);

        if (!activationResponse.Certificate.Verify(now, rootKey, "device-activation"))
        {
            return false;
        }

        // 5. 验证激活回执签名
        using var operationalKey = ECDsa.Create();
        operationalKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(activationResponse.Certificate.PublicKey), out _);

        if (!activationResponse.Envelope.Verify("device-activation", activationResponse.Envelope.Nonce, now, operationalKey))
        {
            return false;
        }

        // 6. 保存至本地配置
        config.MachineCode = activationResponse.MachineCode;
        config.ActivationReceipt = JsonSerializer.Serialize(activationResponse.Envelope);
        config.PrivacyConsentAccepted = true;
        config.AcceptedTermsVersion = "1.0";
        config.AcceptedPrivacyVersion = "1.0";
        config.PrivacyConsentTimestamp = now;

        _configService.Save();
        return true;
    }

    public async Task<bool> SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        var config = _configService.Current;
        if (string.IsNullOrWhiteSpace(config.ActivationReceipt) || string.IsNullOrWhiteSpace(config.MachineCode))
        {
            return false;
        }

        var now = DateTimeOffset.Now;

        // 如果上次心跳在一小时内（避免频繁请求），我们直接限流，返回 true
        if (config.LastHeartbeatTimestamp.HasValue && config.LastHeartbeatTimestamp.Value.AddHours(24) > now)
        {
            return true;
        }

        SignedEnvelope receiptEnvelope;
        try
        {
            receiptEnvelope = JsonSerializer.Deserialize<SignedEnvelope>(config.ActivationReceipt)!;
        }
        catch
        {
            return false;
        }

        var request = new DeviceHeartbeatRequest
        {
            InstallationId = Guid.Parse(config.InstallationId),
            MachineCode = config.MachineCode,
            ClientVersion = "1.0.0",
            OsVersion = Environment.OSVersion.ToString(),
            ReceiptEnvelope = receiptEnvelope
        };

        using var client = new HttpClient();
        var requestMessage = new HttpRequestMessage(HttpMethod.Post, GetBackendUrl() + "/api/v1/devices/heartbeat")
        {
            Content = JsonContent.Create(request)
        };
        requestMessage.Headers.Add(Protocol.VersionHeader, Protocol.CurrentVersion.ToString());
        requestMessage.Headers.Add(Protocol.ClientVersionHeader, "1.0.0");
        requestMessage.Headers.Add(Protocol.RequestIdHeader, Guid.NewGuid().ToString("N"));

        var response = await client.SendAsync(requestMessage, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        config.LastHeartbeatTimestamp = now;
        _configService.Save();
        return true;
    }
}
