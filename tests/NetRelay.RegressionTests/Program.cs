using System.IO;
using System.Linq;
using System.Threading;
using NetRelay.Models;
using NetRelay.Services;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Collections.Concurrent;

if (args.FirstOrDefault() == "--acceptance-toggle-vmnet1")
{
    return RunVmnet1AcceptanceTest(args.Skip(1).FirstOrDefault());
}

var tests = new (string Name, Action Test)[]
{
    ("Valid probe policy is accepted", ValidProbePolicyIsAccepted),
    ("Probe policy supports ping and dns", ProbePolicySupportsPingAndDns),
    ("DNS query building and parsing works", DnsQueryBuildingAndParsingWorks),
    ("DNS response validation rejects invalid responses", DnsResponseValidationRejectsInvalidResponses),
    ("Zero probe attempts are rejected", ZeroProbeAttemptsAreRejected),
    ("Required endpoint threshold is validated", RequiredEndpointThresholdIsValidated),
    ("Invalid saved policy pauses automation", InvalidSavedPolicyPausesAutomation),
    ("Settings apply custom log retention immediately", SettingsApplyCustomLogRetentionImmediately),
    ("Invalid log retention falls back safely", InvalidLogRetentionFallsBackSafely),
    ("Rule defaults apply only to new rules", RuleDefaultsApplyOnlyToNewRules),
    ("Invalid settings do not enable automation", InvalidSettingsDoNotEnableAutomation),
    ("Recovery bypasses invalid probe policy gate", RecoveryBypassesInvalidProbePolicyGate),
    ("Recovery bypasses rule cooldown", RecoveryBypassesRuleCooldown),
    ("Recovery checks native enabled state", RecoveryChecksNativeEnabledState),
    ("Offline transition requires an online baseline", OfflineTransitionRequiresAnOnlineBaseline),
    ("Post-switch validation requires a verified backup", PostSwitchValidationRequiresAVerifiedBackup),
    ("Manual records do not suppress scheduled rules", ManualRecordsDoNotSuppressScheduledRules),
    ("Notification cancellation suppresses only its scheduled occurrence", NotificationCancellationSuppressesOnlyItsScheduledOccurrence),
    ("Disabled native status is not reported as enabled", DisabledNativeStatusIsNotReportedAsEnabled),
    ("Native inventory retains transiently missing adapters", NativeInventoryRetainsTransientlyMissingAdapters),
    ("Native inventory remembers expected disabled state", NativeInventoryRemembersExpectedDisabledState),
    ("Expected disabled state overrides transient disconnected status", ExpectedDisabledStateOverridesTransientDisconnectedStatus),
    ("Adapter identity ignores GUID formatting", AdapterIdentityIgnoresGuidFormatting),
    ("Read-only adapter diagnostic report is created", ReadOnlyAdapterDiagnosticReportIsCreated),
    ("Auto-start task must target current executable", AutoStartTaskMustTargetCurrentExecutable),
    ("Scheduler execution gate rejects re-entry", SchedulerExecutionGateRejectsReentry),
    ("Editing a rule resets scheduler runtime state", EditingRuleResetsSchedulerRuntimeState),
    ("Editing a rule resets engine cooldown state", EditingRuleResetsEngineCooldownState),
    ("Missing adapter does not start cooldown", MissingAdapterDoesNotStartCooldown),
    ("Rule engine writes test logs to isolated directory", RuleEngineWritesTestLogsToIsolatedDirectory),
    ("Adapter UI statuses distinguish link and internet", AdapterUiStatusesDistinguishLinkAndInternet),
    ("Single instance service signals primary instance", SingleInstanceServiceSignalsPrimaryInstance),
    ("Notification protocol activation is strict", NotificationProtocolActivationIsStrict),
    ("Notification actions require a valid one-shot ticket", NotificationActionsRequireValidOneShotTicket),
    ("Rich toast respects notification action permissions", RichToastRespectsNotificationActionPermissions),
    ("Scheduler triggers automatic recovery on time elapsed", SchedulerTriggersAutomaticRecoveryOnTimeElapsed),
    ("Scheduler skips expired automatic recovery", SchedulerSkipsExpiredAutomaticRecovery),
    ("EvidenceHasher anonymizes evidence properly", EvidenceHasherAnonymizesEvidenceProperly),
    ("ConnectivityService challenge probe fallback behaves gracefully on BACKEND_UNAVAILABLE", ConnectivityServiceGracefulDegradationOnBackendUnavailable),
    ("Log packaging logic zips jsonl files properly", TestLogPackagingLogic),
    ("Safe markdown parser parses formatting and filters unsafe protocols", TestSafeMarkdownParser)
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

static void ProbePolicySupportsPingAndDns()
{
    var policy = new ConnectivityProbePolicy
    {
        Endpoints = new List<ProbeEndpoint>
        {
            new() { Url = "ping://1.1.1.1" },
            new() { Url = "dns://www.google.com" }
        }
    };
    Assert(ConnectivityProbePolicyValidator.Validate(policy) is null);

    var invalidPolicy = new ConnectivityProbePolicy
    {
        Endpoints = new List<ProbeEndpoint>
        {
            new() { Url = "ftp://1.1.1.1" }
        }
    };
    Assert(ConnectivityProbePolicyValidator.Validate(invalidPolicy) is not null);
}

static void DnsQueryBuildingAndParsingWorks()
{
    var buildMethod = typeof(ConnectivityService).GetMethod("BuildDnsQuery", BindingFlags.NonPublic | BindingFlags.Static);
    var parseMethod = typeof(ConnectivityService).GetMethod("TryParseDnsResponse", BindingFlags.NonPublic | BindingFlags.Static);

    Assert(buildMethod is not null);
    Assert(parseMethod is not null);

    var queryBytes = (byte[])buildMethod!.Invoke(null, new object[] { "google.com", false })!;
    Assert(queryBytes.Length > 0);

    var response = BuildDnsResponse(queryBytes, responseCode: 0, answerType: 1, answerBytes: [8, 8, 8, 8]);
    var result = InvokeDnsResponseParser(parseMethod!, response, queryBytes, AddressFamily.InterNetwork);
    Assert(result.Success);
    Assert(result.ResolvedIp?.ToString() == "8.8.8.8");

    var queryBytesV6 = (byte[])buildMethod!.Invoke(null, new object[] { "google.com", true })!;
    Assert(queryBytesV6.Length > 0);

    var responseV6 = BuildDnsResponse(queryBytesV6, responseCode: 0, answerType: 28, answerBytes: Enumerable.Repeat((byte)0xFF, 16).ToArray());
    var resultV6 = InvokeDnsResponseParser(parseMethod!, responseV6, queryBytesV6, AddressFamily.InterNetworkV6);
    Assert(resultV6.Success);
    Assert(resultV6.ResolvedIp?.ToString() == "ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff");
}

static void DnsResponseValidationRejectsInvalidResponses()
{
    var buildMethod = typeof(ConnectivityService).GetMethod("BuildDnsQuery", BindingFlags.NonPublic | BindingFlags.Static);
    var parseMethod = typeof(ConnectivityService).GetMethod("TryParseDnsResponse", BindingFlags.NonPublic | BindingFlags.Static);
    Assert(buildMethod is not null);
    Assert(parseMethod is not null);

    var query = (byte[])buildMethod!.Invoke(null, new object[] { "google.com", false })!;

    var nxdomain = BuildDnsResponse(query, responseCode: 3);
    Assert(!InvokeDnsResponseParser(parseMethod!, nxdomain, query, AddressFamily.InterNetwork).Success);

    var emptyAnswer = BuildDnsResponse(query, responseCode: 0);
    Assert(!InvokeDnsResponseParser(parseMethod!, emptyAnswer, query, AddressFamily.InterNetwork).Success);

    var wrongTransaction = BuildDnsResponse(query, responseCode: 0, answerType: 1, answerBytes: [8, 8, 4, 4]);
    wrongTransaction[0] ^= 0xFF;
    Assert(!InvokeDnsResponseParser(parseMethod!, wrongTransaction, query, AddressFamily.InterNetwork).Success);

    var truncated = BuildDnsResponse(query, responseCode: 0, answerType: 1, answerBytes: [1, 1, 1, 1]);
    truncated[2] |= 0x02;
    Assert(!InvokeDnsResponseParser(parseMethod!, truncated, query, AddressFamily.InterNetwork).Success);

    var wrongRecordType = BuildDnsResponse(query, responseCode: 0, answerType: 28, answerBytes: Enumerable.Repeat((byte)0x01, 16).ToArray());
    Assert(!InvokeDnsResponseParser(parseMethod!, wrongRecordType, query, AddressFamily.InterNetwork).Success);

    var malformed = BuildDnsResponse(query, responseCode: 0, answerType: 1, answerBytes: [9, 9, 9, 9]);
    Assert(!InvokeDnsResponseParserWithLength(parseMethod!, malformed, malformed.Length - 2, query, AddressFamily.InterNetwork).Success);
}

static byte[] BuildDnsResponse(byte[] query, int responseCode, ushort? answerType = null, byte[]? answerBytes = null)
{
    var questionLength = query.Length - 12;
    var answerLength = answerType.HasValue && answerBytes is not null ? 12 + answerBytes.Length : 0;
    var response = new byte[12 + questionLength + answerLength];

    response[0] = query[0];
    response[1] = query[1];
    response[2] = 0x81;
    response[3] = (byte)(0x80 | responseCode);
    response[4] = 0x00;
    response[5] = 0x01;
    response[6] = 0x00;
    response[7] = answerLength > 0 ? (byte)0x01 : (byte)0x00;
    Array.Copy(query, 12, response, 12, questionLength);

    if (answerLength > 0)
    {
        var index = 12 + questionLength;
        response[index++] = 0xC0;
        response[index++] = 0x0C;
        response[index++] = (byte)(answerType!.Value >> 8);
        response[index++] = (byte)answerType.Value;
        response[index++] = 0x00;
        response[index++] = 0x01;
        response[index++] = 0x00;
        response[index++] = 0x00;
        response[index++] = 0x00;
        response[index++] = 0x3C;
        response[index++] = (byte)(answerBytes!.Length >> 8);
        response[index++] = (byte)answerBytes.Length;
        Array.Copy(answerBytes, 0, response, index, answerBytes.Length);
    }

    return response;
}

static (bool Success, IPAddress? ResolvedIp, string ErrorMessage) InvokeDnsResponseParser(
    MethodInfo parseMethod,
    byte[] response,
    byte[] query,
    AddressFamily family)
{
    return InvokeDnsResponseParserWithLength(parseMethod, response, response.Length, query, family);
}

static (bool Success, IPAddress? ResolvedIp, string ErrorMessage) InvokeDnsResponseParserWithLength(
    MethodInfo parseMethod,
    byte[] response,
    int responseLength,
    byte[] query,
    AddressFamily family)
{
    object?[] parameters = [response, responseLength, query, family, null, null];
    var success = (bool)parseMethod.Invoke(null, parameters)!;
    return (success, (IPAddress?)parameters[4], (string?)parameters[5] ?? string.Empty);
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

static void SettingsApplyCustomLogRetentionImmediately()
{
    var directory = Path.Combine(Path.GetTempPath(), $"NetRelay-Settings-{Guid.NewGuid():N}");
    var logDirectory = Path.Combine(directory, "logs");
    try
    {
        Directory.CreateDirectory(logDirectory);
        var oldLog = Path.Combine(logDirectory, $"execution-{DateTime.Today.AddDays(-8):yyyy-MM-dd}.jsonl");
        var retainedLog = Path.Combine(logDirectory, $"execution-{DateTime.Today.AddDays(-2):yyyy-MM-dd}.jsonl");
        File.WriteAllText(oldLog, string.Empty);
        File.WriteAllText(retainedLog, string.Empty);

        var configService = new ConfigurationService(Path.Combine(directory, "config"));
        configService.Current.KeepDays = 3;
        var reloadCount = 0;
        var runtime = new SettingsRuntimeService(
            configService,
            new LogService(logDirectory),
            () => reloadCount++);

        var result = runtime.ApplyAsync().GetAwaiter().GetResult();

        Assert(result.Success);
        Assert(reloadCount == 1);
        Assert(!File.Exists(oldLog));
        Assert(File.Exists(retainedLog));

        var reloaded = new ConfigurationService(Path.Combine(directory, "config"));
        Assert(reloaded.Current.KeepDays == 3);
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void InvalidLogRetentionFallsBackSafely()
{
    var directory = Path.Combine(Path.GetTempPath(), $"NetRelay-Log-Retention-{Guid.NewGuid():N}");
    try
    {
        Directory.CreateDirectory(directory);
        var retainedLog = Path.Combine(directory, $"execution-{DateTime.Today.AddDays(-2):yyyy-MM-dd}.jsonl");
        var expiredLog = Path.Combine(directory, $"execution-{DateTime.Today.AddDays(-40):yyyy-MM-dd}.jsonl");
        File.WriteAllText(retainedLog, string.Empty);
        File.WriteAllText(expiredLog, string.Empty);

        new LogService(directory).RotateLogsAsync(0).GetAwaiter().GetResult();

        Assert(File.Exists(retainedLog));
        Assert(!File.Exists(expiredLog));
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void RuleDefaultsApplyOnlyToNewRules()
{
    var directory = Path.Combine(Path.GetTempPath(), $"NetRelay-Rule-Defaults-{Guid.NewGuid():N}");
    try
    {
        var configService = new ConfigurationService(Path.Combine(directory, "config"));
        var existingRule = new AutomationRule(
            Guid.NewGuid(),
            "Existing rule",
            Enabled: true,
            Guid.NewGuid().ToString("B"),
            RuleAction.Disable,
            new RuleTrigger.NetworkChange(new AdapterOfflineCondition(Guid.NewGuid().ToString("B")), 4),
            Conditions: [],
            PreNotifications: [],
            Recovery: null,
            RequireUsableBackup: false,
            CooldownSeconds: 30);
        configService.Current.Rules.Add(existingRule);
        configService.Current.DebounceSeconds = 17;
        configService.Current.CooldownMinutes = 9;

        var runtime = new SettingsRuntimeService(
            configService,
            new LogService(Path.Combine(directory, "logs")),
            () => { });
        Assert(runtime.ApplyAsync().GetAwaiter().GetResult().Success);

        Assert(RuleDefaultPolicy.GetDebounceSeconds(configService.Current) == 17);
        Assert(RuleDefaultPolicy.GetCooldownSeconds(configService.Current) == 540);
        Assert(configService.Current.Rules.Single().CooldownSeconds == 30);
        Assert(configService.Current.Rules.Single().Trigger is RuleTrigger.NetworkChange { DebounceSeconds: 4 });

        var reloaded = new ConfigurationService(Path.Combine(directory, "config"));
        Assert(reloaded.Current.DebounceSeconds == 17);
        Assert(reloaded.Current.CooldownMinutes == 9);
        Assert(reloaded.Current.Rules.Single().CooldownSeconds == 30);
        Assert(reloaded.Current.Rules.Single().Trigger is RuleTrigger.NetworkChange { DebounceSeconds: 4 });
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void InvalidSettingsDoNotEnableAutomation()
{
    var directory = Path.Combine(Path.GetTempPath(), $"NetRelay-Invalid-Settings-{Guid.NewGuid():N}");
    try
    {
        var configService = new ConfigurationService(Path.Combine(directory, "config"));
        configService.Current.KeepDays = 0;
        var reloadCount = 0;
        var runtime = new SettingsRuntimeService(
            configService,
            new LogService(Path.Combine(directory, "logs")),
            () => reloadCount++);

        var result = runtime.ApplyAsync().GetAwaiter().GetResult();

        Assert(!result.Success);
        Assert(!configService.IsAutomationEnabled);
        Assert(configService.AutomationDisabledReason is not null);
        Assert(reloadCount == 1);

        var reloaded = new ConfigurationService(Path.Combine(directory, "config"));
        Assert(reloaded.Current.KeepDays == 30);
        Assert(reloaded.IsAutomationEnabled);
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

static void RecoveryChecksNativeEnabledState()
{
    var adapterId = Guid.NewGuid();
    var enabled = new NativeConnectionInfo(
        adapterId,
        "Enabled adapter",
        "Enabled adapter",
        NativeConnectionStatus.Disconnected);
    var disabled = enabled with { Status = NativeConnectionStatus.HardwareDisabled };

    Assert(RuleExecutionPolicy.IsAdapterAlreadyEnabledForRecovery([enabled], adapterId.ToString("B")));
    Assert(!RuleExecutionPolicy.IsAdapterAlreadyEnabledForRecovery([disabled], adapterId.ToString("D")));
    Assert(!RuleExecutionPolicy.IsAdapterAlreadyEnabledForRecovery([], adapterId.ToString("D")));
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

static void NotificationCancellationSuppressesOnlyItsScheduledOccurrence()
{
    var ruleId = Guid.NewGuid();
    var scheduledRuleIds = new HashSet<Guid> { ruleId };
    var now = DateTimeOffset.Now;
    var cancelled = new ExecutionRecord(
        Guid.NewGuid(),
        ruleId,
        RuleSource.Notification,
        Guid.NewGuid().ToString(),
        RuleAction.Disable,
        now,
        now,
        "SKIPPED",
        "NOTIFICATION_CANCELLED",
        null);
    var delayed = cancelled with
    {
        Id = Guid.NewGuid(),
        ReasonCode = "NOTIFICATION_DELAYED"
    };

    Assert(RuleSchedulerPolicy.ShouldRestoreTimeRuleOccurrence(cancelled, scheduledRuleIds));
    Assert(!RuleSchedulerPolicy.ShouldRestoreTimeRuleOccurrence(delayed, scheduledRuleIds));
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

static void ExpectedDisabledStateOverridesTransientDisconnectedStatus()
{
    var id = Guid.NewGuid();
    var inventory = new NativeConnectionInventory(missingRefreshLimit: 2);
    inventory.RememberExpectedState(id, "Ethernet", "Adapter", enabled: false);

    var disconnected = new NativeConnectionInfo(
        id,
        "Ethernet",
        "Adapter",
        NativeConnectionStatus.Disconnected);
    Assert(!inventory.MergeObserved([disconnected]).Single().IsEnabled);
    Assert(!inventory.MergeObserved([disconnected]).Single().IsEnabled);
    Assert(inventory.MergeObserved([disconnected]).Single().IsEnabled);
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

static void AutoStartTaskMustTargetCurrentExecutable()
{
    var executablePath = Path.Combine(Path.GetTempPath(), "文档", "NetRelay.exe");
    var xml = $"""
        <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Settings><Enabled>true</Enabled></Settings>
          <Actions><Exec><Command>{executablePath}</Command><Arguments>--startup</Arguments></Exec></Actions>
        </Task>
        """;

    Assert(AutoStartTaskPolicy.MatchesCurrentExecutable(xml, executablePath));
    Assert(!AutoStartTaskPolicy.MatchesCurrentExecutable(
        xml,
        Path.Combine(Path.GetTempPath(), "Different", "NetRelay.exe")));
    Assert(!AutoStartTaskPolicy.MatchesCurrentExecutable(
        xml.Replace("--startup", "--unexpected", StringComparison.Ordinal),
        executablePath));
}

static void SchedulerExecutionGateRejectsReentry()
{
    var gate = new NonReentrantGate();
    Assert(gate.TryEnter());
    Assert(!gate.TryEnter());
    gate.Exit();
    Assert(gate.TryEnter());
    gate.Exit();
}

static void EditingRuleResetsSchedulerRuntimeState()
{
    var directory = Path.Combine(Path.GetTempPath(), $"NetRelay-Scheduler-Reload-{Guid.NewGuid():N}");
    try
    {
        var configService = new ConfigurationService(directory);
        var ruleId = Guid.NewGuid();
        configService.Current.Rules.Add(new AutomationRule(
            ruleId,
            "Edited rule",
            Enabled: true,
            Guid.NewGuid().ToString("B"),
            RuleAction.Disable,
            new RuleTrigger.Daily(new TimeOnly(23, 59)),
            Conditions: [],
            PreNotifications: [],
            Recovery: null,
            RequireUsableBackup: false,
            CooldownSeconds: 0));

        using var scheduler = new RuleSchedulerService(
            new RuleEngine(
                new NativeNetworkConnectionService(),
                new ConnectivityService(),
                configService,
                Path.Combine(directory, "logs")),
            configService,
            new ConnectivityService());
        var lastRan = GetPrivateField<Dictionary<Guid, DateTime>>(scheduler, "_timeTriggerLastRan");
        var onceRan = GetPrivateField<HashSet<Guid>>(scheduler, "_onceTriggerRan");
        var preNotifications = GetPrivateField<Dictionary<(Guid RuleId, int MinutesBefore), DateTime>>(
            scheduler,
            "_preNotificationLastTriggeredDate");
        var oncePreNotifications = GetPrivateField<HashSet<(Guid RuleId, int MinutesBefore)>>(
            scheduler,
            "_oncePreNotificationsTriggered");

        lastRan[ruleId] = DateTime.Today;
        onceRan.Add(ruleId);
        preNotifications[(ruleId, 5)] = DateTime.Today;
        oncePreNotifications.Add((ruleId, 5));
        var adapterStatuses = GetPrivateField<Dictionary<string, System.Net.NetworkInformation.OperationalStatus>>(
            scheduler,
            "_lastAdapterStatuses");
        var adapterInternetStates = GetPrivateField<Dictionary<string, bool>>(
            scheduler,
            "_lastAdapterInternetStates");
        adapterStatuses["adapter"] = System.Net.NetworkInformation.OperationalStatus.Up;
        adapterInternetStates["adapter"] = true;
        scheduler.DelayRule(ruleId, TimeSpan.FromMinutes(10));

        scheduler.Reload(ruleId);

        Assert(!lastRan.ContainsKey(ruleId));
        Assert(!onceRan.Contains(ruleId));
        Assert(!preNotifications.Keys.Any(key => key.RuleId == ruleId));
        Assert(!oncePreNotifications.Any(key => key.RuleId == ruleId));
        var delays = GetPrivateField<Dictionary<Guid, TimeSpan>>(scheduler, "_tempRuleDelays");
        Assert(!delays.ContainsKey(ruleId));
        Assert(!adapterStatuses.ContainsKey("adapter"));
        Assert(!adapterInternetStates.ContainsKey("adapter"));
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void EditingRuleResetsEngineCooldownState()
{
    var directory = Path.Combine(Path.GetTempPath(), $"NetRelay-Engine-Reload-{Guid.NewGuid():N}");
    try
    {
        var configService = new ConfigurationService(directory);
        var engine = new RuleEngine(
            new NativeNetworkConnectionService(),
            new ConnectivityService(),
            configService,
            Path.Combine(directory, "logs"));
        var rule = new AutomationRule(
            Guid.NewGuid(),
            "Edited cooldown rule",
            Enabled: true,
            Guid.NewGuid().ToString("B"),
            RuleAction.Disable,
            new RuleTrigger.Once(DateTimeOffset.Now),
            Conditions: [],
            PreNotifications: [],
            Recovery: null,
            RequireUsableBackup: false,
            CooldownSeconds: 300);

        var cooldowns = GetPrivateField<ConcurrentDictionary<Guid, DateTimeOffset>>(engine, "_lastExecutionTimes");
        cooldowns[rule.Id] = DateTimeOffset.Now;
        var beforeEdit = engine.ExecuteRuleAsync(rule, RuleSource.Schedule).GetAwaiter().GetResult();
        engine.ResetRuleRuntimeState(rule.Id);
        var afterEdit = engine.ExecuteRuleAsync(rule, RuleSource.Schedule).GetAwaiter().GetResult();

        Assert(beforeEdit.ReasonCode == "RULE_COOLDOWN_ACTIVE");
        Assert(afterEdit.ReasonCode == "ADAPTER_NOT_FOUND");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void MissingAdapterDoesNotStartCooldown()
{
    var directory = Path.Combine(Path.GetTempPath(), $"NetRelay-Missing-Adapter-Cooldown-{Guid.NewGuid():N}");
    try
    {
        var configService = new ConfigurationService(directory);
        var engine = new RuleEngine(
            new NativeNetworkConnectionService(),
            new ConnectivityService(),
            configService,
            Path.Combine(directory, "logs"));
        var rule = new AutomationRule(
            Guid.NewGuid(),
            "Missing adapter cooldown rule",
            Enabled: true,
            Guid.NewGuid().ToString("B"),
            RuleAction.Disable,
            new RuleTrigger.Once(DateTimeOffset.Now),
            Conditions: [],
            PreNotifications: [],
            Recovery: null,
            RequireUsableBackup: false,
            CooldownSeconds: 300);

        var first = engine.ExecuteRuleAsync(rule, RuleSource.Schedule).GetAwaiter().GetResult();
        var second = engine.ExecuteRuleAsync(rule, RuleSource.Schedule).GetAwaiter().GetResult();

        Assert(first.ReasonCode == "ADAPTER_NOT_FOUND");
        Assert(second.ReasonCode == "ADAPTER_NOT_FOUND");
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
    primary.StartListening(args => activated.Set());

    var secondaryAcquired = Task.Run(() =>
    {
        using var secondary = new SingleInstanceService(scopeName);
        var acquired = secondary.TryAcquire();
        secondary.SignalPrimaryInstance(new[] { "test-arg" });
        return acquired;
    }).GetAwaiter().GetResult();

    Assert(!secondaryAcquired);
    Assert(activated.Wait(TimeSpan.FromSeconds(2)));
}

static void NotificationProtocolActivationIsStrict()
{
    var notificationId = Guid.NewGuid();
    const string token = "A1B2C3D4";
    var uri = NotificationProtocolActivation.BuildUri(notificationId, token, PreNotificationAction.Delay);

    Assert(NotificationProtocolActivation.TryParse(uri, out var activation));
    Assert(activation is not null);
    Assert(activation!.NotificationId == notificationId);
    Assert(activation.Token == token);
    Assert(activation.Action == PreNotificationAction.Delay);

    Assert(!NotificationProtocolActivation.TryParse(
        $"netrelay://notification?action=unknown&notificationId={notificationId:D}&token={token}",
        out _));
    Assert(!NotificationProtocolActivation.TryParse(
        $"netrelay:action=delay&ruleId={notificationId:D}",
        out _));
    Assert(!NotificationProtocolActivation.TryParse(
        $"https://notification?action=delay&notificationId={notificationId:D}&token={token}",
        out _));
    Assert(!NotificationProtocolActivation.TryParse(
        $"netrelay://notification?action=delay&action=cancel&notificationId={notificationId:D}&token={token}",
        out _));
}

static void NotificationActionsRequireValidOneShotTicket()
{
    var directory = Path.Combine(Path.GetTempPath(), $"NetRelay-Notification-Ticket-{Guid.NewGuid():N}");
    try
    {
        var configService = new ConfigurationService(directory);
        var notification = new PreNotification(
            Guid.NewGuid(),
            MinutesBefore: 5,
            AllowDelay: true,
            DelayMinutes: 7,
            AllowCancelOccurrence: false);
        var rule = new AutomationRule(
            Guid.NewGuid(),
            "Notification action rule",
            Enabled: true,
            Guid.NewGuid().ToString("B"),
            RuleAction.Disable,
            new RuleTrigger.Daily(new TimeOnly(23, 59)),
            Conditions: [],
            PreNotifications: [notification],
            Recovery: null,
            RequireUsableBackup: false,
            CooldownSeconds: 0);
        configService.Current.Rules.Add(rule);

        using var scheduler = new RuleSchedulerService(
            new RuleEngine(
                new NativeNetworkConnectionService(),
                new ConnectivityService(),
                configService,
                Path.Combine(directory, "logs")),
            configService,
            new ConnectivityService());
        var triggerMethod = typeof(RuleSchedulerService).GetMethod(
            "TriggerPreNotification",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(triggerMethod is not null);

        var actionRecords = new List<ExecutionRecord>();
        scheduler.RuleExecuted += (_, record) => actionRecords.Add(record);
        PreNotificationEventArgs? eventArgs = null;
        using var triggered = new ManualResetEventSlim();
        scheduler.PreNotificationTriggered += (_, args) =>
        {
            eventArgs = args;
            triggered.Set();
        };

        var targetTime = DateTimeOffset.Now.AddMinutes(5);
        triggerMethod!.Invoke(scheduler, new object[] { rule, notification, targetTime, 5 });
        Assert(triggered.Wait(TimeSpan.FromSeconds(2)));
        Assert(eventArgs is not null);

        var wrongToken = scheduler.TryApplyPreNotificationAction(
            eventArgs!.NotificationActionId,
            "WRONG",
            PreNotificationAction.Delay);
        Assert(!wrongToken.Applied && wrongToken.ReasonCode == "NOTIFICATION_TOKEN_INVALID");

        var forbiddenCancel = scheduler.TryApplyPreNotificationAction(
            eventArgs.NotificationActionId,
            eventArgs.NotificationActionToken,
            PreNotificationAction.Cancel);
        Assert(!forbiddenCancel.Applied && forbiddenCancel.ReasonCode == "NOTIFICATION_ACTION_NOT_ALLOWED");

        var delayed = scheduler.TryApplyPreNotificationAction(
            eventArgs.NotificationActionId,
            eventArgs.NotificationActionToken,
            PreNotificationAction.Delay);
        Assert(delayed.Applied && delayed.ReasonCode == "NOTIFICATION_DELAYED");

        var delays = GetPrivateField<Dictionary<Guid, TimeSpan>>(scheduler, "_tempRuleDelays");
        Assert(delays.TryGetValue(rule.Id, out var delay) && delay == TimeSpan.FromMinutes(7));
        Assert(actionRecords.Any(record =>
            record.RuleId == rule.Id
            && record.Source == RuleSource.Notification
            && record.ReasonCode == "NOTIFICATION_DELAYED"));

        var duplicate = scheduler.TryApplyPreNotificationAction(
            eventArgs.NotificationActionId,
            eventArgs.NotificationActionToken,
            PreNotificationAction.Delay);
        Assert(!duplicate.Applied && duplicate.ReasonCode == "NOTIFICATION_NOT_FOUND");

        triggered.Reset();
        eventArgs = null;
        triggerMethod.Invoke(scheduler, new object[] { rule, notification, targetTime, 5 });
        Assert(triggered.Wait(TimeSpan.FromSeconds(2)));
        var expired = scheduler.TryApplyPreNotificationAction(
            eventArgs!.NotificationActionId,
            eventArgs.NotificationActionToken,
            PreNotificationAction.Delay,
            targetTime);
        Assert(!expired.Applied && expired.ReasonCode == "NOTIFICATION_EXPIRED");

        var cancelNotification = notification with
        {
            Id = Guid.NewGuid(),
            AllowDelay = false,
            AllowCancelOccurrence = true
        };
        configService.Current.Rules[0] = rule with { PreNotifications = [cancelNotification] };
        triggered.Reset();
        eventArgs = null;
        triggerMethod.Invoke(scheduler, new object[] { configService.Current.Rules[0], cancelNotification, targetTime, 5 });
        Assert(triggered.Wait(TimeSpan.FromSeconds(2)));
        var cancelled = scheduler.TryApplyPreNotificationAction(
            eventArgs!.NotificationActionId,
            eventArgs.NotificationActionToken,
            PreNotificationAction.Cancel);
        Assert(cancelled.Applied && cancelled.ReasonCode == "NOTIFICATION_CANCELLED");
        Assert(actionRecords.Any(record =>
            record.RuleId == rule.Id
            && record.Source == RuleSource.Notification
            && record.ReasonCode == "NOTIFICATION_CANCELLED"));
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void RichToastRespectsNotificationActionPermissions()
{
    var buildMethod = typeof(RichToastService).GetMethod(
        "BuildPreNotificationXml",
        BindingFlags.Static | BindingFlags.NonPublic);
    Assert(buildMethod is not null);

    var rule = new AutomationRule(
        Guid.NewGuid(),
        "Toast permission rule",
        Enabled: true,
        Guid.NewGuid().ToString("B"),
        RuleAction.Disable,
        new RuleTrigger.Daily(new TimeOnly(23, 59)),
        Conditions: [],
        PreNotifications: [],
        Recovery: null,
        RequireUsableBackup: false,
        CooldownSeconds: 0);

    var cancelOnly = new PreNotification(Guid.NewGuid(), 5, false, 7, true);
    var cancelOnlyArgs = new PreNotificationEventArgs(
        Guid.NewGuid(),
        "TOKEN",
        rule,
        cancelOnly,
        DateTimeOffset.Now.AddMinutes(5),
        5);
    var cancelOnlyXml = (string)buildMethod!.Invoke(null, new object[] { cancelOnlyArgs, "禁用" })!;
    Assert(!cancelOnlyXml.Contains("延迟", StringComparison.Ordinal));
    Assert(cancelOnlyXml.Contains("取消本次", StringComparison.Ordinal));

    var both = new PreNotification(Guid.NewGuid(), 5, true, 7, true);
    var bothArgs = new PreNotificationEventArgs(
        Guid.NewGuid(),
        "TOKEN",
        rule,
        both,
        DateTimeOffset.Now.AddMinutes(5),
        5);
    var bothXml = (string)buildMethod.Invoke(null, new object[] { bothArgs, "禁用" })!;
    Assert(bothXml.Contains("延迟 7 分钟", StringComparison.Ordinal));
    Assert(bothXml.Contains("notificationId=", StringComparison.Ordinal));
    Assert(bothXml.Contains("token=TOKEN", StringComparison.Ordinal));
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

static void AdapterUiStatusesDistinguishLinkAndInternet()
{
    var adapter = new NetworkAdapterInfo
    {
        Id = Guid.NewGuid().ToString("D"),
        Name = "Test adapter",
        Description = "Test adapter",
        InterfaceType = System.Net.NetworkInformation.NetworkInterfaceType.Ethernet,
        OperationalStatus = System.Net.NetworkInformation.OperationalStatus.Up,
        IsEnabled = true,
        Speed = 100_000_000,
        MacAddress = "00-00-00-00-00-00",
        IpAddresses = ["192.0.2.10"],
        IsLikelyVirtual = false,
        ClassificationLabel = "物理候选",
        CanToggle = true
    };

    Assert(adapter.EnabledStatusLabel == "已启用");
    Assert(adapter.LinkStatusLabel == "链路正常");
    Assert(adapter.InternetStatusLabel == "等待检测");
    Assert(adapter.BadgeStatusLabel == "未检测");

    adapter.LastProbeTime = DateTimeOffset.Now;
    adapter.ProbeReasonCode = "PROBE_ROUTE_UNAVAILABLE";
    Assert(adapter.InternetStatusLabel == "仅本地网络");
    Assert(adapter.BadgeStatusLabel == "仅本地");

    adapter.ProbeReasonCode = "PROBE_FAILED";
    Assert(adapter.InternetStatusLabel == "联网探测失败");
    Assert(adapter.BadgeStatusLabel == "联网失败");

    adapter.IsInternetOnline = true;
    Assert(adapter.InternetStatusLabel == "可访问互联网");
    Assert(adapter.BadgeStatusLabel == "可联网");

    adapter.OperationalStatus = System.Net.NetworkInformation.OperationalStatus.Down;
    Assert(adapter.LinkStatusLabel == "链路断开");
    Assert(adapter.InternetStatusLabel == "链路断开");
    Assert(adapter.BadgeStatusLabel == "链路断开");

    adapter.IsEnabled = false;
    Assert(adapter.EnabledStatusLabel == "已禁用");
    Assert(adapter.InternetStatusLabel == "网卡已禁用");
    Assert(adapter.BadgeStatusLabel == "已禁用");
}

static void RuleEngineWritesTestLogsToIsolatedDirectory()
{
    var directory = Path.Combine(Path.GetTempPath(), $"NetRelay-Log-Isolation-{Guid.NewGuid():N}");
    try
    {
        var logDirectory = Path.Combine(directory, "logs");
        var engine = new RuleEngine(
            new NativeNetworkConnectionService(),
            new ConnectivityService(),
            new ConfigurationService(directory),
            logDirectory);
        var record = CreateRecord(Guid.NewGuid(), RuleSource.Schedule);

        engine.WriteExecutionRecordAsync(record).GetAwaiter().GetResult();

        var logPath = Path.Combine(logDirectory, $"execution-{DateTime.Today:yyyy-MM-dd}.jsonl");
        Assert(File.Exists(logPath));
        Assert(File.ReadAllText(logPath).Contains(record.Id.ToString(), StringComparison.OrdinalIgnoreCase));
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static T GetPrivateField<T>(object instance, string fieldName)
{
    return (T)(instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
        ?.GetValue(instance)
        ?? throw new InvalidOperationException($"Missing private field: {fieldName}"));
}

static int RunVmnet1AcceptanceTest(string? reportPath)
{
    const string expectedName = "VMware Network Adapter VMnet1";
    if (!string.IsNullOrWhiteSpace(reportPath))
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        File.WriteAllText(reportPath, string.Empty);
    }

    void Report(string message, bool error = false)
    {
        if (error)
        {
            Console.Error.WriteLine(message);
        }
        else
        {
            Console.WriteLine(message);
        }

        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            File.AppendAllText(reportPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
    }

    var service = new NativeNetworkConnectionService();
    var before = FindVmnet1(service.GetConnections());
    if (before is null)
    {
        Report("FAIL: VMnet1 was not found in Windows Network Connections.", error: true);
        return 1;
    }

    if (!before.IsEnabled)
    {
        Report("FAIL: VMnet1 is already disabled; refusing to change its state.", error: true);
        return 1;
    }

    Report($"BEFORE: {before.Id:B} {before.Status} Enabled={before.IsEnabled}");
    var disabled = false;
    var acceptancePassed = false;
    var restorePassed = true;
    try
    {
        var disableResult = service.SetEnabled(before.Id.ToString("B"), expectedName, enabled: false);
        Report($"DISABLE: Success={disableResult.Success} Message={disableResult.Message}");
        if (!disableResult.Success)
        {
            return 1;
        }

        disabled = true;
        Thread.Sleep(TimeSpan.FromSeconds(2));
        var afterDisable = FindVmnet1(service.GetConnections());
        Report(afterDisable is null
            ? "AFTER_DISABLE: Missing"
            : $"AFTER_DISABLE: {afterDisable.Status} Enabled={afterDisable.IsEnabled}");
        if (afterDisable is null || afterDisable.IsEnabled)
        {
            Report("FAIL: VMnet1 was not retained as disabled.", error: true);
        }
        else
        {
            var uiAdapter = new NetworkAdapterService(service)
                .GetAdapters()
                .SingleOrDefault(adapter => AdapterIdentity.AreEqual(adapter.Id, before.Id.ToString("B")));
            Report(uiAdapter is null
                ? "UI_LIST_AFTER_DISABLE: Missing"
                : $"UI_LIST_AFTER_DISABLE: Status={uiAdapter.BadgeStatusLabel} Enabled={uiAdapter.IsEnabled} CanToggle={uiAdapter.CanToggle}");
            acceptancePassed = uiAdapter is { IsEnabled: false, CanToggle: true };
            if (!acceptancePassed)
            {
                Report("FAIL: VMnet1 was not retained as a disabled controllable UI adapter.", error: true);
            }
        }
    }
    finally
    {
        if (disabled)
        {
            var enableResult = service.SetEnabled(before.Id.ToString("B"), expectedName, enabled: true);
            Report($"ENABLE: Success={enableResult.Success} Message={enableResult.Message}");
            Thread.Sleep(TimeSpan.FromSeconds(2));
            var afterEnable = FindVmnet1(service.GetConnections());
            Report(afterEnable is null
                ? "AFTER_ENABLE: Missing"
                : $"AFTER_ENABLE: {afterEnable.Status} Enabled={afterEnable.IsEnabled}");
            if (!enableResult.Success || afterEnable is null || !afterEnable.IsEnabled)
            {
                Report("FAIL: VMnet1 could not be confirmed enabled after restoration.", error: true);
                restorePassed = false;
            }
        }
    }

    if (!restorePassed)
    {
        return 2;
    }

    if (!acceptancePassed)
    {
        return 1;
    }

    var ruleEnginePassed = false;
    var configDirectory = Path.Combine(Path.GetTempPath(), $"NetRelay-VMnet1-Acceptance-{Guid.NewGuid():N}");
    try
    {
        var configService = new ConfigurationService(configDirectory);
        var ruleEngine = new RuleEngine(
            service,
            new ConnectivityService(),
            configService,
            Path.Combine(configDirectory, "logs"));
        var rule = new AutomationRule(
            Guid.NewGuid(),
            "VMnet1 acceptance",
            Enabled: true,
            before.Id.ToString("B"),
            RuleAction.Disable,
            new RuleTrigger.Once(DateTimeOffset.Now),
            Conditions: [],
            PreNotifications: [],
            Recovery: null,
            RequireUsableBackup: false,
            CooldownSeconds: 300);

        var disableRecord = ruleEngine
            .ExecuteRuleAsync(rule, RuleSource.Schedule)
            .GetAwaiter()
            .GetResult();
        Report($"RULE_DISABLE: Outcome={disableRecord.Outcome} Reason={disableRecord.ReasonCode}");

        var recoveryRecord = ruleEngine
            .ExecuteRuleAsync(rule with { Action = RuleAction.Enable }, RuleSource.Recovery)
            .GetAwaiter()
            .GetResult();
        Report($"RULE_RECOVERY: Outcome={recoveryRecord.Outcome} Reason={recoveryRecord.ReasonCode}");
        Thread.Sleep(TimeSpan.FromSeconds(2));

        var afterRuleRecovery = FindVmnet1(service.GetConnections());
        Report(afterRuleRecovery is null
            ? "AFTER_RULE_RECOVERY: Missing"
            : $"AFTER_RULE_RECOVERY: {afterRuleRecovery.Status} Enabled={afterRuleRecovery.IsEnabled}");
        ruleEnginePassed = disableRecord.Outcome == "SUCCESS"
            && recoveryRecord.Outcome == "SUCCESS"
            && afterRuleRecovery is { IsEnabled: true };

        var scheduledRule = rule with
        {
            Id = Guid.NewGuid(),
            Name = "VMnet1 scheduler acceptance",
            Trigger = new RuleTrigger.Once(DateTimeOffset.Now.AddSeconds(2)),
            CooldownSeconds = 0
        };
        configService.Current.Rules.Add(scheduledRule);
        configService.Save();

        ExecutionRecord? scheduledRecord = null;
        using var scheduled = new ManualResetEventSlim();
        using (var scheduler = new RuleSchedulerService(ruleEngine, configService, new ConnectivityService()))
        {
            scheduler.RuleExecuted += (_, record) =>
            {
                if (record.RuleId == scheduledRule.Id)
                {
                    scheduledRecord = record;
                    scheduled.Set();
                }
            };
            scheduler.Start();
            scheduled.Wait(TimeSpan.FromSeconds(12));
            scheduler.Stop();
        }

        Report(scheduledRecord is null
            ? "SCHEDULER_RULE: Missing"
            : $"SCHEDULER_RULE: Outcome={scheduledRecord.Outcome} Reason={scheduledRecord.ReasonCode}");
        var schedulerRecovery = ruleEngine
            .ExecuteRuleAsync(scheduledRule with { Action = RuleAction.Enable }, RuleSource.Recovery)
            .GetAwaiter()
            .GetResult();
        Report($"SCHEDULER_RECOVERY: Outcome={schedulerRecovery.Outcome} Reason={schedulerRecovery.ReasonCode}");
        Thread.Sleep(TimeSpan.FromSeconds(2));
        var afterSchedulerRecovery = FindVmnet1(service.GetConnections());
        Report(afterSchedulerRecovery is null
            ? "AFTER_SCHEDULER_RECOVERY: Missing"
            : $"AFTER_SCHEDULER_RECOVERY: {afterSchedulerRecovery.Status} Enabled={afterSchedulerRecovery.IsEnabled}");
        ruleEnginePassed = ruleEnginePassed
            && scheduledRecord is { Outcome: "SUCCESS", Source: RuleSource.Schedule }
            && schedulerRecovery.Outcome == "SUCCESS"
            && afterSchedulerRecovery is { IsEnabled: true };
    }
    finally
    {
        var finalEnable = service.SetEnabled(before.Id.ToString("B"), expectedName, enabled: true);
        Report($"FINAL_ENABLE: Success={finalEnable.Success} Message={finalEnable.Message}");
        try
        {
            if (Directory.Exists(configDirectory))
            {
                Directory.Delete(configDirectory, recursive: true);
            }
        }
        catch
        {
            // A leftover temporary test configuration does not change adapter recovery.
        }
    }

    if (!ruleEnginePassed)
    {
        Report("FAIL: RuleEngine schedule disable and recovery acceptance test.", error: true);
        return 1;
    }

    Report("PASS: VMnet1 disable, UI list retention, RuleEngine execution, and restore acceptance test.");
    return 0;
}

static NativeConnectionInfo? FindVmnet1(IEnumerable<NativeConnectionInfo> connections)
{
    return connections.SingleOrDefault(connection =>
        string.Equals(connection.Name, "VMware Network Adapter VMnet1", StringComparison.Ordinal)
        && connection.DeviceName.Contains("VMnet1", StringComparison.OrdinalIgnoreCase));
}

static void SchedulerTriggersAutomaticRecoveryOnTimeElapsed()
{
    var directory = Path.Combine(Path.GetTempPath(), $"NetRelay-Recovery-Elapsed-{Guid.NewGuid():N}");
    try
    {
        var configService = new ConfigurationService(directory);
        var ruleId = Guid.NewGuid();
        var adapterId = Guid.NewGuid().ToString("B");
        var rule = new AutomationRule(
            ruleId,
            "Recovery elapsed rule",
            Enabled: true,
            adapterId,
            RuleAction.Disable,
            new RuleTrigger.Once(DateTimeOffset.Now.AddDays(1)),
            Conditions: [],
            PreNotifications: [],
            Recovery: new RecoveryPolicy(Enabled: true, DelayMinutes: 5),
            RequireUsableBackup: false,
            CooldownSeconds: 0);
        configService.Current.Rules.Add(rule);
        configService.Save();

        var engine = new RuleEngine(
            new NativeNetworkConnectionService(),
            new ConnectivityService(),
            configService,
            Path.Combine(directory, "logs"));

        // Write a successful disable log that happened 5 minutes ago
        var disableTime = DateTimeOffset.Now.AddMinutes(-5).AddSeconds(-1);
        var disableRecord = new ExecutionRecord(
            Guid.NewGuid(),
            ruleId,
            RuleSource.Schedule,
            adapterId,
            RuleAction.Disable,
            disableTime,
            disableTime,
            "SUCCESS",
            "OK",
            null);
        engine.WriteExecutionRecordAsync(disableRecord).GetAwaiter().GetResult();

        // Start scheduler and verify it executes recovery immediately
        using var scheduler = new RuleSchedulerService(engine, configService, new ConnectivityService());
        ExecutionRecord? recoveryRecord = null;
        using var eventSlim = new ManualResetEventSlim();
        scheduler.RuleExecuted += (_, record) =>
        {
            if (record.RuleId == ruleId && record.Source == RuleSource.Recovery)
            {
                recoveryRecord = record;
                eventSlim.Set();
            }
        };

        scheduler.Start();
        eventSlim.Wait(TimeSpan.FromSeconds(3));
        scheduler.Stop();

        Assert(recoveryRecord is not null);
        Assert(recoveryRecord!.Outcome == "FAILED");
        Assert(recoveryRecord.RequestedAction == RuleAction.Enable);
        Assert(recoveryRecord.Source == RuleSource.Recovery);
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void SchedulerSkipsExpiredAutomaticRecovery()
{
    var directory = Path.Combine(Path.GetTempPath(), $"NetRelay-Recovery-Expired-{Guid.NewGuid():N}");
    try
    {
        var configService = new ConfigurationService(directory);
        var ruleId = Guid.NewGuid();
        var adapterId = Guid.NewGuid().ToString("B");
        var rule = new AutomationRule(
            ruleId,
            "Recovery expired rule",
            Enabled: true,
            adapterId,
            RuleAction.Disable,
            new RuleTrigger.Once(DateTimeOffset.Now.AddDays(1)),
            Conditions: [],
            PreNotifications: [],
            Recovery: new RecoveryPolicy(Enabled: true, DelayMinutes: 5),
            RequireUsableBackup: false,
            CooldownSeconds: 0);
        configService.Current.Rules.Add(rule);
        configService.Save();

        var engine = new RuleEngine(
            new NativeNetworkConnectionService(),
            new ConnectivityService(),
            configService,
            Path.Combine(directory, "logs"));

        // Write a successful disable log that happened 8 minutes ago (which is past 5 mins + 2 mins tolerance = 7 mins)
        var disableTime = DateTimeOffset.Now.AddMinutes(-8);
        var disableRecord = new ExecutionRecord(
            Guid.NewGuid(),
            ruleId,
            RuleSource.Schedule,
            adapterId,
            RuleAction.Disable,
            disableTime,
            disableTime,
            "SUCCESS",
            "OK",
            null);
        engine.WriteExecutionRecordAsync(disableRecord).GetAwaiter().GetResult();

        using var scheduler = new RuleSchedulerService(engine, configService, new ConnectivityService());
        ExecutionRecord? recoveryRecord = null;
        using var eventSlim = new ManualResetEventSlim();
        scheduler.RuleExecuted += (_, record) =>
        {
            if (record.RuleId == ruleId && record.Source == RuleSource.Recovery)
            {
                recoveryRecord = record;
                eventSlim.Set();
            }
        };

        scheduler.Start();
        eventSlim.Wait(TimeSpan.FromSeconds(3));
        scheduler.Stop();

        Assert(recoveryRecord is not null);
        Assert(recoveryRecord!.Outcome == "SKIPPED");
        Assert(recoveryRecord.ReasonCode == "TRIGGER_EXPIRED");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void EvidenceHasherAnonymizesEvidenceProperly()
{
    var hash1 = EvidenceHasher.Hash("hardware.windowsDeviceId", "original-value-1");
    var hash2 = EvidenceHasher.Hash("hardware.windowsDeviceId", "original-value-1");
    var hash3 = EvidenceHasher.Hash("hardware.windowsDeviceId", "original-value-2");
    var hash4 = EvidenceHasher.Hash("hardware.machineGuid", "original-value-1");

    Assert(hash1.Length == 64);
    Assert(hash1 != "original-value-1");
    Assert(hash1 == hash2);
    Assert(hash1 != hash3);
    Assert(hash1 != hash4);
}

static void ConnectivityServiceGracefulDegradationOnBackendUnavailable()
{
    var listener = new HttpListener();
    listener.Prefixes.Add("http://127.0.0.1:54321/");
    listener.Start();

    // Start a background thread to handle a single request and return 503 Service Unavailable
    var listenTask = Task.Run(async () =>
    {
        try
        {
            var context = await listener.GetContextAsync();
            context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
            context.Response.StatusDescription = "Service Unavailable";
            context.Response.Close();
        }
        catch
        {
            // Ignore
        }
    });

    try
    {
        Environment.SetEnvironmentVariable("NETRELAY_BACKEND_URL", "http://127.0.0.1:54321");

        var method = typeof(ConnectivityService).GetMethod(
            "TryProbeChallengeAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert(method is not null);

        var localIp = IPAddress.Loopback;
        var timeout = TimeSpan.FromSeconds(2);
        var token = CancellationToken.None;

        var task = (Task)method!.Invoke(null, new object[] { localIp, timeout, token })!;
        task.Wait();

        // Get result
        var resultProperty = task.GetType().GetProperty("Result");
        var attempt = resultProperty!.GetValue(task);
        
        Assert(attempt is not null);
        var successProp = attempt!.GetType().GetProperty("Success");
        var errorMsgProp = attempt.GetType().GetProperty("ErrorMessage");

        var success = (bool)successProp!.GetValue(attempt)!;
        var errorMsg = (string?)errorMsgProp!.GetValue(attempt);

        Assert(!success);
        Assert(errorMsg != null && errorMsg.StartsWith("BACKEND_ERROR", StringComparison.Ordinal));

        // Test connection failure (e.g. no listener on port 54322)
        Environment.SetEnvironmentVariable("NETRELAY_BACKEND_URL", "http://127.0.0.1:54322");
        
        var method2 = typeof(ConnectivityService).GetMethod(
            "TryProbeChallengeAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        var task2 = (Task)method2!.Invoke(null, new object[] { IPAddress.Loopback, TimeSpan.FromSeconds(2), CancellationToken.None })!;
        task2.Wait();

        var attempt2 = task2.GetType().GetProperty("Result")!.GetValue(task2);
        var success2 = (bool)attempt2!.GetType().GetProperty("Success")!.GetValue(attempt2)!;
        var errorMsg2 = (string?)attempt2.GetType().GetProperty("ErrorMessage")!.GetValue(attempt2);

        Assert(!success2);
        Assert(errorMsg2 != null && !errorMsg2.StartsWith("BACKEND_ERROR", StringComparison.Ordinal));
    }
    finally
    {
        listener.Stop();
        Environment.SetEnvironmentVariable("NETRELAY_BACKEND_URL", null);
    }
}

static void TestLogPackagingLogic()
{
    var tempDir = Path.Combine(Path.GetTempPath(), $"netrelay_test_logs_{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);
    var zipPath = Path.Combine(Path.GetTempPath(), $"netrelay_test_zip_{Guid.NewGuid():N}.zip");

    try
    {
        var file1 = Path.Combine(tempDir, "execution-20260613.jsonl");
        var file2 = Path.Combine(tempDir, "execution-20260614.jsonl");
        var file3 = Path.Combine(tempDir, "other.txt");

        File.WriteAllText(file1, "line1\nline2");
        File.WriteAllText(file2, "line3\nline4");
        File.WriteAllText(file3, "ignored");

        var logService = new LogService(tempDir);
        logService.CreateDiagnosticZipAsync(zipPath).Wait();

        Assert(File.Exists(zipPath));

        using (var archive = System.IO.Compression.ZipFile.OpenRead(zipPath))
        {
            Assert(archive.Entries.Count == 2);
            var entryNames = archive.Entries.Select(e => e.Name).ToList();
            Assert(entryNames.Contains("execution-20260613.jsonl"));
            Assert(entryNames.Contains("execution-20260614.jsonl"));
            Assert(!entryNames.Contains("other.txt"));
        }
    }
    finally
    {
        try { Directory.Delete(tempDir, true); } catch {}
        try { File.Delete(zipPath); } catch {}
    }
}

static void RunOnSTA(Action action)
{
    var tcs = new TaskCompletionSource<object?>();
    var thread = new Thread(() =>
    {
        try
        {
            action();
            tcs.SetResult(null);
        }
        catch (Exception ex)
        {
            tcs.SetException(ex);
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    tcs.Task.GetAwaiter().GetResult();
}

static void TestSafeMarkdownParser()
{
    RunOnSTA(() =>
    {
        var doc = SafeMarkdownParser.Parse("# Hello\nSome **bold** text\n- List item\n[Safe Link](https://google.com)\n[Unsafe Link](file:///c:/)");
        Assert(doc != null);
        Assert(doc!.Blocks.Count >= 5);

        var p0 = doc.Blocks.ElementAt(0) as System.Windows.Documents.Paragraph;
        Assert(p0 != null);
        Assert(p0!.FontSize == 16);
        Assert(p0.FontWeight == System.Windows.FontWeights.Bold);

        var p1 = doc.Blocks.ElementAt(1) as System.Windows.Documents.Paragraph;
        Assert(p1 != null);
        Assert(p1!.Inlines.Count == 3);
        var run0 = p1.Inlines.ElementAt(0) as System.Windows.Documents.Run;
        var run1 = p1.Inlines.ElementAt(1) as System.Windows.Documents.Run;
        var run2 = p1.Inlines.ElementAt(2) as System.Windows.Documents.Run;
        Assert(run0?.Text == "Some ");
        Assert(run1?.Text == "bold");
        Assert(run1?.FontWeight == System.Windows.FontWeights.Bold);
        Assert(run2?.Text == " text");

        var list = doc.Blocks.ElementAt(2) as System.Windows.Documents.List;
        Assert(list != null);
        Assert(list!.ListItems.Count == 1);

        var p3 = doc.Blocks.ElementAt(3) as System.Windows.Documents.Paragraph;
        Assert(p3 != null);
        Assert(p3!.Inlines.Count == 1);
        var hyper = p3.Inlines.FirstInline as System.Windows.Documents.Hyperlink;
        Assert(hyper != null);
        Assert(hyper!.NavigateUri?.AbsoluteUri == "https://google.com/");

        var p4 = doc.Blocks.ElementAt(4) as System.Windows.Documents.Paragraph;
        Assert(p4 != null);
        Assert(p4!.Inlines.Count == 1);
        var grayRun = p4.Inlines.FirstInline as System.Windows.Documents.Run;
        Assert(grayRun != null);
        Assert(grayRun!.Text == "Unsafe Link (file:///c:/)");
        Assert(grayRun.Foreground == System.Windows.Media.Brushes.Gray);
    });
}

