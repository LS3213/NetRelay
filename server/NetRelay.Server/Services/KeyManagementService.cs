using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRelay.Contracts.Security;
using NetRelay.Server.Configuration;

namespace NetRelay.Server.Services;

public sealed class KeyManagementService
{
    private const string DefaultRootPrivateKeyBase64 = "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQg/NXpocWMQ1bT+yu5MBByIOOmU02n3b2a5R0tvedzwoOhRANCAAQi91R4JEsCFDshD0vZV3OHU93ThiX26vaTrtQ3e9u3nxsKpN6H5afJE9TwNgUtSsj9ShDWfDWNkf5vp+Xm0UVK";
    public const string DefaultRootPublicKeyBase64 = OperationalKeyCertificate.DefaultRootPublicKeyBase64;

    private readonly IOptions<ServerOptions> _options;
    private readonly ILogger<KeyManagementService> _logger;
    private readonly object _lock = new();

    private ECDsa? _operationKey;
    private OperationalKeyCertificate? _operationCertificate;

    public KeyManagementService(IOptions<ServerOptions> options, ILogger<KeyManagementService> logger)
    {
        _options = options;
        _logger = logger;
        InitializeKeys();
    }

    public ECDsa OperationPrivateKey
    {
        get
        {
            lock (_lock)
            {
                EnsureValidKeys();
                return _operationKey!;
            }
        }
    }

    public OperationalKeyCertificate OperationCertificate
    {
        get
        {
            lock (_lock)
            {
                EnsureValidKeys();
                return _operationCertificate!;
            }
        }
    }

    private void InitializeKeys()
    {
        lock (_lock)
        {
            EnsureValidKeys();
        }
    }

    private void EnsureValidKeys()
    {
        var keysDirectory = _options.Value.KeysRoot;
        if (!Directory.Exists(keysDirectory))
        {
            Directory.CreateDirectory(keysDirectory);
        }

        var keyPath = Path.Combine(keysDirectory, "operation.key");
        var certPath = Path.Combine(keysDirectory, "operation.crt");

        bool keysValid = false;

        if (File.Exists(keyPath) && File.Exists(certPath))
        {
            try
            {
                var keyBytes = Convert.FromBase64String(File.ReadAllText(keyPath));
                var certJson = File.ReadAllText(certPath);
                var cert = System.Text.Json.JsonSerializer.Deserialize<OperationalKeyCertificate>(certJson);

                if (cert != null)
                {
                    var now = DateTimeOffset.UtcNow;
                    // 如果证书在未来 2 天内过期，或者当前时间不在有效期内，则重新生成
                    if (now >= cert.NotBefore && now < cert.NotAfter.AddDays(-2) && cert.AllowedPurposes.Contains("update-manifest") && cert.AllowedPurposes.Contains("announcement"))
                    {
                        var key = ECDsa.Create();
                        key.ImportPkcs8PrivateKey(keyBytes, out _);
                        _operationKey = key;
                        _operationCertificate = cert;
                        keysValid = true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "加载缓存的在线操作密钥或证书失败，将重新生成。");
            }
        }

        if (!keysValid)
        {
            GenerateNewOperationKey(keyPath, certPath);
        }
    }

    private void GenerateNewOperationKey(string keyPath, string certPath)
    {
        _logger.LogInformation("正在生成新的在线操作密钥与证书...");

        var operationKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var operationPublicKeyBytes = operationKey.ExportSubjectPublicKeyInfo();
        var operationPrivateKeyBytes = operationKey.ExportPkcs8PrivateKey();

        // 获取 Root 私钥
        var rootPrivateKeyBase64 = Environment.GetEnvironmentVariable("NETRELAY_ROOT_PRIVATE_KEY");
        if (string.IsNullOrWhiteSpace(rootPrivateKeyBase64))
        {
            _logger.LogWarning("未配置环境变量 NETRELAY_ROOT_PRIVATE_KEY，将使用默认内置的 Root 私钥（非生产安全）。");
            rootPrivateKeyBase64 = DefaultRootPrivateKeyBase64;
        }

        using var rootKey = ECDsa.Create();
        rootKey.ImportPkcs8PrivateKey(Convert.FromBase64String(rootPrivateKeyBase64), out _);

        var now = DateTimeOffset.UtcNow;
        var keyId = "operations-" + now.ToString("yyyyMMdd-HHmmss");
        var cert = OperationalKeyCertificate.Create(
            keyId,
            operationPublicKeyBytes,
            new[] { "connectivity-challenge", "client-configuration", "device-activation", "heartbeat", "update-manifest", "announcement" },
            now.AddDays(-1),
            now.AddDays(30),
            rootKey);

        File.WriteAllText(keyPath, Convert.ToBase64String(operationPrivateKeyBytes));
        File.WriteAllText(certPath, System.Text.Json.JsonSerializer.Serialize(cert, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        _operationKey = operationKey;
        _operationCertificate = cert;

        _logger.LogInformation("新的在线操作密钥与证书生成并持久化成功。KeyId: {KeyId}", keyId);
    }

    public SignedEnvelope Sign(string purpose, string nonce, DateTimeOffset issuedAt, DateTimeOffset expiresAt, IReadOnlyDictionary<string, object?> payload)
    {
        lock (_lock)
        {
            EnsureValidKeys();
            return SignedEnvelope.Create(purpose, nonce, issuedAt, expiresAt, payload, _operationKey!);
        }
    }
}
