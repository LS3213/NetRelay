# NetRelay 文档索引

## 当前状态

截至 2026-06-12，项目已进入第一阶段开发，包含原生 WPF 桌面工程、XAML 液态玻璃概览界面、.NET 网卡扫描服务和构建配置。本文档集同时承担：

- **现状审计**：准确记录当前不存在的实现。
- **实施设计**：定义后续开发 NetRelay 时应遵循的产品、架构和接口基线。

文档中的状态标记：

| 标记 | 含义 |
| --- | --- |
| 已实现 | 可由当前代码验证的行为 |
| 计划实现 | 已确定的后续实现设计 |
| 未确认 | 必须通过后续编码或 Windows 真机测试确认 |

## 推荐阅读顺序

1. [当前状态与产品定义](01-current-state-and-product.md)
2. [架构与数据流](02-architecture-and-data-flow.md)
3. [功能与 UI 规格](03-functional-and-ui-spec.md)
4. [接口与数据模型](04-interfaces-and-data-model.md)
5. [网络检测与自动化](05-network-detection-and-automation.md)
6. [开发与运维](06-development-and-operations.md)
7. [安全、测试与风险](07-security-testing-and-risks.md)

## 目录结构

```text
NetRelay/
├── assets/                   # 图标源文件
├── docs/                     # 设计与维护文档
├── src/NetRelay/             # C# / WPF 原生桌面程序
├── NetRelay.sln
├── global.json
└── README.md
```

## 关键结论

- 当前技术栈为 C#、.NET 8、WPF、XAML 和 Windows DWM API，无 WebView。
- 目标平台为 Windows 10/11 x64。
- 程序计划始终以管理员权限运行，不安装 Windows 服务。
- 时间规则和提前通知由 Windows 任务计划程序保证；网络变化规则依赖托盘进程。
- 断网判定使用网卡状态、Windows NLM/NCSI 和双端点 HTTP/HTTPS 探测；`ping` 仅用于诊断。
- 自动禁用故障网卡前，必须确认备用网卡已经连接并可访问互联网。
- 首版使用 JSON 配置与滚动日志，不使用数据库。

## 源码引用说明

第一阶段已实现代码位于 `src/NetRelay/MainWindow.xaml`、`ViewModels/MainViewModel.cs`、`Services/NetworkAdapterService.cs` 与 `Native/WindowBackdrop.cs`。其余自动化设计仍为计划实现，落地时需继续更新文档。
