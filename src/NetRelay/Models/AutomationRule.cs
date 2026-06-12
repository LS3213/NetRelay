using System.Text.Json.Serialization;

namespace NetRelay.Models;

public enum RuleAction
{
    Enable,
    Disable
}

public enum RuleSource
{
    Manual,
    Schedule,
    NetworkChange,
    Recovery,
    Notification
}

public sealed record AutomationRule(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("targetAdapterId")] string TargetAdapterId,
    [property: JsonPropertyName("action")] RuleAction Action,
    [property: JsonPropertyName("trigger")] RuleTrigger Trigger,
    [property: JsonPropertyName("conditions")] IReadOnlyList<RuleCondition> Conditions,
    [property: JsonPropertyName("preNotifications")] IReadOnlyList<PreNotification> PreNotifications,
    [property: JsonPropertyName("recovery")] RecoveryPolicy? Recovery,
    [property: JsonPropertyName("requireUsableBackup")] bool RequireUsableBackup,
    [property: JsonPropertyName("cooldownSeconds")] int CooldownSeconds
);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(RuleTrigger.Once), typeDiscriminator: "Once")]
[JsonDerivedType(typeof(RuleTrigger.Daily), typeDiscriminator: "Daily")]
[JsonDerivedType(typeof(RuleTrigger.Weekly), typeDiscriminator: "Weekly")]
[JsonDerivedType(typeof(RuleTrigger.NetworkChange), typeDiscriminator: "NetworkChange")]
public abstract record RuleTrigger
{
    public sealed record Once(
        [property: JsonPropertyName("at")] DateTimeOffset At
    ) : RuleTrigger;

    public sealed record Daily(
        [property: JsonPropertyName("localTime")] TimeOnly LocalTime
    ) : RuleTrigger;

    public sealed record Weekly(
        [property: JsonPropertyName("localTime")] TimeOnly LocalTime,
        [property: JsonPropertyName("weekdays")] IReadOnlySet<DayOfWeek> Weekdays
    ) : RuleTrigger;

    public sealed record NetworkChange(
        [property: JsonPropertyName("condition")] RuleCondition Condition,
        [property: JsonPropertyName("debounceSeconds")] int DebounceSeconds
    ) : RuleTrigger;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(AdapterOfflineCondition), typeDiscriminator: "AdapterOffline")]
public abstract record RuleCondition;

public sealed record AdapterOfflineCondition(
    [property: JsonPropertyName("adapterId")] string AdapterId
) : RuleCondition;

public sealed record PreNotification(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("minutesBefore")] int MinutesBefore,
    [property: JsonPropertyName("allowDelay")] bool AllowDelay,
    [property: JsonPropertyName("delayMinutes")] int DelayMinutes,
    [property: JsonPropertyName("allowCancelOccurrence")] bool AllowCancelOccurrence
);

public sealed record RecoveryPolicy(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("delayMinutes")] int DelayMinutes
);
