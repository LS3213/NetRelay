using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetRelay.Models;

namespace NetRelay.Services;

public sealed class NetworkAdapterService
{
    public IReadOnlyList<NetworkAdapterInfo> GetAdapters()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Select(MapAdapter)
            .OrderByDescending(adapter => adapter.IsConnected)
            .ThenBy(adapter => adapter.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
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
            IpAddresses = addresses
        };
    }

    private static string FormatMacAddress(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 0 ? "未提供" : string.Join("-", bytes.Select(value => value.ToString("X2")));
    }
}

