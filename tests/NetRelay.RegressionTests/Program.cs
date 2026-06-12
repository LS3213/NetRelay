using NetRelay.Models;
using NetRelay.Services;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

if (args.FirstOrDefault() == "--acceptance-toggle-vmnet1")
{
    return RunVmnet1AcceptanceTest(args.Skip(1).FirstOrDefault());
}

var tests = new (string Name, Action Test)[]
{
    ("Valid probe policy is accepted", ValidProbePolicyIsAccepted),
    ("Probe policy supports ping and dns", ProbePolicySupportsPingAndDns),
    ("DNS query building and parsing works", DnsQueryBuildingAndParsingWorks),
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
    ("Expected disabled state overrides transient disconnected status", ExpectedDisabledStateOverridesTransientDisconnectedStatus),
    ("Adapter identity ignores GUID formatting", AdapterIdentityIgnoresGuidFormatting),
    ("Read-only adapter diagnostic report is created", ReadOnlyAdapterDiagnosticReportIsCreated),
    ("Auto-start task must target current executable", AutoStartTaskMustTargetCurrentExecutable),
    ("Scheduler execution gate rejects re-entry", SchedulerExecutionGateRejectsReentry),
    ("Editing a rule resets scheduler runtime state", EditingRuleResetsSchedulerRuntimeState),
    ("Editing a rule resets engine cooldown state", EditingRuleResetsEngineCooldownState),
    ("Rule engine writes test logs to isolated directory", RuleEngineWritesTestLogsToIsolatedDirectory),
    ("Adapter UI statuses distinguish link and internet", AdapterUiStatusesDistinguishLinkAndInternet),
    ("Single instance service signals primary instance", SingleInstanceServiceSignalsPrimaryInstance),
    ("Scheduler triggers automatic recovery on time elapsed", SchedulerTriggersAutomaticRecoveryOnTimeElapsed),
    ("Scheduler skips expired automatic recovery", SchedulerSkipsExpiredAutomaticRecovery)
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
    var parseMethod = typeof(ConnectivityService).GetMethod("ParseDnsResponseIp", BindingFlags.NonPublic | BindingFlags.Static);

    Assert(buildMethod is not null);
    Assert(parseMethod is not null);

    // 1. Build query for IPv4
    var queryBytes = (byte[])buildMethod!.Invoke(null, new object[] { "google.com", false })!;
    Assert(queryBytes.Length > 0);

    // 2. Build mock response
    var questionLength = queryBytes.Length - 12;
    var response = new byte[12 + questionLength + 20];
    
    // Header
    response[0] = 0x12; response[1] = 0x34;
    response[2] = 0x81; response[3] = 0x80;
    response[4] = 0x00; response[5] = 0x01; // 1 Question
    response[6] = 0x00; response[7] = 0x01; // 1 Answer

    // Copy question
    Array.Copy(queryBytes, 12, response, 12, questionLength);

    int index = 12 + questionLength;
    // Answer
    response[index++] = 0xC0; response[index++] = 0x0C; // Name pointer
    response[index++] = 0x00; response[index++] = 0x01; // Type A
    response[index++] = 0x00; response[index++] = 0x01; // Class IN
    response[index++] = 0x00; response[index++] = 0x00; response[index++] = 0x00; response[index++] = 0x3C; // TTL
    response[index++] = 0x00; response[index++] = 0x04; // Data length 4
    response[index++] = 8; response[index++] = 8; response[index++] = 8; response[index++] = 8; // IP

    var resolved = (IPAddress?)parseMethod!.Invoke(null, new object[] { response, index, AddressFamily.InterNetwork })!;
    Assert(resolved is not null);
    Assert(resolved!.ToString() == "8.8.8.8");

    // 3. Build query for IPv6
    var queryBytesV6 = (byte[])buildMethod!.Invoke(null, new object[] { "google.com", true })!;
    Assert(queryBytesV6.Length > 0);

    var questionLengthV6 = queryBytesV6.Length - 12;
    var responseV6 = new byte[12 + questionLengthV6 + 32];
    responseV6[0] = 0x12; responseV6[1] = 0x34;
    responseV6[2] = 0x81; responseV6[3] = 0x80;
    responseV6[4] = 0x00; responseV6[5] = 0x01;
    responseV6[6] = 0x00; responseV6[7] = 0x01;

    Array.Copy(queryBytesV6, 12, responseV6, 12, questionLengthV6);

    int indexV6 = 12 + questionLengthV6;
    responseV6[indexV6++] = 0xC0; responseV6[indexV6++] = 0x0C;
    responseV6[indexV6++] = 0x00; responseV6[indexV6++] = 0x1C; // Type AAAA = 28
    responseV6[indexV6++] = 0x00; responseV6[indexV6++] = 0x01;
    responseV6[indexV6++] = 0x00; responseV6[indexV6++] = 0x00; responseV6[indexV6++] = 0x00; responseV6[indexV6++] = 0x3C;
    responseV6[indexV6++] = 0x00; responseV6[indexV6++] = 0x10; // Data length 16
    for (int k = 0; k < 16; k++) responseV6[indexV6++] = 0xFF; // IP v6 address

    var resolvedV6 = (IPAddress?)parseMethod!.Invoke(null, new object[] { responseV6, indexV6, AddressFamily.InterNetworkV6 })!;
    Assert(resolvedV6 is not null);
    Assert(resolvedV6!.ToString() == "ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff");
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

        var first = engine.ExecuteRuleAsync(rule, RuleSource.Schedule).GetAwaiter().GetResult();
        var second = engine.ExecuteRuleAsync(rule, RuleSource.Schedule).GetAwaiter().GetResult();
        engine.ResetRuleRuntimeState(rule.Id);
        var afterEdit = engine.ExecuteRuleAsync(rule, RuleSource.Schedule).GetAwaiter().GetResult();

        Assert(first.ReasonCode == "ADAPTER_NOT_FOUND");
        Assert(second.ReasonCode == "RULE_COOLDOWN_ACTIVE");
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
