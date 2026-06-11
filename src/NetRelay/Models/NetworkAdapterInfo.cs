using System.Net.NetworkInformation;

namespace NetRelay.Models;

public sealed class NetworkAdapterInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required NetworkInterfaceType InterfaceType { get; init; }
    public required OperationalStatus OperationalStatus { get; init; }
    public required long Speed { get; init; }
    public required string MacAddress { get; init; }
    public required IReadOnlyList<string> IpAddresses { get; init; }

    public bool IsConnected => OperationalStatus == OperationalStatus.Up;
    public bool IsEnabled => OperationalStatus is not OperationalStatus.NotPresent;
    public string TypeLabel => InterfaceType switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => "有线网络",
        NetworkInterfaceType.Wireless80211 => "无线网络",
        NetworkInterfaceType.Ppp => "VPN / PPP",
        NetworkInterfaceType.Loopback => "环回接口",
        NetworkInterfaceType.Tunnel => "隧道接口",
        _ => "其他网卡"
    };
    public string StatusLabel => IsConnected ? "已连接" : IsEnabled ? "未连接" : "已禁用";
    public string PrimaryIpAddress => IpAddresses.FirstOrDefault() ?? "未分配";
    public string SpeedLabel => Speed <= 0 ? "未知" : $"{Speed / 1_000_000d:0.#} Mbps";
}

