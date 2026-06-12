using System.Text.Json.Serialization;

namespace NetRelay.Models;

public sealed record ConnectivityResult(
    [property: JsonPropertyName("adapterId")] string AdapterId,
    [property: JsonPropertyName("checkedAt")] DateTimeOffset CheckedAt,
    [property: JsonPropertyName("online")] bool Online,
    [property: JsonPropertyName("routePresent")] bool RoutePresent,
    [property: JsonPropertyName("attempts")] IReadOnlyList<ProbeAttempt> Attempts,
    [property: JsonPropertyName("reasonCode")] string ReasonCode,
    [property: JsonPropertyName("nlmConnectivity")] string? NlmConnectivity
);

public sealed record ProbeAttempt(
    [property: JsonPropertyName("endpointUrl")] string EndpointUrl,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("elapsedMs")] double ElapsedMs,
    [property: JsonPropertyName("errorMessage")] string? ErrorMessage
)
{
    [JsonIgnore]
    public TimeSpan Elapsed
    {
        get => TimeSpan.FromMilliseconds(ElapsedMs);
        init => ElapsedMs = value.TotalMilliseconds;
    }
}
