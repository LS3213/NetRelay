using System.Text.Json.Serialization;

namespace NetRelay.Models;

public sealed record ExecutionRecord(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("ruleId")] Guid? RuleId,
    [property: JsonPropertyName("source")] RuleSource Source,
    [property: JsonPropertyName("targetAdapterId")] string TargetAdapterId,
    [property: JsonPropertyName("requestedAction")] RuleAction RequestedAction,
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("finishedAt")] DateTimeOffset FinishedAt,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("reasonCode")] string ReasonCode,
    [property: JsonPropertyName("windowsErrorCode")] int? WindowsErrorCode
);
