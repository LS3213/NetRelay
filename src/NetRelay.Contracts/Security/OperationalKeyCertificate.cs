using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace NetRelay.Contracts.Security;

public sealed class OperationalKeyCertificate
{
    public const string DefaultRootPublicKeyBase64 = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEIvdUeCRLAhQ7IQ9L2Vdzh1Pd04Yl9ur2k67UN3vbt58bCqTeh+WnyRPU8DYFLUrI/UoQ1nw1jZH+b6fl5tFFSg==";

    [JsonPropertyName("keyId")]
    public string KeyId { get; set; } = string.Empty;

    [JsonPropertyName("publicKey")]
    public string PublicKey { get; set; } = string.Empty; // Base64 encoded SubjectPublicKeyInfo

    [JsonPropertyName("allowedPurposes")]
    public List<string> AllowedPurposes { get; set; } = [];

    [JsonPropertyName("notBefore")]
    public DateTimeOffset NotBefore { get; set; }

    [JsonPropertyName("notAfter")]
    public DateTimeOffset NotAfter { get; set; }

    [JsonPropertyName("rootSignature")]
    public string RootSignature { get; set; } = string.Empty; // Base64 encoded root signature

    public static OperationalKeyCertificate Create(
        string keyId,
        byte[] publicKey,
        IReadOnlyList<string> allowedPurposes,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter,
        ECDsa rootKey)
    {
        var normalizedPurposes = allowedPurposes.OrderBy(value => value, StringComparer.Ordinal).ToList();
        var publicKeyBase64 = Convert.ToBase64String(publicKey);
        var unsigned = GetUnsignedBytes(keyId, publicKeyBase64, normalizedPurposes, notBefore, notAfter);
        var signatureBytes = rootKey.SignData(unsigned, HashAlgorithmName.SHA256);

        return new OperationalKeyCertificate
        {
            KeyId = keyId,
            PublicKey = publicKeyBase64,
            AllowedPurposes = normalizedPurposes,
            NotBefore = notBefore,
            NotAfter = notAfter,
            RootSignature = Convert.ToBase64String(signatureBytes)
        };
    }

    public bool Verify(DateTimeOffset now, ECDsa rootKey, string requiredPurpose)
    {
        if (now < NotBefore || now >= NotAfter || !AllowedPurposes.Contains(requiredPurpose, StringComparer.Ordinal))
        {
            return false;
        }

        byte[] signatureBytes;
        try
        {
            signatureBytes = Convert.FromBase64String(RootSignature);
        }
        catch (FormatException)
        {
            return false;
        }

        var unsigned = GetUnsignedBytes(KeyId, PublicKey, AllowedPurposes, NotBefore, NotAfter);
        return rootKey.VerifyData(unsigned, signatureBytes, HashAlgorithmName.SHA256);
    }

    private static byte[] GetUnsignedBytes(
        string keyId,
        string publicKey,
        IEnumerable<string> purposes,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter) =>
        Encoding.UTF8.GetBytes(
            $"{keyId}\n{publicKey}\n{string.Join(',', purposes)}\n{notBefore.ToUniversalTime():O}\n{notAfter.ToUniversalTime():O}");
}
