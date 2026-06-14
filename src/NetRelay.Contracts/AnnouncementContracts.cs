using System;
using System.Text.Json.Serialization;
using NetRelay.Contracts.Security;

namespace NetRelay.Contracts;

public sealed class AnnouncementDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = string.Empty; // "normal" | "important" | "critical"

    [JsonPropertyName("targetVersionMin")]
    public string? TargetVersionMin { get; set; }

    [JsonPropertyName("targetVersionMax")]
    public string? TargetVersionMax { get; set; }

    [JsonPropertyName("displayTrigger")]
    public string DisplayTrigger { get; set; } = string.Empty; // "once_per_device" | "every_startup"

    [JsonPropertyName("publishedAt")]
    public DateTimeOffset PublishedAt { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed class AnnouncementCheckResponse
{
    [JsonPropertyName("envelope")]
    public SignedEnvelope Envelope { get; set; } = new();

    [JsonPropertyName("certificate")]
    public OperationalKeyCertificate Certificate { get; set; } = new();
}

public sealed class AnnouncementCreateRequest
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = string.Empty;

    [JsonPropertyName("targetVersionMin")]
    public string? TargetVersionMin { get; set; }

    [JsonPropertyName("targetVersionMax")]
    public string? TargetVersionMax { get; set; }

    [JsonPropertyName("displayTrigger")]
    public string DisplayTrigger { get; set; } = string.Empty;

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; set; }
}
