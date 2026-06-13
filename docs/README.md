# NetRelay 文档索引

## 当前状态

截至 2026-06-12，项目已完成第七阶段（富交互）的代码实现，包含原生 WPF 桌面工程、自动化规则配置、内联调度器、执行历史、系统托盘、交互式 Toast、多协议探测及 Windows NLM/NCSI 辅助状态。需要真实 Windows 通知点击、物理网卡、休眠和重新登录的行为仍待手动验收。本文档集同时承担：

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
10. [后端平台总体实施规范](10-backend-platform-master-plan.md)
11. [后端平台分阶段开发与验收计划](11-backend-staged-development-and-acceptance-plan.md)
12. [后端 API 与共享契约规范](12-backend-api-and-contracts.md)
13. [后端数据库与存储规范](13-backend-database-and-storage.md)
14. [后端安全、签名与隐私规范](14-backend-security-signing-and-privacy.md)
15. [后端部署与运维规范](15-backend-deployment-and-operations.md)
16. [B0 设备指纹与安全原型报告](16-b0-device-fingerprint-and-security-prototype-report.md)
17. [B0 设备指纹手动测试指南](17-b0-device-fingerprint-manual-test-guide.md)

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
- 首版使用 JSON 配置与按日 JSONL 执行日志，不使用数据库；日志按设置页的可配置保留天数自动清理。
- 产品官网位于 `website/`，使用原生 HTML、CSS 和少量 JavaScript，可独立部署到任意静态文件服务器，不影响桌面程序的原生技术栈。
- 后端、双源更新、设备激活、反馈、公告与封锁功能尚未实现；其后续开发必须遵循 [后端平台总体实施规范](10-backend-platform-master-plan.md)。
- 后端平台 B0-B6 的开发任务、当前状态、验收证据和文档维护要求统一记录在 [后端平台分阶段开发与验收计划](11-backend-staged-development-and-acceptance-plan.md)；未在该文档记录并通过验收的阶段不得视为完成。

## 源码引用说明

已实现代码包含：原生界面网卡管理、系统托盘、关闭拦截对话框、程序设置页面、配置原子持久化、绑定源 IP 的 HTTP 探测服务，以及纯内联规则引擎 ([RuleEngine.cs](../src/NetRelay/Services/RuleEngine.cs)) 与后台调度器 ([RuleSchedulerService.cs](../src/NetRelay/Services/RuleSchedulerService.cs))。所有自动化规则及延迟恢复均由程序内联常驻线程托管。
