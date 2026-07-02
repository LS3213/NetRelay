# 客户端托盘常驻内存分析

[返回索引](README.md)

本文记录 2026-07-02 对 NetRelay 客户端隐藏到系统托盘后内存占用偏高的代码级分析、一次优化前实际运行采样，以及第一阶段优化实施情况。

## 1. 优化前实测样本

测试时客户端已隐藏到系统托盘运行，主窗口标题为空，进程仍处于响应状态。

| 指标 | 当前样本 |
| --- | --- |
| 进程 ID | `17596` |
| 启动时间 | `2026-07-02 21:39:34` |
| 工作集 | 约 `324.9 MB` |
| 私有内存 | 约 `226.4 MB` |
| 句柄数 | 约 `1200` |
| 线程数 | 约 `55` |
| 主窗口标题 | 空 |

本次采样中，`%LOCALAPPDATA%\NetRelay\logs` 下执行日志体量很小，仅发现两个 `execution-*.jsonl` 文件，大小分别约 `344 B` 和 `1637 B`。因此本次实际高内存不应优先归因于执行历史日志过大。

受当前权限限制，`Win32_Process` 命令行读取、进程模块枚举和线程 CPU 排序均未能完整读取。后续若需要进一步拆分托管堆、原生模块、WPF 资源和工作集构成，应在与 NetRelay 同等权限的管理员终端中使用诊断工具重新采样。

## 2. 从代码看出的主要原因

### 2.1 托盘启动仍创建完整主窗口

`App.OnStartup` 在通过隐私、激活和单实例检查后，无论是否为 `--startup` 启动，都会创建 `MainWindow`：

```csharp
var mainWindow = new MainWindow(configService);
MainWindow = mainWindow;
```

`--startup` 只是不调用 `mainWindow.Show()`，并不阻止 `MainWindow` 构造函数执行。也就是说“开机自启隐藏到托盘”当前只是窗口未显示，不是真正的轻量后台模式。

### 2.2 MainWindow 构造会加载完整 WPF UI 树

`MainWindow` 构造函数会立即执行：

- `InitializeComponent()`：加载完整主窗口 XAML、样式、控件树、动画、资源、图标和绑定。
- `RichToastService.Initialize()`：初始化 Toast 相关注册。
- 创建 `ConnectivityService`、`NativeNetworkConnectionService`、`RuleEngine`、`RuleSchedulerService`、`SettingsRuntimeService`。
- 创建 `MainViewModel` 并设置为 `DataContext`。
- 初始化 WinForms `NotifyIcon`。

当前 `MainWindow.xaml` 体量较大，包含网络概览、规则管理、执行历史、关于页、弹窗通知区域、样式资源和托盘菜单。即使窗口不可见，WPF 控件树和资源仍会常驻内存。

### 2.3 MainViewModel 构造会立即执行 UI 数据加载

`MainViewModel` 构造期间会立即做这些操作：

- `RefreshAdapters()`：枚举系统网卡、COM 网络连接、IP、状态、MAC、虚拟网卡分类等。
- 启动 1 秒 `DispatcherTimer`，用于 UI 流量采样。
- 创建倒计时 `DispatcherTimer`。
- 创建更新服务、日志服务、诊断服务和大量 UI 命令。
- 加载自动化规则到 `ObservableCollection<AutomationRuleViewModel>`。
- 异步 `LoadLogsAsync()`，读取执行历史并包装成 `ExecutionRecordViewModel`。
- 执行日志保留清理。

这些行为对打开主窗口时合理，但对纯托盘后台待机偏重。

### 2.4 隐藏托盘后仍维持 UI 级网卡采样和探测

`MainViewModel` 的 1 秒定时器会持续执行 `SampleTraffic()`：

- 每秒调用 `NetworkAdapterService.UpdateTraffic()`。
- 每秒对 `Adapters` 做排序。
- 每 5 秒触发 `TriggerAllAdaptersProbe()`。
- `TriggerAllAdaptersProbe()` 会对当前 UI 列表里的所有网卡执行联网探测，并把结果写回 UI 绑定对象。

这意味着窗口隐藏到托盘后，主界面级别的实时网络状态监控仍然在运行。该设计会维持更多对象、网络探测任务、Dispatcher 回调和 UI 绑定更新，增加内存与运行时分配压力。

### 2.5 日志加载是潜在放大项

`LogService.LoadLogsAsync()` 默认读取最近 7 天 `execution-*.jsonl`，每个文件使用 `ReadAllLinesAsync()` 一次读完整文件，再反序列化为 `ExecutionRecord`，最后由 ViewModel 包装成 `ExecutionRecordViewModel` 并放入 `ObservableCollection`。

本次实测日志很小，所以不是当前样本主因。但如果用户长期高频执行规则，执行历史会放大托盘后台内存占用，尤其是在主窗口从未显示时也加载日志列表这一点不合理。

### 2.6 WPF 与自包含运行时基线较高

客户端使用 WPF + WinForms NotifyIcon + .NET 8 自包含单文件发布。WPF 应用的工作集基线本身高于轻量 Win32/控制台后台进程；任务管理器显示的工作集还包含可被系统回收的页面，并不完全等同于泄漏。

但托盘常驻达到 300MB 级工作集、200MB 级私有内存，对该产品定位仍然偏高，主要问题是后台模式没有和完整 UI 模式解耦。

## 3. 已实施的第一阶段优化

2026-07-02 已完成第一阶段代码调整：

- 新增 `BackgroundRuntime`，由应用启动路径先创建轻量后台运行时。
- 新增 `TrayIconService`，将系统托盘图标和托盘菜单从 `MainWindow` 中拆出。
- `--startup` 启动不再创建 `MainWindow`，只启动配置、规则调度、托盘、Toast 协议注册和静默更新检查。
- 托盘“打开主窗口”和普通双击启动时才懒加载 `MainWindow`。
- `MainWindow` 可接收后台运行时中已经创建的 `ConnectivityService`、`NativeNetworkConnectionService`、`RuleEngine`、`RuleSchedulerService` 和 `LogService`，避免打开 UI 后重复启动规则调度。
- `MainWindow` 在由 `BackgroundRuntime` 托管时不再创建第二个托盘图标，也不再重复订阅规则通知。
- `netrelay://` Toast 回调独立启动时只处理通知动作，不因为不是 `--startup` 就打开主窗口。
- `MainViewModel` 不再在构造阶段自动加载执行历史日志。
- 主窗口隐藏时暂停 UI 专用的 1 秒流量采样和全网卡 UI 探测；窗口恢复显示时再刷新网卡并恢复采样。
- 规则执行后只有执行历史已被用户加载过，才刷新日志列表。

已通过验证：

- `dotnet build NetRelay.sln -c Release --no-restore` 成功。
- `tests/NetRelay.RegressionTests` 45 项回归测试通过。

尚未完成的验证：

- 需要重新打包或运行新构建产物，在真实 `--startup` 托盘模式下采样优化后内存。
- 需要验证托盘打开主窗口、托盘检查更新、托盘导出诊断包、Toast 延迟/取消动作和关闭到托盘行为。

## 4. 后续优化优先级

### P0：实测第一阶段优化结果

第一阶段已完成真正的轻量托盘后台 Host。下一步应使用新构建产物在真实开机自启或手动 `--startup` 模式下重新采样：

| 指标 | 优化前 | 优化后目标 |
| --- | --- | --- |
| 工作集 | 约 `324.9 MB` | 优先降到 `< 150 MB` |
| 私有内存 | 约 `226.4 MB` | 优先降到 `< 120 MB` |
| 线程数 | 约 `55` | 明显低于完整 UI 常驻 |
| 句柄数 | 约 `1200` | 明显低于完整 UI 常驻 |

### P1：主窗口不可见时暂停 UI 专用采样

第一阶段已完成“窗口隐藏即暂停 UI 专用采样”。后续需要做真机行为验证，确认自动化规则不依赖主界面采样，且窗口恢复时状态能及时刷新。

### P1：执行历史延迟加载

第一阶段已取消启动时自动 `LoadLogsAsync()`。后续仍应增加最大条数，例如最近 300 到 500 条。

### P2：日志读取改为流式限量

`LogService.LoadLogsAsync()` 和 `RuleSchedulerService.InitializeLastRanFromLogs()` 应避免 `ReadAllLines*` 读取完整文件。可以按文件从尾部或逐行流式读取，并尽早停止。

### P2：后台状态采样加入诊断指标

后续可以在诊断日志中加入轻量进程指标：

- 工作集。
- 私有内存。
- 句柄数。
- 线程数。
- 主窗口是否已创建。
- 当前是否处于托盘后台模式。

这些指标需要脱敏，不能包含用户路径、命令行敏感参数或设备完整指纹。

## 5. 当前结论

优化前高内存更像架构性常驻开销，而不是单一泄漏。最核心的问题是：托盘模式创建并持有完整 WPF 主窗口、完整 ViewModel、UI 绑定集合、日志集合和 UI 级网络监控循环。

第一阶段已把“托盘后台 Host”和“主窗口 UI”拆开。下一步应对新构建产物重新采样优化后工作集和私有内存，并根据结果决定是否继续做日志限量、流式读取和主窗口长时间隐藏后释放。
