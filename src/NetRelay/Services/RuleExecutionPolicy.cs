using NetRelay.Models;

namespace NetRelay.Services;

public static class RuleExecutionPolicy
{
    public static bool RequiresValidProbePolicy(RuleSource source)
    {
        return source is not (RuleSource.Manual or RuleSource.Recovery);
    }

    public static bool IsCooldownActive(
        RuleSource source,
        int cooldownSeconds,
        DateTimeOffset? lastExecuted,
        DateTimeOffset now)
    {
        return source != RuleSource.Recovery
            && cooldownSeconds > 0
            && lastExecuted.HasValue
            && (now - lastExecuted.Value).TotalSeconds < cooldownSeconds;
    }

    public static bool ShouldValidatePostSwitch(
        bool operationSucceeded,
        RuleAction action,
        int validatedBackupCount)
    {
        return operationSucceeded
            && action == RuleAction.Disable
            && validatedBackupCount > 0;
    }

    public static bool ShouldTriggerOfflineTransition(
        bool hadPreviousState,
        bool wasOnline,
        bool isOnline)
    {
        return hadPreviousState && wasOnline && !isOnline;
    }
}
