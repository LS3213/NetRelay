# 开发与运维

[上一篇：网络检测与自动化](05-network-detection-and-automation.md) | [返回索引](README.md) | [下一篇：安全、测试与风险](07-security-testing-and-risks.md)

> 当前已有原生 WPF 源码、规则管理及后台调度服务、结构化日志与配置保存服务、构建配置和 Windows EXE 发布流程。

## 1. 当前目录结构

```text
NetRelay/
├── assets/                       # 图标源文件
├── docs/                         # 设计与维护文档
├── src/NetRelay/
│   ├── Assets/                   # WPF 嵌入资源
│   ├── Converters/               # XAML 值转换器
│   ├── Infrastructure/           # MVVM 基础类与命令
│   ├── Models/                   # 数据模型
│   ├── Native/                   # DWM/PInvoke Windows 集成
│   ├── Services/                 # 网卡及后续应用服务
│   ├── ViewModels/               # 页面状态和命令
│   ├── App.xaml
│   ├── MainWindow.xaml
│   ├── app.manifest
│   └── NetRelay.csproj
├── global.json
├── NetRelay.sln
└── README.md
```

界面使用 WPF/XAML；业务状态使用 MVVM；Windows API 调用限制在 `Native` 和 `Services` 层。

## 2. 开发环境

目标开发机需安装：

- Windows 10/11 x64。
- .NET 8 SDK。
- Git。
- Visual Studio 或其他支持 C#/XAML 的编辑器，可选。

当前机器已安装用户级 .NET SDK `8.0.422`。`global.json` 固定该 SDK 特征版本，并允许使用更新补丁。

## 3. 当前命令

```powershell
dotnet build NetRelay.sln
dotnet run --project src/NetRelay/NetRelay.csproj
dotnet publish src/NetRelay/NetRelay.csproj -c Release -r win-x64 --self-contained false
```

在沙箱无法读取用户级 NuGet 配置时，可在首次还原后使用：

```powershell
dotnet build NetRelay.sln --no-restore
```

当前项目不使用第三方 NuGet 包，不需要 Node.js、Rust、Cargo 或 WebView2。

## 4. 构建与发布

- Debug 输出：`src/NetRelay/bin/Debug/net8.0-windows/win-x64/NetRelay.exe`
- Release 发布输出：`src/NetRelay/bin/Release/net8.0-windows/win-x64/publish/NetRelay.exe`
- 发布模式为 framework-dependent，目标电脑需要 .NET 8 Desktop Runtime。
- 使用 `app.manifest` 声明管理员权限、Per-Monitor DPI 和 Windows 10/11 兼容性。
- 正式发布前应增加代码签名和安装包。

## 5. UI 与 Windows 集成

- `MainWindow.xaml` 定义原生 XAML 页面和控件样式。
- `WindowBackdrop` 调用 `DwmSetWindowAttribute`，在支持的 Windows 11 上启用 Mica 与原生圆角。
- Windows 10 或 DWM 属性不可用时，自动保留 WPF 渐变和半透明卡片作为降级。
- 程序不创建或依赖 WebView2 进程。

## 6. 配置、日志与诊断

当前已实现以下配置与日志路径：

| 路径 | 内容 |
| --- | --- |
| `%APPDATA%\NetRelay\config.json` | 设置、规则和探测策略 |
| `%LOCALAPPDATA%\NetRelay\logs\execution-YYYY-MM-DD.jsonl` | 结构化执行历史 |
| `%LOCALAPPDATA%\NetRelay\logs\app-YYYY-MM-DD.log` | 应用诊断日志 |

日志不得包含认证凭据、完整 HTTP 响应正文或用户可执行命令。

## 7. 部署与维护

- 首版为单机安装，无服务器部署。
- 后续安装程序负责检查 .NET 8 Desktop Runtime、注册通知身份和卸载信息。
- 卸载时应询问是否删除配置和日志，并清理 NetRelay 创建的任务计划项。
- 配置损坏时优先加载最近有效备份，并暂停自动化。

## 8. 文档维护规则

实现开始后，每次影响下列内容的变更必须同步文档：

- 应用服务、CLI、事件或数据模型。
- 网卡控制、断网判定、保护或恢复语义。
- 任务计划命名、权限或安装行为。
- 配置字段、迁移、日志和故障排查流程。
