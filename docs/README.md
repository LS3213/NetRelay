# NetRelay 文档索引

> **当前云控文档入口（2026-07-30）**：桌面客户端的生产云控已经迁移到 Omnexa，当前身份、激活、缓存和运行组合语义以 [NetRelay 接入 Omnexa](21-omnexa-client-integration.md) 为准。08、10—17 中涉及旧 NetRelay Server 或 B0 原型的“安装实例”内容仅作为历史验收记录保留，不再定义当前产品行为。

## 当前状态

截至 2026-06-16，原生 WPF 客户端及后端平台 B0-B7 已完成既定开发与验收；B7 反馈强化与设备指纹库扩展已通过自动构建测试、管理后台与 WPF 客户端手工验收、真实 MySQL 迁移和生产包升级验收。B8 正在进行客户端交付和在线更新链路实机联调，更新包上传、存储、发布、撤回、版本不可变规则、旧客户端 HEAD 探测和故障排查规则已纳入维护规范。本文档集同时承担：

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
18. [宝塔便携部署与首次安装向导](18-baota-portable-deployment.md)
19. [构建产物与更新流程规范](19-build-artifacts-and-update-workflow.md)
20. [客户端托盘常驻内存分析](20-client-tray-memory-analysis.md)
21. [NetRelay 接入 Omnexa（当前生产云控规范）](21-omnexa-client-integration.md)

## 目录结构

```text
NetRelay/
├── deploy/                   # B1 Docker Compose、Nginx 与运维脚本
├── assets/                   # 图标源文件
├── docs/                     # 设计与维护文档
├── server/NetRelay.Server/   # ASP.NET Core 8 后端
├── server/NetRelay.Server.Tests/
├── src/NetRelay.Contracts/   # 客户端与后端共享协议
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
- 桌面客户端的设备激活、按程序目录隔离的内部运行槽位、签名云控、心跳、公告、更新、反馈和备用源容灾已迁移到 Omnexa；后台只管理设备，接入坐标、身份语义、信任链和验证要求见 [NetRelay 接入 Omnexa](21-omnexa-client-integration.md)。
- `server/NetRelay.Server` 保留为旧后端源码与迁移参考，不再是桌面客户端的生产云控入口。
- B7 已新增反馈环境信息、客户端反馈历史、管理端设备指纹库、筛选分页和附件下载加固；当前状态为“已通过”。
- 第八阶段交付打包已于 2026-06-15 正式启动，正在实现 Windows 自包含安装器、卸载清理和统一客户端交付构建链路。
- 在线更新维护必须区分草稿、已发布和已撤回状态；相同版本、通道和架构一旦创建即禁止覆盖，详细流程和故障判断见 [构建产物与更新流程规范](19-build-artifacts-and-update-workflow.md)。
- 客户端开发预览、在线更新包、Windows 安装器和后端宝塔便携包的唯一构建入口、标准产物和更新步骤见 [构建产物与更新流程规范](19-build-artifacts-and-update-workflow.md)。
- 后端平台 B0-B7 的开发任务、当前状态、验收证据和文档维护要求统一记录在 [后端平台分阶段开发与验收计划](11-backend-staged-development-and-acceptance-plan.md)；未在该文档记录并通过验收的阶段不得视为完成。

## 源码引用说明

已实现代码包含：原生界面网卡管理、系统托盘、关闭拦截对话框、程序设置页面、配置原子持久化、绑定源 IP 的 HTTP 探测服务，以及纯内联规则引擎 ([RuleEngine.cs](../src/NetRelay/Services/RuleEngine.cs)) 与后台调度器 ([RuleSchedulerService.cs](../src/NetRelay/Services/RuleSchedulerService.cs))。所有自动化规则及延迟恢复均由程序内联常驻线程托管。
