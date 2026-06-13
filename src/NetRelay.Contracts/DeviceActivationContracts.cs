using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using NetRelay.Contracts.Security;

namespace NetRelay.Contracts;

public sealed class DeviceActivationRequest
{
    [JsonPropertyName("installationId")]
    public Guid InstallationId { get; set; }

    [JsonPropertyName("fingerprintVersion")]
    public int FingerprintVersion { get; set; }

    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("evidence")]
    public Dictionary<string, List<string>> Evidence { get; set; } = [];

    [JsonPropertyName("acceptedTermsVersion")]
    public string AcceptedTermsVersion { get; set; } = string.Empty;

    [JsonPropertyName("acceptedPrivacyVersion")]
    public string AcceptedPrivacyVersion { get; set; } = string.Empty;

    [JsonPropertyName("clientVersion")]
    public string ClientVersion { get; set; } = string.Empty;

    [JsonPropertyName("osVersion")]
    public string OsVersion { get; set; } = string.Empty;

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; }
}

public sealed class DeviceActivationResponse
{
    [JsonPropertyName("machineCode")]
    public string MachineCode { get; set; } = string.Empty;

    [JsonPropertyName("envelope")]
    public SignedEnvelope Envelope { get; set; } = new();

    [JsonPropertyName("certificate")]
    public OperationalKeyCertificate Certificate { get; set; } = new();
}

public sealed class DeviceHeartbeatRequest
{
    [JsonPropertyName("installationId")]
    public Guid InstallationId { get; set; }

    [JsonPropertyName("machineCode")]
    public string MachineCode { get; set; } = string.Empty;

    [JsonPropertyName("clientVersion")]
    public string ClientVersion { get; set; } = string.Empty;

    [JsonPropertyName("osVersion")]
    public string OsVersion { get; set; } = string.Empty;

    [JsonPropertyName("receiptEnvelope")]
    public SignedEnvelope ReceiptEnvelope { get; set; } = new();
}

public sealed class ConnectivityChallengeRequest
{
    [JsonPropertyName("nonce")]
    public string Nonce { get; set; } = string.Empty;
}

public sealed class ConnectivityChallengeResponse
{
    [JsonPropertyName("envelope")]
    public SignedEnvelope Envelope { get; set; } = new();

    [JsonPropertyName("certificate")]
    public OperationalKeyCertificate Certificate { get; set; } = new();
}
