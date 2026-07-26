# 架构与数据流

[上一篇：当前状态与产品定义](01-current-state-and-product.md) | [返回索引](README.md) | [下一篇：功能与 UI 规格](03-functional-and-ui-spec.md)

> 项目已完成第四阶段的主要界面与自动化功能。本文明确区分当前实现与后续设计；NLM/NCSI、独立通知服务和应用诊断日志尚未实现。

## 1. 技术栈

| 层 | 技术 | 职责 |
| --- | --- | --- |
| UI | WPF + XAML | 原生窗口、页面、状态展示和液态玻璃 UI |
| 应用层 | C# + MVVM | 用户意图、状态管理、规则执行和服务编排 |
| Windows 集成 | .NET + P/Invoke/COM | 网卡、DWM 与托盘气泡通知 |
| 持久化 | JSON + JSONL | 配置、规则和执行历史 |
| 调度 | 纯内联调度器 (System.Threading.Timer) | 定时规则与网络变化监听 |

## 2. 进程与模块边界

当前由单一的主可执行程序提供图形界面与内联后台服务：

```text
NetRelay.exe
├── BackgroundRuntime：托盘、规则调度、更新检查、Toast 协议回调
└── MainWindow：按需创建的可见主界面，共用后台运行时服务实例
```

程序使用当前用户会话范围内的命名 Mutex 保证单实例运行。第二个实例不会启动新的调度器，而是通知现有实例显示主窗口后退出。

当前模块：

| 模块 | 主要职责 | 不应承担 |
| --- | --- | --- |
| UI / ViewModel | 展示状态、收集用户意图、调用应用服务 | 直接执行 PowerShell 或 Windows API |
| Adapter Service | **已实现**枚举、识别和通过 GUID 启用/禁用网卡 | 判断完整自动化规则 |
| Connectivity Service | 汇总链路、IP 与绑定源 IP 的 HTTP 探测 | 直接切换网卡 |
| Rule Engine | 评估触发、条件、保护和冷却策略 | 绕过安全校验 |
| Scheduler Service | 运行 Timer 扫描时间规则并在网络变化时触发检测 | 执行网络探测 |
| TrayIconService / RichToastService | 发送托盘气泡、交互式 Toast 与托盘菜单请求 | 在未验证参数时执行高权限操作 |
| Config Repository | 原子读写配置、迁移版本 | 保存密钥或任意命令 |
| Execution Logger | 记录按日结构化执行结果 | 存储敏感请求内容 |

`BackgroundRuntime`、`MainWindow`、`MainViewModel` 与 `RuleSchedulerService` 共享同一个 `RuleEngine` 实例。主窗口只按需创建并接收后台运行时服务实例，不再创建第二套托盘、调度器或通知订阅。规则引擎按规则 ID 和目标网卡 GUID 串行执行，避免手动立即执行、定时触发和网络变化触发并发操作同一网卡。

调度器的完整 5 秒扫描周期和异步联网状态评估分别使用防重入门禁。上一轮尚未完成时跳过新一轮，避免普通字典状态竞争、重复通知和重复调度。

编辑已有规则时保留规则 ID，但会清空该规则的“已执行”、引擎冷却、提前通知、临时延迟和网络变化防抖运行时状态，并重新建立网络状态基线，使修改后的触发条件立即生效且不继承旧网络边沿。仅切换规则启用状态时不会清空这些状态，避免重复执行。

## 3. 总体架构

```mermaid
flowchart LR
    UI[WPF XAML UI] <-->|MVVM binding/commands| CORE[C# Application Core]
    CORE --> ADAPTER[Adapter Service]
    CORE --> PROBE[Connectivity Service]
    CORE --> RULES[Rule Engine]
    CORE --> CONFIG[(config.json)]
    CORE --> LOGS[(JSONL logs)]
    RULES --> ADAPTER
    RULES --> PROBE
    RULES --> NOTIFY[Notification Service]
    CORE --> SCHED[Rule Scheduler Service]
    SCHED --> RULES
    ADAPTER --> WINNET[Windows Networking APIs]
    PROBE --> WINNET
    PROBE --> HTTP[Configured probe endpoints]
```

## 4. 权限模型

- 应用清单声明 `requireAdministrator`，所有入口均以管理员权限运行。
- 高权限用于启用/禁用网卡。
- UI 传入的网卡 GUID、规则 ID 必须在本地配置中重新查找和校验。
- 不接受用户提供的可执行路径、Shell 命令或任意参数。

该模型实现简单，但每次启动会触发 UAC，并使 UI 也处于高权限进程中。长期改进方向见[安全、测试与风险](07-security-testing-and-risks.md)。

## 5. 数据流

### 手动禁用

```mermaid
sequenceDiagram
    actor User
    participant UI
    participant Core
    participant Adapter
    participant Log
    participant Notify
    User->>UI: 点击禁用
    UI->>User: 显示确认与风险
    User->>UI: 确认
    UI->>Core: set_adapter_enabled(guid, false)
    Core->>Adapter: 重新枚举并校验 GUID
    Adapter->>Adapter: 调用 Windows API
    Adapter-->>Core: 结果 / Windows 错误码
    Core->>Log: 写入执行记录
    Core->>Notify: 发送结果通知
    Core-->>UI: 返回结构化结果
```

### 自动切换

```mermaid
sequenceDiagram
    participant Trigger as 定时任务/网络监听
    participant Rule as Rule Engine
    participant Probe as Connectivity Service
    participant Adapter as Adapter Service
    participant Notify as Notification Service
    Trigger->>Rule: 执行规则
    Rule->>Probe: 检查目标网卡及条件
    Probe-->>Rule: 多信号探测结果
    Rule->>Probe: 验证备用网卡互联网可用
    alt 条件满足且备用可用
        Rule->>Adapter: 禁用目标网卡
        Rule->>Notify: 发送成功/恢复信息
    else 条件不满足或备用不可用
        Rule->>Notify: 跳过并说明原因
    end
```

## 6. 配置与文件

| 路径 | 内容 |
| --- | --- |
| `%APPDATA%\NetRelay\config.json` | 设置、网卡引用、规则、探测策略和模式版本 |
| `%LOCALAPPDATA%\NetRelay\logs\execution-YYYY-MM-DD.jsonl` | 结构化执行历史 |
| `%LOCALAPPDATA%\NetRelay\logs\app-YYYY-MM-DD.log` | 计划中的应用诊断日志，当前未实现 |
| `%LOCALAPPDATA%\NetRelay\diagnostics\adapter-diagnostic-*.json` | 用户主动执行 `--diagnose-adapters` 时生成的只读网卡清单 |

配置采用临时文件写入、刷新后原子替换，避免崩溃导致半写文件。执行日志按天写入；自动滚动清理尚未实现。

日志页刷新会在后台线程读取并解析最近 7 天的 JSONL 文件，并使用主界面已有网卡快照一次性解析网卡名称。完成后以单次集合替换更新 UI，避免按日志逐条查询 Windows 网卡 COM 接口和逐条重绘造成界面卡顿；刷新期间会暂时禁用刷新与清空按钮，防止并发操作。

## 7. 依赖方向与错误边界

- ViewModel 只能依赖应用服务接口，不依赖 Windows API 细节。
- Rule Engine 依赖抽象的 Adapter 与 Connectivity 接口，便于测试替身。
- Windows 错误必须转换为稳定的应用错误码，同时保留原始错误码供诊断。
- 探测端点失败、配置损坏或任务同步失败不能导致应用崩溃；应进入可恢复错误状态并通知用户。
- `NativeNetworkConnectionService` 缓存最近成功枚举及成功切换后的预期原生状态。原生 COM 枚举短暂漏项时，最多保留 12 次查询，避免禁用网卡从 UI 立即消失；持续缺失后自动移除陈旧项。部分驱动在成功禁用后仍短暂报告 `Disconnected`，此时优先保留 NetRelay 已确认的预期禁用状态，直到原生状态收敛或达到查询上限。UI 刷新合并按 GUID 语义比较，不受带花括号或无花括号的字符串格式影响。
