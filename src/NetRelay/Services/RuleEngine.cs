using System.Collections.Concurrent;
using System.IO;
using System.Net.NetworkInformation;
using System.Text.Json;
using NetRelay.Models;

namespace NetRelay.Services;

public sealed class RuleEngine
{
    private readonly NativeNetworkConnectionService _connectionService;
    private readonly ConnectivityService _connectivityService;
    private readonly ConfigurationService _configService;
    private readonly string _executionLogDirectory;
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastExecutionTimes = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _ruleLocks = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _adapterLocks = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] VirtualAdapterKeywords =
    [
        "virtual", "vmware", "hyper-v", "vethernet", "virtualbox", "vpn", "tap-",
        "tunnel", "loopback", "pseudo-interface", "teredo", "isatap", "wsl",
        "docker", "mihomo", "clash", "zerotier", "tailscale"
    ];

    public event EventHandler<ExecutionRecord>? ExecutionRecorded;
    public string ExecutionLogDirectory => _executionLogDirectory;

    public RuleEngine(
        NativeNetworkConnectionService connectionService,
        ConnectivityService connectivityService,
        ConfigurationService configService,
        string? executionLogDirectory = null)
    {
        _connectionService = connectionService;
        _connectivityService = connectivityService;
        _configService = configService;
        _executionLogDirectory = executionLogDirectory ?? GetDefaultExecutionLogDirectory();
    }

    public void ResetRuleRuntimeState(Guid ruleId)
    {
        _lastExecutionTimes.TryRemove(ruleId, out _);
    }

    public async Task<ExecutionRecord> ExecuteRuleAsync(AutomationRule rule, RuleSource source)
    {
        var ruleLock = _ruleLocks.GetOrAdd(rule.Id, _ => new SemaphoreSlim(1, 1));
        var adapterLock = _adapterLocks.GetOrAdd(rule.TargetAdapterId, _ => new SemaphoreSlim(1, 1));

        await ruleLock.WaitAsync();
        try
        {
            await adapterLock.WaitAsync();
            try
            {
                return await ExecuteRuleCoreAsync(rule, source);
            }
            finally
            {
                adapterLock.Release();
            }
        }
        finally
        {
            ruleLock.Release();
        }
    }

    private async Task<ExecutionRecord> ExecuteRuleCoreAsync(AutomationRule rule, RuleSource source)
    {
        var startedAt = DateTimeOffset.Now;
        var recordId = Guid.NewGuid();

        if (RuleExecutionPolicy.RequiresValidProbePolicy(source)
            && !_configService.IsAutomationEnabled)
        {
            var record = new ExecutionRecord(
                recordId,
                rule.Id,
                source,
                rule.TargetAdapterId,
                rule.Action,
                startedAt,
                DateTimeOffset.Now,
                Outcome: "SKIPPED",
                ReasonCode: "CONFIG_INVALID",
                WindowsErrorCode: null
            );
            await RecordExecutionAsync(record);
            return record;
        }

        // 1. Cooldown Check
        _lastExecutionTimes.TryGetValue(rule.Id, out var lastExecuted);
        if (RuleExecutionPolicy.IsCooldownActive(
                source,
                rule.CooldownSeconds,
                lastExecuted == default ? null : lastExecuted,
                startedAt))
        {
            var record = new ExecutionRecord(
                recordId,
                rule.Id,
                source,
                rule.TargetAdapterId,
                rule.Action,
                startedAt,
                DateTimeOffset.Now,
                Outcome: "SKIPPED",
                ReasonCode: "RULE_COOLDOWN_ACTIVE",
                WindowsErrorCode: null
            );
            await RecordExecutionAsync(record);
            return record;
        }

        if (source != RuleSource.Recovery && !await AreConditionsSatisfiedAsync(rule.Conditions))
        {
            var record = new ExecutionRecord(
                recordId,
                rule.Id,
                source,
                rule.TargetAdapterId,
                rule.Action,
                startedAt,
                DateTimeOffset.Now,
                Outcome: "SKIPPED",
                ReasonCode: "CONDITION_NOT_MET",
                WindowsErrorCode: null
            );
            await RecordExecutionAsync(record);
            return record;
        }

        // 2. Backup network check for RuleAction.Disable
        var validatedBackupAdapterIds = new List<string>();
        if (rule.Action == RuleAction.Disable && rule.RequireUsableBackup)
        {
            var targetNi = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(ni => string.Equals(ni.Id, rule.TargetAdapterId, StringComparison.OrdinalIgnoreCase));

            var isVirtual = targetNi != null && IsVirtualHeuristic(targetNi);
            if (!isVirtual)
            {
                validatedBackupAdapterIds = await GetUsableBackupAdaptersAsync(rule.TargetAdapterId);
                if (validatedBackupAdapterIds.Count == 0)
                {
                    var record = new ExecutionRecord(
                        recordId,
                        rule.Id,
                        source,
                        rule.TargetAdapterId,
                        rule.Action,
                        startedAt,
                        DateTimeOffset.Now,
                        Outcome: "SKIPPED",
                        ReasonCode: "BACKUP_NETWORK_UNAVAILABLE",
                        WindowsErrorCode: null
                    );
                    await RecordExecutionAsync(record);
                    return record;
                }
            }
        }

        // 3. Perform actual action
        bool targetEnabled = rule.Action == RuleAction.Enable;
        string adapterName = "";

        var niTarget = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(ni => string.Equals(ni.Id, rule.TargetAdapterId, StringComparison.OrdinalIgnoreCase));
        var nativeConnections = _connectionService.GetConnections();

        if (source == RuleSource.Recovery
            && targetEnabled
            && RuleExecutionPolicy.IsAdapterAlreadyEnabledForRecovery(nativeConnections, rule.TargetAdapterId))
        {
            var record = new ExecutionRecord(
                recordId,
                rule.Id,
                source,
                rule.TargetAdapterId,
                rule.Action,
                startedAt,
                DateTimeOffset.Now,
                Outcome: "SUCCESS",
                ReasonCode: "RECOVERY_ALREADY_ENABLED",
                WindowsErrorCode: null
            );
            await RecordExecutionAsync(record);
            return record;
        }

        if (niTarget != null)
        {
            adapterName = niTarget.Name;
        }
        else
        {
            var conn = nativeConnections
                .FirstOrDefault(c => string.Equals(c.Id.ToString("B"), rule.TargetAdapterId, StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(c.Id.ToString(), rule.TargetAdapterId, StringComparison.OrdinalIgnoreCase));
            if (conn != null)
            {
                adapterName = conn.Name;
            }
        }

        if (string.IsNullOrEmpty(adapterName))
        {
            var record = new ExecutionRecord(
                recordId,
                rule.Id,
                source,
                rule.TargetAdapterId,
                rule.Action,
                startedAt,
                DateTimeOffset.Now,
                Outcome: "FAILED",
                ReasonCode: "ADAPTER_NOT_FOUND",
                WindowsErrorCode: null
            );
            await RecordExecutionAsync(record);
            return record;
        }

        // Only an actual adapter operation starts cooldown. Recovery never shifts the normal cooldown window.
        if (source != RuleSource.Recovery)
        {
            _lastExecutionTimes[rule.Id] = startedAt;
        }

        var toggleResult = await Task.Run(() => _connectionService.SetEnabled(rule.TargetAdapterId, adapterName, targetEnabled));

        var finishedAt = DateTimeOffset.Now;
        var outcome = toggleResult.Success ? "SUCCESS" : "FAILED";
        var reasonCode = toggleResult.Success ? "OK" : "ADAPTER_OPERATION_FAILED";

        if (RuleExecutionPolicy.ShouldValidatePostSwitch(
                toggleResult.Success,
                rule.Action,
                validatedBackupAdapterIds.Count))
        {
            await Task.Delay(TimeSpan.FromSeconds(3));

            if (!await IsAnyAdapterOnlineAsync(validatedBackupAdapterIds))
            {
                var rollbackStartedAt = DateTimeOffset.Now;
                var rollbackResult = await Task.Run(
                    () => _connectionService.SetEnabled(rule.TargetAdapterId, adapterName, enabled: true));
                var rollbackRecord = new ExecutionRecord(
                    Guid.NewGuid(),
                    rule.Id,
                    RuleSource.Recovery,
                    rule.TargetAdapterId,
                    RuleAction.Enable,
                    rollbackStartedAt,
                    DateTimeOffset.Now,
                    Outcome: rollbackResult.Success ? "SUCCESS" : "FAILED",
                    ReasonCode: rollbackResult.Success ? "ROLLBACK_SUCCEEDED" : "ROLLBACK_FAILED",
                    WindowsErrorCode: rollbackResult.WindowsErrorCode
                );
                await RecordExecutionAsync(rollbackRecord);

                outcome = "FAILED";
                reasonCode = "POST_SWITCH_VALIDATION_FAILED";
                finishedAt = DateTimeOffset.Now;
            }
        }

        var finalRecord = new ExecutionRecord(
            recordId,
            rule.Id,
            source,
            rule.TargetAdapterId,
            rule.Action,
            startedAt,
            finishedAt,
            Outcome: outcome,
            ReasonCode: reasonCode,
            WindowsErrorCode: toggleResult.WindowsErrorCode
        );

        await RecordExecutionAsync(finalRecord);

        return finalRecord;
    }

    private async Task<bool> IsAnyAdapterOnlineAsync(IEnumerable<string> adapterIds)
    {
        var policy = _configService.Current.ProbePolicy;
        foreach (var adapterId in adapterIds)
        {
            var result = await _connectivityService.ProbeAdapterAsync(adapterId, policy);
            if (result.Online)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> AreConditionsSatisfiedAsync(IEnumerable<RuleCondition>? conditions)
    {
        foreach (var condition in conditions ?? [])
        {
            if (condition is not AdapterOfflineCondition adapterOffline)
            {
                return false;
            }

            var result = await _connectivityService.ProbeAdapterAsync(
                adapterOffline.AdapterId,
                _configService.Current.ProbePolicy);
            if (result.Online)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsVirtualHeuristic(NetworkInterface adapter)
    {
        if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback
            or NetworkInterfaceType.Tunnel
            or NetworkInterfaceType.Ppp)
        {
            return true;
        }

        var identity = $"{adapter.Name} {adapter.Description}";
        return VirtualAdapterKeywords.Any(keyword => identity.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private async Task RecordExecutionAsync(ExecutionRecord record)
    {
        await WriteExecutionRecordAsync(record);
        try
        {
            ExecutionRecorded?.Invoke(this, record);
        }
        catch
        {
            // Notification and UI subscribers must not break rule execution.
        }
    }

    public async Task WriteExecutionRecordAsync(ExecutionRecord record)
    {
        try
        {
            Directory.CreateDirectory(_executionLogDirectory);
            var logPath = Path.Combine(_executionLogDirectory, $"execution-{DateTime.Today:yyyy-MM-dd}.jsonl");

            var line = JsonSerializer.Serialize(record) + Environment.NewLine;
            await File.AppendAllTextAsync(logPath, line);
        }
        catch
        {
            // Fail silently on logging failure to not crash the rule engine
        }
    }

    public async Task<bool> IsBackupNetworkUsableAsync(string targetAdapterId)
    {
        var targetNi = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(ni => string.Equals(ni.Id, targetAdapterId, StringComparison.OrdinalIgnoreCase));

        // 如果目标网卡本身就是虚拟网卡，禁用它不会切断真实外网，因此无需强制要求有备份网络
        if (targetNi != null && IsVirtualHeuristic(targetNi))
        {
            return true;
        }

        var backups = await GetUsableBackupAdaptersAsync(targetAdapterId);
        return backups.Count > 0;
    }

    public async Task<List<string>> GetUsableBackupAdaptersAsync(string targetAdapterId)
    {
        var validatedBackupAdapterIds = new List<string>();
        var targetNi = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(ni => string.Equals(ni.Id, targetAdapterId, StringComparison.OrdinalIgnoreCase));

        if (targetNi == null || !IsVirtualHeuristic(targetNi))
        {
            var currentInterfaces = NetworkInterface.GetAllNetworkInterfaces();
            var backupInterfaces = currentInterfaces
                .Where(ni => !string.Equals(ni.Id, targetAdapterId, StringComparison.OrdinalIgnoreCase) &&
                             ni.OperationalStatus == OperationalStatus.Up)
                .ToList();

            var physicalBackups = backupInterfaces
                .Where(ni => !IsVirtualHeuristic(ni))
                .ToList();

            var policy = _configService.Current.ProbePolicy;
            foreach (var backup in physicalBackups)
            {
                var result = await _connectivityService.ProbeAdapterAsync(backup.Id, policy);
                if (result.Online)
                {
                    validatedBackupAdapterIds.Add(backup.Id);
                }
            }
        }

        return validatedBackupAdapterIds;
    }

    private static string GetDefaultExecutionLogDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "NetRelay", "logs");
    }
}
