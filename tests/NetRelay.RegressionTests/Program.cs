using NetRelay.Models;
using NetRelay.Services;

var tests = new (string Name, Action Test)[]
{
    ("Valid probe policy is accepted", ValidProbePolicyIsAccepted),
    ("Zero probe attempts are rejected", ZeroProbeAttemptsAreRejected),
    ("Required endpoint threshold is validated", RequiredEndpointThresholdIsValidated),
    ("Invalid saved policy pauses automation", InvalidSavedPolicyPausesAutomation),
    ("Recovery bypasses invalid probe policy gate", RecoveryBypassesInvalidProbePolicyGate),
    ("Recovery bypasses rule cooldown", RecoveryBypassesRuleCooldown),
    ("Offline transition requires an online baseline", OfflineTransitionRequiresAnOnlineBaseline),
    ("Post-switch validation requires a verified backup", PostSwitchValidationRequiresAVerifiedBackup),
    ("Manual records do not suppress scheduled rules", ManualRecordsDoNotSuppressScheduledRules),
    ("Single instance service signals primary instance", SingleInstanceServiceSignalsPrimaryInstance)
};

var failures = new List<string>();
foreach (var (name, test) in tests)
{
    try
    {
        test();
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{name}: {exception.Message}");
        Console.WriteLine($"FAIL: {name}");
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

Console.WriteLine($"{tests.Length} regression tests passed.");
return 0;

static void ValidProbePolicyIsAccepted()
{
    Assert(ConnectivityProbePolicyValidator.Validate(new ConnectivityProbePolicy()) is null);
}

static void ZeroProbeAttemptsAreRejected()
{
    var policy = new ConnectivityProbePolicy { Attempts = 0 };
    Assert(ConnectivityProbePolicyValidator.Validate(policy) is not null);
}

static void RequiredEndpointThresholdIsValidated()
{
    var policy = new ConnectivityProbePolicy { RequiredSuccessfulEndpoints = 3 };
    Assert(ConnectivityProbePolicyValidator.Validate(policy) is not null);
}

static void InvalidSavedPolicyPausesAutomation()
{
    var directory = Path.Combine(Path.GetTempPath(), $"NetRelay-Regression-{Guid.NewGuid():N}");
    try
    {
        var service = new ConfigurationService(directory);
        service.Current.ProbePolicy.Attempts = 0;
        service.Save();
        Assert(!service.IsAutomationEnabled);

        var reloaded = new ConfigurationService(directory);
        Assert(!reloaded.IsAutomationEnabled);
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void RecoveryBypassesRuleCooldown()
{
    var now = DateTimeOffset.Now;
    Assert(RuleExecutionPolicy.IsCooldownActive(RuleSource.Schedule, 300, now.AddSeconds(-10), now));
    Assert(!RuleExecutionPolicy.IsCooldownActive(RuleSource.Recovery, 300, now.AddSeconds(-10), now));
}

static void RecoveryBypassesInvalidProbePolicyGate()
{
    Assert(RuleExecutionPolicy.RequiresValidProbePolicy(RuleSource.Schedule));
    Assert(RuleExecutionPolicy.RequiresValidProbePolicy(RuleSource.NetworkChange));
    Assert(!RuleExecutionPolicy.RequiresValidProbePolicy(RuleSource.Manual));
    Assert(!RuleExecutionPolicy.RequiresValidProbePolicy(RuleSource.Recovery));
}

static void OfflineTransitionRequiresAnOnlineBaseline()
{
    Assert(!RuleExecutionPolicy.ShouldTriggerOfflineTransition(false, false, false));
    Assert(!RuleExecutionPolicy.ShouldTriggerOfflineTransition(true, false, false));
    Assert(RuleExecutionPolicy.ShouldTriggerOfflineTransition(true, true, false));
}

static void PostSwitchValidationRequiresAVerifiedBackup()
{
    Assert(RuleExecutionPolicy.ShouldValidatePostSwitch(true, RuleAction.Disable, 1));
    Assert(!RuleExecutionPolicy.ShouldValidatePostSwitch(true, RuleAction.Disable, 0));
    Assert(!RuleExecutionPolicy.ShouldValidatePostSwitch(true, RuleAction.Enable, 1));
    Assert(!RuleExecutionPolicy.ShouldValidatePostSwitch(false, RuleAction.Disable, 1));
}

static void ManualRecordsDoNotSuppressScheduledRules()
{
    var ruleId = Guid.NewGuid();
    var record = CreateRecord(ruleId, RuleSource.Manual);
    Assert(!RuleSchedulerPolicy.ShouldRestoreTimeRuleOccurrence(record, new HashSet<Guid> { ruleId }));

    record = CreateRecord(ruleId, RuleSource.Schedule);
    Assert(RuleSchedulerPolicy.ShouldRestoreTimeRuleOccurrence(record, new HashSet<Guid> { ruleId }));
}

static void SingleInstanceServiceSignalsPrimaryInstance()
{
    var scopeName = $"NetRelay-Regression-{Guid.NewGuid():N}";
    using var primary = new SingleInstanceService(scopeName);
    Assert(primary.TryAcquire());

    using var activated = new ManualResetEventSlim();
    primary.StartListening(activated.Set);

    var secondaryAcquired = Task.Run(() =>
    {
        using var secondary = new SingleInstanceService(scopeName);
        var acquired = secondary.TryAcquire();
        secondary.SignalPrimaryInstance();
        return acquired;
    }).GetAwaiter().GetResult();

    Assert(!secondaryAcquired);
    Assert(activated.Wait(TimeSpan.FromSeconds(2)));
}

static ExecutionRecord CreateRecord(Guid ruleId, RuleSource source)
{
    var now = DateTimeOffset.Now;
    return new ExecutionRecord(
        Guid.NewGuid(),
        ruleId,
        source,
        Guid.NewGuid().ToString(),
        RuleAction.Disable,
        now,
        now,
        "SUCCESS",
        "OK",
        null);
}

static void Assert(bool condition)
{
    if (!condition)
    {
        throw new InvalidOperationException("Assertion failed.");
    }
}
