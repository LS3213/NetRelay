using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NetRelay.Native;

[Flags]
public enum NlmConnectivity
{
    Unknown = 0,
    Disconnected = 1,
    IPv4NoTraffic = 2,
    IPv6NoTraffic = 4,
    IPv4Subnet = 0x10,
    IPv4LocalNetwork = 0x20,
    IPv4Internet = 0x40,
    IPv6Subnet = 0x100,
    IPv6LocalNetwork = 0x200,
    IPv6Internet = 0x400
}

[ComImport]
[Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface INetworkListManager
{
    [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
    [return: MarshalAs(UnmanagedType.Interface)]
    object GetNetworks([In] int Flags);

    [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
    [return: MarshalAs(UnmanagedType.Interface)]
    object GetNetwork([In] Guid gdNetworkId);

    [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
    [return: MarshalAs(UnmanagedType.Interface)]
    object GetNetworkConnections();

    [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
    [return: MarshalAs(UnmanagedType.Interface)]
    object GetNetworkConnection([In] Guid gdNetworkConnectionId);

    bool IsConnectedToInternet
    {
        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        get;
    }

    bool IsConnected
    {
        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        get;
    }
}

[ComImport]
[Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumNetworkConnections
{
    [PreserveSig]
    [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
    int get__NewEnum([MarshalAs(UnmanagedType.Interface)] out object ppEnumVar);

    [PreserveSig]
    [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
    int Next(
        uint count,
        [MarshalAs(UnmanagedType.Interface)] out INetworkConnection connection,
        out uint fetched);

    [PreserveSig]
    [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
    int Skip([In] uint celt);

    [PreserveSig]
    [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
    int Reset();

    [PreserveSig]
    [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
    int Clone([Out] out IEnumNetworkConnections ppenum);
}

[ComImport]
[Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface INetworkConnection
{
    [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
    [return: MarshalAs(UnmanagedType.Interface)]
    object GetNetwork();

    bool IsConnectedToInternet
    {
        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        get;
    }

    bool IsConnected
    {
        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        get;
    }

    [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
    int GetConnectivity();

    [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
    Guid GetConnectionId();

    [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
    Guid GetAdapterId();
}
