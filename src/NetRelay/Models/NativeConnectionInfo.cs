namespace NetRelay.Models;

public sealed record NativeConnectionInfo(
    Guid Id,
    string Name,
    string DeviceName,
    NativeConnectionStatus Status)
{
    public bool IsEnabled => Status is not (
        NativeConnectionStatus.HardwareNotPresent
        or NativeConnectionStatus.HardwareDisabled);
}

public enum NativeConnectionStatus
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Disconnecting = 3,
    HardwareNotPresent = 4,
    HardwareDisabled = 5,
    HardwareMalfunction = 6,
    MediaDisconnected = 7,
    Authenticating = 8,
    AuthenticationSucceeded = 9,
    AuthenticationFailed = 10,
    InvalidAddress = 11,
    CredentialsRequired = 12,
    ActionRequired = 13,
    ActionRequiredRetry = 14,
    ConnectivityLost = 15
}
