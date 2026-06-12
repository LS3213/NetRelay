# 接口与数据模型

[上一篇：功能与 UI 规格](03-functional-and-ui-spec.md) | [返回索引](README.md) | [下一篇：网络检测与自动化](05-network-detection-and-automation.md)

> 当前网卡控制、联网探测、规则执行、内联调度、配置与执行日志模型已实现。下表明确标记直接服务方法、UI 编排行为和未实现接口。

## 1. 应用服务接口

| 方法 | 输入 | 输出 | 状态 |
| --- | --- | --- | --- |
| `NetworkAdapterService.GetAdapters` | 无 | `IReadOnlyList<NetworkAdapterInfo>` | **已实现**：枚举基础网卡状态 |
| `NativeNetworkConnectionService.SetEnabled` | GUID、名称、启用状态 | `AdapterActionResult` | **已实现**：使用 `INetConnection.Connect/Disconnect` |
| `ProbeAdapterAsync` | GUID、探测策略 | `ConnectivityResult` | **已实现**：通过 HTTP 进行可用性探测 |
| 规则列表加载 | 无 | `AutomationRule[]` | **已实现（UI 编排）**：ViewModel 从配置加载规则 |
| 规则保存/删除 | 规则或规则 ID | 无 | **已实现（UI 编排）**：修改配置、原子保存并热重载 |
| `RuleEngine.ExecuteRuleAsync` | 规则、来源 | `ExecutionRecord` | **已实现**：立即或自动执行规则 |
| `LogService.LoadLogsAsync` | 天数限制 | `ExecutionRecord[]` | **已实现**：读取最近若干天的 JSONL 日志 |
| `PauseAutomationAsync` | 暂停状态 | `ActionResult` | **未实现**：无全局人工暂停开关；仅无效探测配置会自动暂停 |

服务方法应返回结构化结果，不向 ViewModel 暴露底层 COM/P/Invoke 异常细节。ViewModel 使用 `INotifyPropertyChanged`、`ObservableCollection<T>` 和 `ICommand` 与 XAML 绑定。

## 2. CLI

当前同一个原生 EXE 仅识别：

```text
NetRelay.exe --startup
```

- **`--startup`**：以开机自启模式运行应用。主窗口会直接隐藏到系统托盘，不显示任务栏图标，不发生任何窗口显示闪烁。
- 开机自启使用固定名称 `NetRelay AutoStart` 的 Windows 登录任务，动作仅允许为当前 NetRelay 可执行文件与 `--startup` 参数。
`--execute-rule`、`--notify-rule` 与 `--restore-adapter` 尚未实现。当前程序也尚未实现完整 CLI 错误码与未知参数拒绝机制。

## 3. 应用事件

| .NET 事件 | 载荷 | 用途 |
| --- | --- | --- |
| `RuleSchedulerService.PreNotificationTriggered` | `PreNotificationEventArgs` | 显示提前提醒与倒计时 |
| `RuleSchedulerService.RuleExecuted` | `ExecutionRecord` | 刷新执行历史与规则状态 |
| `RuleEngine.ExecutionRecorded` | `ExecutionRecord` | 发送自动执行结果气泡通知 |
| `MainViewModel.RequestEditRule` | `AutomationRule?` | 打开新增或编辑规则对话框 |

事件由应用服务发布，ViewModel 订阅后切换回 WPF Dispatcher 更新 UI。

## 4. 当前已实现模型

```csharp
public sealed class NetworkAdapterInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required NetworkInterfaceType InterfaceType { get; init; }
    public required OperationalStatus OperationalStatus { get; init; }
    public required long Speed { get; init; }
    public required string MacAddress { get; init; }
    public required IReadOnlyList<string> IpAddresses { get; init; }
}
```

当前 `Id` 来源于 .NET `NetworkInterface.Id` 或 Windows Network Connections GUID；启用/禁用前会重新在原生连接枚举中校验 GUID。

## 5. 当前规则模型

```csharp
public enum RuleAction { Enable, Disable }
public enum RuleSource { Manual, Schedule, NetworkChange, Recovery }

public sealed record AutomationRule(
    Guid Id,
    string Name,
    bool Enabled,
    string TargetAdapterId,
    RuleAction Action,
    RuleTrigger Trigger,
    IReadOnlyList<RuleCondition> Conditions,
    IReadOnlyList<PreNotification> PreNotifications,
    RecoveryPolicy? Recovery,
    bool RequireUsableBackup,
    int CooldownSeconds);

public abstract record RuleTrigger
{
    public sealed record Once(DateTimeOffset At) : RuleTrigger;
    public sealed record Daily(TimeOnly LocalTime) : RuleTrigger;
    public sealed record Weekly(TimeOnly LocalTime, IReadOnlySet<DayOfWeek> Weekdays) : RuleTrigger;
    public sealed record NetworkChange(RuleCondition Condition, int DebounceSeconds) : RuleTrigger;
}

public sealed record PreNotification(
    Guid Id,
    int MinutesBefore,
    bool AllowDelay,
    int DelayMinutes,
    bool AllowCancelOccurrence);

public sealed record RecoveryPolicy(bool Enabled, int DelayMinutes);
```

## 6. 探测与日志模型

```csharp
public sealed record ConnectivityProbePolicy(
    IReadOnlyList<ProbeEndpoint> Endpoints,
    TimeSpan Timeout,
    int Attempts,
    int RequiredFailedAttempts,
    int RequiredSuccessfulEndpoints,
    bool OptionalPingDiagnostics);

public sealed record ConnectivityResult(
    string AdapterId,
    DateTimeOffset CheckedAt,
    bool Online,
    bool RoutePresent,
    IReadOnlyList<ProbeAttempt> Attempts,
    string ReasonCode);

public sealed record ExecutionRecord(
    Guid Id,
    Guid? RuleId,
    RuleSource Source,
    string TargetAdapterId,
    RuleAction RequestedAction,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string Outcome,
    string ReasonCode,
    int? WindowsErrorCode);
```

## 7. 配置文件

计划配置位置：`%APPDATA%\NetRelay\config.json`。

- 使用 `System.Text.Json` 序列化。
- 所有 ID 使用 GUID；网卡 ID 保存前规范化。
- 时间戳使用 RFC 3339；每日和每周规则以 Windows 本地时区解释。
- 配置保存前完整校验，写入时采用临时文件和原子替换。
- `schemaVersion` 变化时先备份旧配置，再逐版本迁移。
- 不存储账号、密码、校园网认证信息或外部服务密钥。

## 8. 错误模型

稳定应用错误码至少包括：

- `ADAPTER_NOT_FOUND`
- `ADAPTER_OPERATION_FAILED`
- `PROBE_TIMEOUT`
- `PROBE_ROUTE_UNAVAILABLE`
- `BACKUP_NETWORK_UNAVAILABLE`
- `RULE_CONDITION_NOT_MET`
- `RULE_COOLDOWN_ACTIVE`
- `TASK_SCHEDULER_SYNC_FAILED`
- `CONFIG_INVALID`
- `NOTIFICATION_FAILED`
