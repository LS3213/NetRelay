using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetRelay.Models;

namespace NetRelay.Services;

public sealed class NetworkAdapterService
{
    private static readonly string[] VirtualAdapterKeywords =
    [
        "virtual", "vmware", "hyper-v", "vethernet", "virtualbox", "vpn", "tap-",
        "tunnel", "loopback", "pseudo-interface", "teredo", "isatap", "wsl",
        "docker", "mihomo", "clash", "zerotier", "tailscale"
    ];

    public IReadOnlyList<NetworkAdapterInfo> GetAdapters()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Select(MapAdapter)
            .OrderByDescending(adapter => adapter.IsConnected)
            .ThenBy(adapter => adapter.IsLikelyVirtual)
            .ThenBy(adapter => adapter.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public void UpdateTraffic(IEnumerable<NetworkAdapterInfo> adapters)
    {
        var adaptersById = adapters.ToDictionary(adapter => adapter.Id, StringComparer.OrdinalIgnoreCase);
        var sampledAt = DateTimeOffset.Now;

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!adaptersById.TryGetValue(networkInterface.Id, out var adapter))
            {
                continue;
            }

            try
            {
                var statistics = networkInterface.GetIPv4Statistics();
                adapter.UpdateTraffic(statistics.BytesReceived, statistics.BytesSent, sampledAt);
            }
            catch (NetworkInformationException)
            {
                // Some tunnel and transient interfaces do not expose counters.
            }
        }
    }

    private static NetworkAdapterInfo MapAdapter(NetworkInterface adapter)
    {
        IReadOnlyList<string> addresses;
        try
        {
            addresses = adapter.GetIPProperties().UnicastAddresses
                .Where(address => address.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .Select(address => address.Address.ToString())
                .ToArray();
        }
        catch (NetworkInformationException)
        {
            addresses = Array.Empty<string>();
        }

        return new NetworkAdapterInfo
        {
            Id = adapter.Id,
            Name = adapter.Name,
            Description = adapter.Description,
            InterfaceType = adapter.NetworkInterfaceType,
            OperationalStatus = adapter.OperationalStatus,
            Speed = adapter.Speed,
            MacAddress = FormatMacAddress(adapter.GetPhysicalAddress()),
            IpAddresses = addresses,
            IsLikelyVirtual = IsLikelyVirtual(adapter),
            ClassificationLabel = GetClassificationLabel(adapter)
        };
    }

    private static bool IsLikelyVirtual(NetworkInterface adapter)
    {
        if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback
            or NetworkInterfaceType.Tunnel
            or NetworkInterfaceType.Ppp)
        {
            return true;
        }

        var identity = $"{adapter.Name} {adapter.Description}";
        return VirtualAdapterKeywords.Any(keyword => identity.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetClassificationLabel(NetworkInterface adapter)
    {
        if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
        {
            return "系统环回";
        }

        if (adapter.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)
        {
            return "隧道 / VPN";
        }

        return IsLikelyVirtual(adapter) ? "疑似虚拟" : "物理候选";
    }

    private static string FormatMacAddress(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 0 ? "未提供" : string.Join("-", bytes.Select(value => value.ToString("X2")));
    }
}
