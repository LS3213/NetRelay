# NetRelay

NetRelay 是面向 Windows 10/11 x64 的原生桌面网络适配器管理工具。

当前技术栈为 **C# / .NET 8 / WPF / XAML**，不使用 WebView、HTML、CSS、JavaScript、Node.js 或 Rust。

## 当前实现

- 原生 WPF 桌面窗口与 XAML 液态玻璃视觉。
- Windows 11 使用 DWM Mica 和原生圆角，Windows 10 自动降级。
- 扫描并展示本机网卡、链路状态、速度、MAC 地址与 IP 地址。
- 选择目标网卡并查看详情。
- 管理员权限启动清单，为后续启用/禁用网卡做准备。

网卡启用/禁用、断网探测、通知和自动化规则仍在开发中。

## 开发

```powershell
dotnet build NetRelay.sln
dotnet run --project src/NetRelay/NetRelay.csproj
```

发布原生 Windows EXE：

```powershell
dotnet publish src/NetRelay/NetRelay.csproj -c Release -r win-x64 --self-contained false
```

输出位置：

```text
src/NetRelay/bin/Release/net8.0-windows/win-x64/publish/NetRelay.exe
```

完整设计与维护文档见 [docs/README.md](docs/README.md)。
