using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetRelay.Models;

namespace NetRelay.Services;

public sealed class NetworkAdapterService
{
    private readonly NativeNetworkConnectionService _connectionService;

    private static readonly string[] VirtualAdapterKeywords =
    [
        "virtual", "vmware", "hyper-v", "vethernet", "virtualbox", "vpn", "tap-",
        "tunnel", "loopback", "pseudo-interface", "teredo", "isatap", "wsl",
        "docker", "mihomo", "clash", "zerotier", "tailscale"
    ];

    public NetworkAdapterService(NativeNetworkConnectionService connectionService)
    {
        _connectionService = connectionService;
    }

    public IReadOnlyList<NetworkAdapterInfo> GetAdapters()
    {
        var nativeConnections = _connectionService.GetConnections();
        var nativeById = nativeConnections.ToDictionary(connection => connection.Id);
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Select(adapter => MapAdapter(adapter, nativeById))
            .ToList();
        var discoveredIds = adapters
            .Select(adapter => Guid.TryParse(adapter.Id, out var id) ? id : Guid.Empty)
            .ToHashSet();

        adapters.AddRange(nativeConnections
            .Where(connection => !discoveredIds.Contains(connection.Id))
            .Select(MapDisabledAdapter));

        return adapters
            .OrderByDescending(adapter => adapter.IsConnected)
            .ThenByDescending(adapter => adapter.IsEnabled)
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

    private static NetworkAdapterInfo MapAdapter(
        NetworkInterface adapter,
        IReadOnlyDictionary<Guid, NativeConnectionInfo> nativeConnections)
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
            IsEnabled = adapter.OperationalStatus is not OperationalStatus.NotPresent,
            Speed = adapter.Speed,
            MacAddress = FormatMacAddress(adapter.GetPhysicalAddress()),
            IpAddresses = addresses,
            IsLikelyVirtual = IsLikelyVirtual(adapter),
            ClassificationLabel = GetClassificationLabel(adapter),
            CanToggle = Guid.TryParse(adapter.Id, out var adapterGuid) && nativeConnections.ContainsKey(adapterGuid)
        };
    }

    private static NetworkAdapterInfo MapDisabledAdapter(NativeConnectionInfo connection)
    {
        var identity = $"{connection.Name} {connection.DeviceName}";
        var likelyVirtual = IsLikelyVirtual(identity, NetworkInterfaceType.Unknown);
        return new NetworkAdapterInfo
        {
            Id = connection.Id.ToString("B"),
            Name = string.IsNullOrWhiteSpace(connection.Name) ? connection.DeviceName : connection.Name,
            Description = connection.DeviceName,
            InterfaceType = NetworkInterfaceType.Unknown,
            OperationalStatus = OperationalStatus.Down,
            IsEnabled = false,
            Speed = 0,
            MacAddress = "网卡已禁用",
            IpAddresses = Array.Empty<string>(),
            IsLikelyVirtual = likelyVirtual,
            ClassificationLabel = likelyVirtual ? "疑似虚拟" : "物理候选",
            CanToggle = true
        };
    }

    private static bool IsLikelyVirtual(NetworkInterface adapter)
    {
        return IsLikelyVirtual($"{adapter.Name} {adapter.Description}", adapter.NetworkInterfaceType);
    }

    private static bool IsLikelyVirtual(string identity, NetworkInterfaceType interfaceType)
    {
        return interfaceType is NetworkInterfaceType.Loopback
            or NetworkInterfaceType.Tunnel
            or NetworkInterfaceType.Ppp
            || VirtualAdapterKeywords.Any(keyword => identity.Contains(keyword, StringComparison.OrdinalIgnoreCase));
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
