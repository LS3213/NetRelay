# NetRelay B0 DeviceId 隔离原型

此目录只用于 B0 设备指纹风险验证，不属于正式客户端或服务器。

选择 DeviceId 6.11.0 的原因：

- MIT 许可、持续维护、公开采用量明显高于 HardwareIds.NET。
- 提供原生 .NET 8 目标。
- Windows 与 WMI 能力按包拆分，可只引入需要的本机标识。
- 不提供局域网邻居、附近 Wi-Fi 或其他设备扫描功能。
- 支持自定义哈希格式和版本化构建器。
- 通过 `IDeviceFingerprintProvider` 隔离第三方库，并对证据执行 NetRelay 应用域隔离哈希。

核心硬件层选择 Windows Device ID、Machine GUID、System UUID、主板、CPU 和系统盘证据。

NetRelay 网卡辅助层复用程序已有的物理候选分类逻辑，只加入独立哈希后的：

- 可控制接口 GUID。
- 物理候选网卡 MAC。
- 物理候选网卡硬件描述与接口类型。

网卡辅助层只用于增加匹配可信度和识别换网卡，不得单独决定设备封锁。明确禁止加入用户名、机器名、Windows Product ID、IP 地址、网速、流量、链路状态、联网状态和任何网络扫描结果。

```powershell
dotnet run --project prototypes/NetRelay.B0.DeviceIdPrototype/NetRelay.B0.DeviceIdPrototype.csproj -c Release
```

双击 EXE 或不带参数运行时，会在 EXE 同目录的 `reports/` 下生成 JSON 报告。报告不包含原始硬件值，但为了比较前后变化会包含稳定哈希，因此不得公开分享或上传到公开仓库。

生成带标签报告：

```powershell
NetRelay.B0.DeviceIdTestTool.exe --label "更换网卡前"
```

比较两个报告：

```powershell
NetRelay.B0.DeviceIdTestTool.exe --compare before.json after.json
```

也可以在资源管理器中选中两份 JSON 报告，并将它们一起拖到 EXE 上完成比较。

比较结果只输出各分类匹配、增加和移除数量，不复制完整证据哈希。正式接入前仍需执行稳定性、虚拟机克隆、换硬盘、重装系统和网络抓包测试。
