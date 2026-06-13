using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using DeviceId;
using DeviceId.Encoders;
using DeviceId.Formatters;

internal interface IDeviceFingerprintProvider
{
    Task<DeviceFingerprintEvidence> CollectAsync(CancellationToken cancellationToken);
}

internal interface IDeviceEvidenceSource
{
    IReadOnlyDictionary<string, IReadOnlyList<string>> Collect();
}

internal sealed record DeviceFingerprintEvidence(
    int Version,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Evidence);

internal sealed class CompositeDeviceFingerprintProvider(
    IEnumerable<IDeviceEvidenceSource> sources) : IDeviceFingerprintProvider
{
    private const int FingerprintVersion = 1;

    public Task<DeviceFingerprintEvidence> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var evidence = sources
            .SelectMany(source => source.Collect())
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        return Task.FromResult(new DeviceFingerprintEvidence(FingerprintVersion, evidence));
    }
}

internal sealed class DeviceIdHardwareEvidenceSource : IDeviceEvidenceSource
{
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Collect() =>
        new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["hardware.windowsDeviceId"] = Single(Build("hardware.windowsDeviceId", builder => builder.OnWindows(windows => windows.AddWindowsDeviceId()))),
            ["hardware.machineGuid"] = Single(Build("hardware.machineGuid", builder => builder.OnWindows(windows => windows.AddMachineGuid()))),
            ["hardware.systemUuid"] = Single(Build("hardware.systemUuid", builder => builder.OnWindows(windows => windows.AddSystemUuid()))),
            ["hardware.motherboard"] = Single(Build("hardware.motherboard", builder => builder.OnWindows(windows => windows.AddMotherboardSerialNumber()))),
            ["hardware.processor"] = Single(Build("hardware.processor", builder => builder.OnWindows(windows => windows.AddProcessorId()))),
            ["hardware.systemDrive"] = Single(Build("hardware.systemDrive", builder => builder.OnWindows(windows => windows.AddSystemDriveSerialNumber())))
        };

    private static string Build(string category, Action<DeviceIdBuilder> configure)
    {
        var builder = new DeviceIdBuilder();
        configure(builder);
        var localComponentHash = builder
            .UseFormatter(new HashDeviceIdFormatter(() => SHA256.Create(), new Base64UrlByteArrayEncoder()))
            .ToString();

        return EvidenceHasher.Hash(category, localComponentHash);
    }

    private static IReadOnlyList<string> Single(string value) => [value];
}

internal sealed class NetRelayNetworkEvidenceSource : IDeviceEvidenceSource
{
    private static readonly string[] VirtualAdapterKeywords =
    [
        "virtual", "vmware", "hyper-v", "vethernet", "virtualbox", "vpn", "tap-",
        "tunnel", "loopback", "pseudo-interface", "teredo", "isatap", "wsl",
        "docker", "mihomo", "clash", "zerotier", "tailscale"
    ];

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Collect()
    {
        var physicalCandidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(IsPhysicalCandidate)
            .ToArray();

        return new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["network.physicalCandidate.adapterGuid"] = HashDistinct(
                "network.physicalCandidate.adapterGuid",
                physicalCandidates.Select(adapter => adapter.Id)),
            ["network.physicalCandidate.mac"] = HashDistinct(
                "network.physicalCandidate.mac",
                physicalCandidates.Select(adapter => adapter.GetPhysicalAddress().ToString())
                    .Where(value => value.Length >= 12)),
            ["network.physicalCandidate.model"] = HashDistinct(
                "network.physicalCandidate.model",
                physicalCandidates.Select(adapter =>
                    $"{adapter.NetworkInterfaceType}|{Normalize(adapter.Description)}"))
        };
    }

    private static bool IsPhysicalCandidate(NetworkInterface adapter)
    {
        var identity = $"{adapter.Name} {adapter.Description}";
        return adapter.NetworkInterfaceType is not (
                NetworkInterfaceType.Loopback
                or NetworkInterfaceType.Tunnel
                or NetworkInterfaceType.Ppp)
            && !VirtualAdapterKeywords.Any(keyword =>
                identity.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> HashDistinct(string category, IEnumerable<string> values) =>
        values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(Normalize)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Select(value => EvidenceHasher.Hash(category, value))
            .ToArray();

    private static string Normalize(string value) =>
        string.Join(' ', value.Trim().ToUpperInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

internal static class EvidenceHasher
{
    private const string ApplicationDomain = "netrelay:fingerprint:v1";

    public static string Hash(string category, string localValue)
    {
        var applicationScopedBytes = Encoding.UTF8.GetBytes($"{ApplicationDomain}:{category}:{localValue}");
        return Convert.ToHexString(SHA256.HashData(applicationScopedBytes));
    }
}
