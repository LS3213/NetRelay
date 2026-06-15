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
├── website/                      # 零依赖静态产品官网
│   ├── assets/                   # 官网图标等静态资源
│   ├── downloads/                # 可选的本地发布包目录
│   ├── index.html                # 官网页面结构与中文产品文案
│   ├── styles.css                # 响应式视觉与产品界面示意图
│   ├── script.js                 # 入场动画与下载地址配置
│   └── README.md                 # 官网预览和部署说明
├── global.json
├── NetRelay.sln
└── README.md
```

界面使用 WPF/XAML；业务状态使用 MVVM；Windows API 调用限制在 `Native` 和 `Services` 层。
`website/` 是独立的产品展示站，不会被桌面程序加载，也不改变桌面程序不使用 WebView、HTML、CSS 或 JavaScript 的约束。

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
dotnet run --project tests/NetRelay.RegressionTests/NetRelay.RegressionTests.csproj -c Release
powershell -ExecutionPolicy Bypass -File deploy\windows\build-preview.ps1
powershell -ExecutionPolicy Bypass -File deploy\windows\build-delivery.ps1
powershell -ExecutionPolicy Bypass -File deploy\baota\build-package.ps1
```

在沙箱无法读取用户级 NuGet 配置时，可在首次还原后使用：

```powershell
dotnet build NetRelay.sln --no-restore
```

当前项目不使用第三方 NuGet 包，不需要 Node.js、Rust、Cargo 或 WebView2。
根目录 `NuGet.Config` 清空远程包源，使当前无第三方依赖的解决方案可以离线还原和构建。

`NetRelay.RegressionTests` 是不依赖第三方测试框架的回归测试可执行项目，不启用或禁用真实网卡。当前覆盖探测策略边界、恢复冷却语义、定时重启去重、禁用网卡清单保留、只读诊断报告和单实例唤醒信号。回归测试和 VMnet1 验收会向 `RuleEngine` 注入各自的临时日志目录，不得写入 `%LOCALAPPDATA%\NetRelay\logs` 正式执行历史。

专用测试机上可使用管理员权限运行固定 VMnet1 受控切换验收。该入口只接受名称和设备描述均匹配 `VMware Network Adapter VMnet1` 的网卡，并在 `finally` 中尝试恢复：

```powershell
tests\NetRelay.RegressionTests\bin\Release\net8.0-windows\win-x64\NetRelay.RegressionTests.exe --acceptance-toggle-vmnet1
```

该验收会测试原生禁用/启用、主界面网卡合并列表、`RuleEngine` 调度来源与恢复来源，以及一次性规则的真实 Timer 触发；同时会写入正常的结构化执行日志。

## 4. 构建与发布

第八阶段开始后，客户端开发预览统一使用 `deploy/windows/build-preview.ps1`，输出到 `artifacts/publish/win-x64/`；客户端正式交付统一使用 `deploy/windows/build-delivery.ps1`，输出到 `artifacts/delivery/win-x64/`，并生成管理后台应上传的 `win-x64.zip` 与 Inno Setup 安装器 `NetRelaySetup.exe`。后端统一使用 `deploy/baota/build-package.ps1` 生成 `artifacts/baota-portable.zip`。四类产物不得互相替代，完整操作标准见 [构建产物与更新流程规范](19-build-artifacts-and-update-workflow.md)。`bin/` 与 `obj/` 仅为编译中间产物。

版本号维护纪律：

- 开发阶段不得主动修改 `src/NetRelay.Contracts/Protocol.cs` 中的 `ProductVersion` 或其他对外展示、上报、更新判断使用的产品版本号。
- 只有在维护者明确说明当前进入发布阶段后，才允许评估是否需要修改版本号。
- 即使已经进入发布阶段，修改版本号前也必须再次取得维护者明确同意，不得把功能开发、修复、打包或验收自动等同于版本号递增授权。

每次运行标准构建脚本时会先清理对应输出目录，再从当前工作区重新生成。目录中的 `BUILD-INFO.txt` 记录 Commit、工作区 Dirty 状态、构建时间、EXE/DLL SHA256 和签名状态，作为本地产物追溯依据。

- Debug 输出：`src/NetRelay/bin/Debug/net8.0-windows/win-x64/NetRelay.exe`
- 客户端开发预览输出：`artifacts/publish/win-x64/NetRelay.exe`
- 客户端正式交付输入目录：`artifacts/delivery/win-x64/publish/`
- 发布模式为 framework-dependent，目标电脑需要 .NET 8 Desktop Runtime。
- 当前完整发布目录包含 `NetRelay.exe`、`NetRelay.Updater.exe`、相关 DLL、`.deps.json`、`.runtimeconfig.json` 与 `BUILD-INFO.txt`；程序和安装器尚未签名。
- 使用 `app.manifest` 声明管理员权限与 Windows 10/11 兼容性；Per-Monitor V2 DPI 通过 `ApplicationHighDpiMode` 项目属性配置。
- Inno Setup 安装器脚本位于 `deploy/windows/NetRelay.iss`；正式发布前仍应增加代码签名并完成干净 Windows 10/11 虚拟机验收。
- 安装器使用仓库内简体中文语言包，始终显示安装路径页，并让用户选择开机自启动与桌面快捷方式；管理员权限因网卡控制能力强制启用，不能作为可关闭选项。

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
| `%LOCALAPPDATA%\NetRelay\logs\app-YYYY-MM-DD.log` | 计划中的应用诊断日志，当前未实现 |
| `%LOCALAPPDATA%\NetRelay\diagnostics\adapter-diagnostic-*.json` | 主动执行只读网卡诊断时生成 |

日志不得包含认证凭据、完整 HTTP 响应正文或用户可执行命令。

执行日志保留期限来自 `config.json` 的 `keepDays`。程序启动时会按该值执行清理；用户保存设置后会立即再次清理，而不是固定保留 30 天。非法保留期限不会直接用于删除，而会安全回退到默认 30 天。设置页的防抖与冷却值仅用于新建规则，已有规则继续使用各自保存的参数。

只读实机诊断命令：

```powershell
NetRelay.exe --diagnose-adapters
NetRelay.exe --diagnose-adapters .\adapter-diagnostic.json --quiet
```

诊断报告包含网卡 GUID、名称、设备描述、状态、原生 COM 实际观察数量、缓存返回数量和枚举异常，可能属于设备识别信息；报告仅保存在本机，分享前应由用户检查。

## 7. 部署与维护

- 首版为单机安装，无服务器部署。
- 第八阶段安装程序已实现 .NET 8 Desktop Runtime 检测与微软官方下载引导；客户端首次运行时按实际登录用户注册通知身份和 `netrelay://` 协议。
- 卸载时询问是否删除配置和日志，并清理 NetRelay 创建的任务计划项、通知身份和协议注册；真实干净虚拟机卸载验收仍待执行。
- 配置损坏时将原文件重命名为带时间戳的 `config.json.corrupted-*`，随后创建不含规则的默认配置。当前没有“最近有效配置备份”恢复机制。
- 开机自启状态不仅检查任务名称，还校验任务动作仍指向当前 EXE 且参数为 `--startup`；程序移动后旧任务会显示为未启用，用户重新勾选即可重建。

## 8. 产品官网维护与部署

产品官网位于 `website/`，为零依赖静态站点。页面视觉参考简洁的软件产品落地页结构，并延续桌面程序的浅色蓝紫渐变与磨砂玻璃风格。首屏产品展示图当前由 HTML 与 CSS 构建，无需额外截图资源。

本地预览：

```powershell
python -m http.server 4173 --directory website
```

随后访问 `http://localhost:4173`。也可以直接打开 `website/index.html`，但 HTTP 预览更接近实际部署行为。

下载按钮由 `website/script.js` 顶部的 `downloadUrl` 常量统一配置：

```js
const downloadUrl = "https://example.com/NetRelay.exe";
```

- 留空时，下载按钮只显示“下载地址尚未配置”的提示，不会请求不存在的发布包。
- 正式发布时应填写 GitHub Release、对象存储或其他稳定下载地址。
- `website/downloads/` 默认忽略实际发布包，避免将大型二进制文件提交到 Git；仅保留 `.gitkeep`。

静态部署不需要构建命令：

| 平台 | 配置 |
| --- | --- |
| Cloudflare Pages / Netlify | 构建命令留空，输出目录设为 `website` |
| GitHub Pages | 发布 `website/` 内容或使用 Pages 工作流 |
| Nginx / 静态服务器 | 将 `website/` 中的全部文件复制至站点根目录 |

官网变更的最低验证要求：

- 桌面端与移动端均不得产生横向滚动。
- 浏览器控制台不得出现 JavaScript 错误或静态资源加载错误。
- 导航锚点、FAQ、下载按钮和响应式布局必须可用。
- 产品能力文案必须与当前实现一致；尚未实现的功能不得描述为已提供。
- 若替换首屏 CSS 产品示意图，应使用经过脱敏的正式截图，不得包含网卡 GUID、真实 IP、MAC 地址或其他设备识别信息。

## 9. 文档维护规则

实现开始后，每次影响下列内容的变更必须同步文档：

- 应用服务、CLI、事件或数据模型。
- 网卡控制、断网判定、保护或恢复语义。
- 任务计划命名、权限或安装行为。
- 配置字段、迁移、日志和故障排查流程。
- 官网产品文案、下载地址配置、部署方式或目录结构。
