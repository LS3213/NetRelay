# NetRelay

NetRelay 是面向 Windows 10/11 x64 的原生桌面网络适配器管理工具。

桌面客户端技术栈为 **C# / .NET 8 / WPF / XAML**，不使用 WebView。后端平台 B1 使用 ASP.NET Core 8、EF Core、Pomelo 和 MySQL 8.0。

## 当前实现

- 原生 WPF 桌面窗口与极客扁平毛玻璃（磨砂玉润玻璃胶囊）视觉体验。
- Windows 11 使用 DWM Mica 和原生圆角，Windows 10 自动降级。
- 扫描并展示本机网卡、链路状态、速度、流量波形、MAC 地址与 IP 地址。
- 多态自动化规则配置（一次性、每日定时、每周定时、网络连接变化触发器），具备保护性断网自动延迟与备用网络验证。
- 完整常驻后台运行支持：最小化至托盘无干扰静默运行，并支持气泡预警与顶部中央 Toast 状态提示。
- 安全拦截与倒计时交互，支持立即执行、延迟或取消单次自动化任务。
- 7天合并审计执行日志历史，可过滤清除并对异常损坏行做鲁棒性解析。

## 开发

```powershell
dotnet build NetRelay.sln
dotnet run --project src/NetRelay/NetRelay.csproj
```

发布原生 Windows EXE：

```powershell
powershell -ExecutionPolicy Bypass -File deploy\windows\build-preview.ps1
```

输出位置：

```text
artifacts/publish/win-x64/NetRelay.exe
```

完整设计与维护文档见 [docs/README.md](docs/README.md)。

后端基础开发：

```powershell
dotnet tool restore
dotnet run --project server/NetRelay.Server.Tests -c Release
dotnet run --project server/NetRelay.Server
```

后端需要通过环境变量提供 MySQL、正式 HTTPS 地址、GitHub 备用仓库和管理员初始化 Secret，详见 [server/NetRelay.Server/README.md](server/NetRelay.Server/README.md)。

## 官网

零依赖静态官网位于 [`website/`](website/)，可自行部署到 GitHub Pages、Cloudflare Pages、Netlify、Nginx 或任意静态文件服务器。

```powershell
python -m http.server 4173 --directory website
```

本地访问 `http://localhost:4173`。部署和下载地址配置说明见 [website/README.md](website/README.md)。
