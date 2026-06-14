using System;
using System.Text.Json.Serialization;
using NetRelay.Contracts.Security;

namespace NetRelay.Contracts;

public sealed class PolicyEvaluateRequest
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("installationId")]
    public Guid InstallationId { get; set; }

    [JsonPropertyName("clientVersion")]
    public string ClientVersion { get; set; } = string.Empty;
}

public sealed class PolicyEvaluationResult
{
    [JsonPropertyName("isBlocked")]
    public bool IsBlocked { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("appealUrl")]
    public string? AppealUrl { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; set; }

    [JsonPropertyName("allowUpdate")]
    public bool AllowUpdate { get; set; }
}

public sealed class PolicyEvaluateResponse
{
    [JsonPropertyName("envelope")]
    public SignedEnvelope Envelope { get; set; } = new();

    [JsonPropertyName("certificate")]
    public OperationalKeyCertificate Certificate { get; set; } = new();
}

public sealed class DeviceBlockDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("deviceId")]
    public Guid? DeviceId { get; set; }

    [JsonPropertyName("installationId")]
    public Guid? InstallationId { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty; // "active" | "revoked"

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; set; }

    [JsonPropertyName("revokedAt")]
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class GlobalPolicyDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty; // "global" | "version_range"

    [JsonPropertyName("targetVersionMin")]
    public string? TargetVersionMin { get; set; }

    [JsonPropertyName("targetVersionMax")]
    public string? TargetVersionMax { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("allowUpdate")]
    public bool AllowUpdate { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty; // "active" | "revoked"

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; set; }

    [JsonPropertyName("revokedAt")]
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class DeviceBlockCreateRequest
{
    [JsonPropertyName("deviceId")]
    public Guid? DeviceId { get; set; }

    [JsonPropertyName("installationId")]
    public Guid? InstallationId { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed class GlobalPolicyCreateRequest
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty; // "global" | "version_range"

    [JsonPropertyName("targetVersionMin")]
    public string? TargetVersionMin { get; set; }

    [JsonPropertyName("targetVersionMax")]
    public string? TargetVersionMax { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("allowUpdate")]
    public bool AllowUpdate { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; set; }
}
