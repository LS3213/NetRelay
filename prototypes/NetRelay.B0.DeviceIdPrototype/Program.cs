using System.Diagnostics;

IDeviceFingerprintProvider provider = new CompositeDeviceFingerprintProvider(
[
    new DeviceIdHardwareEvidenceSource(),
    new NetRelayNetworkEvidenceSource()
]);
var stopwatch = Stopwatch.StartNew();
var fingerprint = await provider.CollectAsync(CancellationToken.None);

foreach (var item in fingerprint.Evidence)
{
    if (item.Value.Count == 0)
    {
        Console.WriteLine($"{item.Key}: no evidence");
        continue;
    }

    foreach (var hash in item.Value)
    {
        if (hash.Length != 64)
        {
            throw new InvalidOperationException($"{item.Key} produced an invalid evidence hash.");
        }
    }

    Console.WriteLine($"{item.Key}: {item.Value.Count} hashed value(s)");
}

Console.WriteLine($"Fingerprint version: {fingerprint.Version}");
Console.WriteLine($"Evidence categories: {fingerprint.Evidence.Count}");
Console.WriteLine($"Elapsed milliseconds: {stopwatch.ElapsedMilliseconds}");
Console.WriteLine("DeviceId prototype passed without emitting raw identifiers.");
return 0;
