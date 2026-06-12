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
    ("Disabled native status is not reported as enabled", DisabledNativeStatusIsNotReportedAsEnabled),
    ("Native inventory retains transiently missing adapters", NativeInventoryRetainsTransientlyMissingAdapters),
    ("Native inventory remembers expected disabled state", NativeInventoryRemembersExpectedDisabledState),
    ("Adapter identity ignores GUID formatting", AdapterIdentityIgnoresGuidFormatting),
    ("Read-only adapter diagnostic report is created", ReadOnlyAdapterDiagnosticReportIsCreated),
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

static void DisabledNativeStatusIsNotReportedAsEnabled()
{
    var connection = new NativeConnectionInfo(
        Guid.NewGuid(),
        "Ethernet",
        "Adapter",
        NativeConnectionStatus.HardwareDisabled);
    Assert(!connection.IsEnabled);
    Assert(connection with { Status = NativeConnectionStatus.Disconnected } is { IsEnabled: true });
}

static void NativeInventoryRetainsTransientlyMissingAdapters()
{
    var connection = new NativeConnectionInfo(
        Guid.NewGuid(),
        "Ethernet",
        "Adapter",
        NativeConnectionStatus.HardwareDisabled);
    var inventory = new NativeConnectionInventory(missingRefreshLimit: 2);

    Assert(inventory.MergeObserved([connection]).Count == 1);
    Assert(inventory.MergeObserved([]).Count == 1);
    Assert(inventory.MergeObserved([]).Count == 1);
    Assert(inventory.MergeObserved([]).Count == 0);
}

static void NativeInventoryRemembersExpectedDisabledState()
{
    var id = Guid.NewGuid();
    var inventory = new NativeConnectionInventory();
    inventory.RememberExpectedState(id, "Ethernet", "Adapter", enabled: false);

    var connection = inventory.MergeObserved([]).Single();
    Assert(connection.Id == id);
    Assert(!connection.IsEnabled);
}

static void AdapterIdentityIgnoresGuidFormatting()
{
    var id = Guid.NewGuid();
    Assert(AdapterIdentity.AreEqual(id.ToString("D"), id.ToString("B")));
    Assert(!AdapterIdentity.AreEqual(id.ToString("D"), Guid.NewGuid().ToString("B")));
}

static void ReadOnlyAdapterDiagnosticReportIsCreated()
{
    var directory = Path.Combine(Path.GetTempPath(), $"NetRelay-Diagnostic-{Guid.NewGuid():N}");
    var reportPath = Path.Combine(directory, "adapters.json");
    try
    {
        Assert(AdapterDiagnosticService.WriteReport(reportPath) == reportPath);
        Assert(File.Exists(reportPath));
        var report = File.ReadAllText(reportPath);
        Assert(report.Contains("\"NativeConnections\"", StringComparison.Ordinal));
        Assert(report.Contains("\"LastObservedConnectionCount\"", StringComparison.Ordinal));
        Assert(report.Contains("\"LastEnumerationError\"", StringComparison.Ordinal));
        Assert(report.Contains("\"DotNetAdapters\"", StringComparison.Ordinal));
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
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
