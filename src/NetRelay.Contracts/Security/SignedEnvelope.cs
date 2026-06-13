using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetRelay.Contracts.Security;

public sealed class SignedEnvelope
{
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = 1;

    [JsonPropertyName("purpose")]
    public string Purpose { get; set; } = string.Empty;

    [JsonPropertyName("nonce")]
    public string Nonce { get; set; } = string.Empty;

    [JsonPropertyName("issuedAt")]
    public DateTimeOffset IssuedAt { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; set; }

    [JsonPropertyName("payloadJson")]
    public string PayloadJson { get; set; } = string.Empty;

    [JsonPropertyName("signature")]
    public string Signature { get; set; } = string.Empty;

    public static SignedEnvelope Create(
        string purpose,
        string nonce,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        IReadOnlyDictionary<string, object?> payload,
        ECDsa signingKey)
    {
        var payloadJson = CanonicalJson.Serialize(payload);
        var unsigned = CreateUnsignedBytes(purpose, nonce, issuedAt, expiresAt, payloadJson);
        var signatureBytes = signingKey.SignData(unsigned, HashAlgorithmName.SHA256);

        return new SignedEnvelope
        {
            ProtocolVersion = Protocol.CurrentVersion,
            Purpose = purpose,
            Nonce = nonce,
            IssuedAt = issuedAt,
            ExpiresAt = expiresAt,
            PayloadJson = payloadJson,
            Signature = Convert.ToBase64String(signatureBytes)
        };
    }

    public bool Verify(
        string expectedPurpose,
        string expectedNonce,
        DateTimeOffset now,
        ECDsa verificationKey,
        ISet<string>? replayCache = null)
    {
        if (!string.Equals(Purpose, expectedPurpose, StringComparison.Ordinal)
            || !string.Equals(Nonce, expectedNonce, StringComparison.Ordinal)
            || IssuedAt > now.AddMinutes(1)
            || ExpiresAt <= now
            || ExpiresAt <= IssuedAt
            || (replayCache != null && replayCache.Contains(Nonce)))
        {
            return false;
        }

        byte[] signatureBytes;
        try
        {
            signatureBytes = Convert.FromBase64String(Signature);
        }
        catch (FormatException)
        {
            return false;
        }

        var unsigned = CreateUnsignedBytes(Purpose, Nonce, IssuedAt, ExpiresAt, PayloadJson);
        var valid = verificationKey.VerifyData(unsigned, signatureBytes, HashAlgorithmName.SHA256);

        if (valid && replayCache != null)
        {
            replayCache.Add(Nonce);
        }

        return valid;
    }

    private static byte[] CreateUnsignedBytes(
        string purpose,
        string nonce,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        string payloadJson) =>
        Encoding.UTF8.GetBytes(
            $"{purpose}\n{nonce}\n{issuedAt.ToUniversalTime():O}\n{expiresAt.ToUniversalTime():O}\n{payloadJson}");
}
