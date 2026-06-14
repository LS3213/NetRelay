using System.Text.Json.Serialization;

namespace NetRelay.Contracts;

public sealed class FeedbackRequest
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty; // "bug" | "suggestion" | "other"

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("contact")]
    public string? Contact { get; set; }
}

public sealed class FeedbackStatusUpdateRequest
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty; // "pending" | "resolved" | "ignored"
}
