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
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastExecutionTimes = new();

    private static readonly string[] VirtualAdapterKeywords =
    [
        "virtual", "vmware", "hyper-v", "vethernet", "virtualbox", "vpn", "tap-",
        "tunnel", "loopback", "pseudo-interface", "teredo", "isatap", "wsl",
        "docker", "mihomo", "clash", "zerotier", "tailscale"
    ];

    public RuleEngine(
        NativeNetworkConnectionService connectionService,
        ConnectivityService connectivityService,
        ConfigurationService configService)
    {
        _connectionService = connectionService;
        _connectivityService = connectivityService;
        _configService = configService;
    }

    public async Task<ExecutionRecord> ExecuteRuleAsync(AutomationRule rule, RuleSource source)
    {
        var startedAt = DateTimeOffset.Now;
        var recordId = Guid.NewGuid();

        // 1. Cooldown Check
        if (rule.CooldownSeconds > 0)
        {
            if (_lastExecutionTimes.TryGetValue(rule.Id, out var lastExecuted))
            {
                var elapsed = startedAt - lastExecuted;
                if (elapsed.TotalSeconds < rule.CooldownSeconds)
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
                    await WriteExecutionRecordAsync(record);
                    return record;
                }
            }
        }

        // Update last execution time
        _lastExecutionTimes[rule.Id] = startedAt;

        // 2. Backup network check for RuleAction.Disable
        if (rule.Action == RuleAction.Disable && rule.RequireUsableBackup)
        {
            var targetNi = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(ni => string.Equals(ni.Id, rule.TargetAdapterId, StringComparison.OrdinalIgnoreCase));

            // 如果目标网卡本身就是虚拟网卡，禁用它不会切断真实外网，因此无需强制要求有备份网络
            if (targetNi == null || !IsVirtualHeuristic(targetNi))
            {
                var currentInterfaces = NetworkInterface.GetAllNetworkInterfaces();
                var backupInterfaces = currentInterfaces
                    .Where(ni => !string.Equals(ni.Id, rule.TargetAdapterId, StringComparison.OrdinalIgnoreCase) &&
                                 ni.OperationalStatus == OperationalStatus.Up)
                    .ToList();

                var physicalBackups = backupInterfaces
                    .Where(ni => !IsVirtualHeuristic(ni))
                    .ToList();

                if (physicalBackups.Count == 0)
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
                    await WriteExecutionRecordAsync(record);
                    return record;
                }

                bool anyBackupOnline = false;
                var policy = _configService.Current.ProbePolicy;
                foreach (var backup in physicalBackups)
                {
                    var result = await _connectivityService.ProbeAdapterAsync(backup.Id, policy);
                    if (result.Online)
                    {
                        anyBackupOnline = true;
                        break;
                    }
                }

                if (!anyBackupOnline)
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
                    await WriteExecutionRecordAsync(record);
                    return record;
                }
            }
        }

        // 3. Perform actual action
        bool targetEnabled = rule.Action == RuleAction.Enable;
        string adapterName = "";
        
        var niTarget = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(ni => string.Equals(ni.Id, rule.TargetAdapterId, StringComparison.OrdinalIgnoreCase));
        if (niTarget != null)
        {
            adapterName = niTarget.Name;
        }
        else
        {
            var conn = _connectionService.GetConnections()
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
            await WriteExecutionRecordAsync(record);
            return record;
        }

        var toggleResult = await Task.Run(() => _connectionService.SetEnabled(rule.TargetAdapterId, adapterName, targetEnabled));

        var finishedAt = DateTimeOffset.Now;
        var outcome = toggleResult.Success ? "SUCCESS" : "FAILED";
        var reasonCode = toggleResult.Success ? "OK" : "ADAPTER_OPERATION_FAILED";

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

        await WriteExecutionRecordAsync(finalRecord);

        // 4. Auto recovery queue
        if (toggleResult.Success && rule.Action == RuleAction.Disable && rule.Recovery is { Enabled: true, DelayMinutes: var delayMinutes and > 0 })
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(delayMinutes));
                    var recoveryRule = rule with { Action = RuleAction.Enable };
                    await ExecuteRuleAsync(recoveryRule, RuleSource.Recovery);
                }
                catch
                {
                    // Ignore exceptions to keep background task safe
                }
            });
        }

        return finalRecord;
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

    public static async Task WriteExecutionRecordAsync(ExecutionRecord record)
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var logDir = Path.Combine(appData, "NetRelay", "logs");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, $"execution-{DateTime.Today:yyyy-MM-dd}.jsonl");
            
            var line = JsonSerializer.Serialize(record) + Environment.NewLine;
            await File.AppendAllTextAsync(logPath, line);
        }
        catch
        {
            // Fail silently on logging failure to not crash the rule engine
        }
    }
}
