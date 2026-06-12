using System.Net.NetworkInformation;
using NetRelay.Models;
using NetRelay.Services;

namespace NetRelay.ViewModels;

public sealed class ExecutionRecordViewModel
{
    public ExecutionRecord Record { get; }
    public string TargetAdapterName { get; }
    
    public string TimeLabel => Record.StartedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
    
    public string OutcomeLabel => Record.Outcome switch
    {
        "SUCCESS" => "执行成功",
        "SKIPPED" => "已跳过",
        "FAILED" => "执行失败",
        _ => Record.Outcome
    };

    public string OutcomeBrush => Record.Outcome switch
    {
        "SUCCESS" => "#27B588", // Green
        "SKIPPED" => "#536EF2", // Blue
        "FAILED" => "#E85C63",  // Red
        _ => "#58677E"
    };

    public string ReasonLabel => Record.ReasonCode switch
    {
        "OK" => "操作成功完成",
        "RULE_COOLDOWN_ACTIVE" => "规则处于冷却期内被忽略",
        "BACKUP_NETWORK_UNAVAILABLE" => "安全保护：无可用备份互联网，操作被中止",
        "ADAPTER_NOT_FOUND" => "找不到目标网卡接口",
        "ADAPTER_OPERATION_FAILED" => "控制网卡开关失败 (COM 错误)",
        "TRIGGER_EXPIRED" => "触发已过期 (迟到太久被忽略)",
        "MANUAL_SKIP" => "用户手动取消了本次执行",
        _ => $"原因码: {Record.ReasonCode}"
    };

    public string ActionLabel => Record.RequestedAction == RuleAction.Enable ? "启用网卡" : "禁用网卡";

    public string SourceLabel => Record.Source switch
    {
        RuleSource.Manual => "手动立即执行",
        RuleSource.Schedule => "定时计划触发",
        RuleSource.NetworkChange => "网络离线触发",
        RuleSource.Recovery => "延时自动恢复",
        _ => Record.Source.ToString()
    };

    public ExecutionRecordViewModel(ExecutionRecord record, NativeNetworkConnectionService connectionService)
    {
        Record = record;
        TargetAdapterName = ResolveAdapterName(record.TargetAdapterId, connectionService);
    }

    private static string ResolveAdapterName(string adapterId, NativeNetworkConnectionService connectionService)
    {
        try
        {
            var ni = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => string.Equals(n.Id, adapterId, StringComparison.OrdinalIgnoreCase));
            if (ni != null) return ni.Name;

            var conn = connectionService.GetConnections()
                .FirstOrDefault(c => string.Equals(c.Id.ToString("B"), adapterId, StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(c.Id.ToString(), adapterId, StringComparison.OrdinalIgnoreCase));
            if (conn != null) return conn.Name;
        }
        catch { }

        if (Guid.TryParse(adapterId, out var g))
        {
            var s = g.ToString().ToUpper();
            return $"未知网卡 (...{s.Substring(s.Length - 8)})";
        }
        return $"未知网卡 ({adapterId})";
    }
}
