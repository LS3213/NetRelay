namespace NetRelay.Models;

public sealed record NativeConnectionInfo(
    Guid Id,
    string Name,
    string DeviceName,
    int Status);

