# 接口与数据模型

[上一篇：功能与 UI 规格](03-functional-and-ui-spec.md) | [返回索引](README.md) | [下一篇：网络检测与自动化](05-network-detection-and-automation.md)

> `NetworkAdapterService.GetAdapters` 与当前精简版 `NetworkAdapterInfo` 已实现；其余接口和完整字段仍是首版实现基线。

## 1. 应用服务接口

| 方法 | 输入 | 输出 | 状态 |
| --- | --- | --- | --- |
| `NetworkAdapterService.GetAdapters` | 无 | `IReadOnlyList<NetworkAdapterInfo>` | **已实现**：枚举基础网卡状态 |
| `SetAdapterEnabledAsync` | GUID、启用状态、来源 | `ActionResult` | 计划实现 |
| `ProbeAdapterAsync` | GUID、探测策略 | `ConnectivityResult` | 计划实现 |
| `ListRules` | 无 | `AutomationRule[]` | 计划实现 |
| `SaveRuleAsync` | 规则 | `AutomationRule` | 计划实现 |
| `DeleteRuleAsync` | 规则 ID | `ActionResult` | 计划实现 |
| `RunRuleNowAsync` | 规则 ID | `ExecutionRecord` | 计划实现 |
| `ListExecutionLogs` | 分页过滤条件 | 分页记录 | 计划实现 |
| `PauseAutomationAsync` | 暂停状态 | `ActionResult` | 计划实现 |

服务方法应返回结构化结果，不向 ViewModel 暴露底层 COM/P/Invoke 异常细节。ViewModel 使用 `INotifyPropertyChanged`、`ObservableCollection<T>` 和 `ICommand` 与 XAML 绑定。

## 2. CLI

计划由同一个原生 EXE 提供：

```text
NetRelay.exe --execute-rule <rule-id>
NetRelay.exe --notify-rule <rule-id> <notification-id>
NetRelay.exe --restore-adapter <adapter-guid>
```

- CLI 参数必须严格解析，并在配置中验证对应实体。
- 未知参数、缺失规则、失效 GUID 或禁用规则返回非零退出码并写日志。
- CLI 不接受任意探测 URL、Shell 命令或可执行路径。

## 3. 应用事件

| .NET 事件 | 载荷 | 用途 |
| --- | --- | --- |
| `AdapterStatusChanged` | `NetworkAdapterInfo` | 实时刷新网卡状态 |
| `ConnectivityChanged` | `ConnectivityResult` | 展示联网状态变化 |
| `RuleExecutionUpdated` | `ExecutionRecord` | 更新执行历史和通知 |
| `AutomationPausedChanged` | `bool` | 同步全局暂停状态 |

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

当前 `Id` 来源于 .NET `NetworkInterface.Id`。后续实现网卡启用/禁用前，必须验证它与 Windows 接口 GUID 的稳定映射。

## 5. 计划规则模型

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

