# NetRelay 文档索引

## 当前状态

截至 2026-06-12，项目已完成第六阶段（环境感知）开发，包含原生 WPF 桌面工程、XAML 液态玻璃概览界面、自动化规则配置、时间与网络变化内联调度器、操作执行历史日志、系统托盘/气泡通知、COM异常隔离、优雅取消（Graceful Shutdown）、日志自动清理（Log Rotation）、多协议探测（HTTP/HTTPS/PING/DNS）及 Windows 原生 NLM/NCSI COM 状态实时监测与 UI 联动。本文档集同时承担：

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
8. [验收状态与实机测试清单](08-acceptance-status.md)
9. [未来开发阶段与里程碑规划](09-future-stages-and-milestones.md)

## 目录结构

```text
NetRelay/
├── assets/                   # 图标源文件
├── docs/                     # 设计与维护文档
├── src/NetRelay/             # C# / WPF 原生桌面程序
├── website/                  # 零依赖静态产品官网
├── NetRelay.sln
├── global.json
└── README.md
```

## 关键结论

- 当前技术栈为 C#、.NET 8、WPF、XAML 和 Windows DWM API，无 WebView。
- 目标平台为 Windows 10/11 x64。
- 程序计划始终以管理员权限运行，不安装 Windows 服务。
- 时间规则、提前提醒与网络变化监听均由主程序内联的后台调度服务控制，仅在程序运行时执行。
- 当前断网判定采用多协议探测（支持 HTTP/HTTPS/PING/DNS 协议），配合 Windows 原生 NLM/NCSI COM 状态实时监测与 UI 联动。
- 自动禁用故障网卡前，必须确认备用网卡已经连接并可访问互联网。
- 首版使用 JSON 配置与按日 JSONL 执行日志，不使用数据库；已实现基于保留天数（30天）的后台日志自动滚动清理。
- 产品官网位于 `website/`，使用原生 HTML、CSS 和少量 JavaScript，可独立部署到任意静态文件服务器，不影响桌面程序的原生技术栈。

## 源码引用说明

已实现代码包含：原生界面网卡管理、系统托盘、关闭拦截对话框、程序设置页面、配置原子持久化、绑定源 IP 的 HTTP 探测服务，以及纯内联规则引擎 ([RuleEngine.cs](../src/NetRelay/Services/RuleEngine.cs)) 与后台调度器 ([RuleSchedulerService.cs](../src/NetRelay/Services/RuleSchedulerService.cs))。所有自动化规则及延迟恢复均由程序内联常驻线程托管。
