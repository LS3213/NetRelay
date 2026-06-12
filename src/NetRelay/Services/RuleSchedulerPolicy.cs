using NetRelay.Models;

namespace NetRelay.Services;

public static class RuleSchedulerPolicy
{
    public static bool ShouldRestoreTimeRuleOccurrence(
        ExecutionRecord record,
        IReadOnlySet<Guid> scheduledTimeRuleIds)
    {
        return record is
        {
            Source: RuleSource.Schedule,
            RuleId: Guid ruleId
        }
        && scheduledTimeRuleIds.Contains(ruleId);
    }
}
