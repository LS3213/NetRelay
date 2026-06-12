using NetRelay.Models;

namespace NetRelay.Services;

public static class RuleSchedulerPolicy
{
    public static bool ShouldRestoreTimeRuleOccurrence(
        ExecutionRecord record,
        IReadOnlySet<Guid> scheduledTimeRuleIds)
    {
        if (record.RuleId is not Guid ruleId || !scheduledTimeRuleIds.Contains(ruleId))
        {
            return false;
        }

        return record.Source == RuleSource.Schedule
            || (record.Source == RuleSource.Notification
                && record.ReasonCode == "NOTIFICATION_CANCELLED");
    }
}
