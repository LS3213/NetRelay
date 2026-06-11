using System.Runtime.InteropServices;
using NetRelay.Models;

namespace NetRelay.Services;

public sealed class NativeNetworkConnectionService
{
    private static readonly Guid ConnectionManagerClassId = new("BA126AD1-2166-11D1-B1D0-00805FC1270E");

    public IReadOnlySet<Guid> GetControllableConnectionIds()
    {
        return GetConnections().Select(connection => connection.Id).ToHashSet();
    }

    public IReadOnlyList<NativeConnectionInfo> GetConnections()
    {
        var connectionsResult = new List<NativeConnectionInfo>();
        INetConnectionManager? manager = null;
        IEnumNetConnection? connections = null;
        try
        {
            manager = CreateManager();
            Marshal.ThrowExceptionForHR(manager.EnumConnections(NetConManagerEnumFlags.Default, out connections));

            while (connections.Next(1, out var connection, out var fetched) == 0 && fetched == 1)
            {
                try
                {
                    if (TryGetConnectionInfo(connection, out var connectionInfo))
                    {
                        connectionsResult.Add(connectionInfo);
                    }
                }
                finally
                {
                    Marshal.FinalReleaseComObject(connection);
                }
            }
        }
        catch
        {
            return connectionsResult;
        }
        finally
        {
            if (connections is not null)
            {
                Marshal.FinalReleaseComObject(connections);
            }
            if (manager is not null)
            {
                Marshal.FinalReleaseComObject(manager);
            }
        }

        return connectionsResult;
    }

    public AdapterActionResult SetEnabled(string adapterId, string adapterName, bool enabled)
    {
        if (!Guid.TryParse(adapterId, out var adapterGuid))
        {
            return new AdapterActionResult(false, $"“{adapterName}”没有可用于原生控制的接口 GUID。");
        }

        INetConnectionManager? manager = null;
        IEnumNetConnection? connections = null;
        try
        {
            manager = CreateManager();
            Marshal.ThrowExceptionForHR(manager.EnumConnections(NetConManagerEnumFlags.Default, out connections));

            while (connections.Next(1, out var connection, out var fetched) == 0 && fetched == 1)
            {
                try
                {
                    if (!TryGetConnectionId(connection, out var connectionId) || connectionId != adapterGuid)
                    {
                        continue;
                    }

                    var result = enabled ? connection.Connect() : connection.Disconnect();
                    Marshal.ThrowExceptionForHR(result);
                    var action = enabled ? "启用" : "禁用";
                    return new AdapterActionResult(true, $"已请求{action}“{adapterName}”。");
                }
                finally
                {
                    Marshal.FinalReleaseComObject(connection);
                }
            }

            return new AdapterActionResult(false, $"Windows 网络连接管理器中未找到“{adapterName}”。");
        }
        catch (COMException exception)
        {
            return new AdapterActionResult(
                false,
                $"操作“{adapterName}”失败：{exception.Message}（0x{exception.ErrorCode:X8}）",
                exception.ErrorCode);
        }
        catch (Exception exception)
        {
            return new AdapterActionResult(false, $"操作“{adapterName}”失败：{exception.Message}");
        }
        finally
        {
            if (connections is not null)
            {
                Marshal.FinalReleaseComObject(connections);
            }
            if (manager is not null)
            {
                Marshal.FinalReleaseComObject(manager);
            }
        }
    }

    private static INetConnectionManager CreateManager()
    {
        var managerType = Type.GetTypeFromCLSID(ConnectionManagerClassId, throwOnError: true)
            ?? throw new COMException("无法创建 Windows 网络连接管理器。");
        return (INetConnectionManager)Activator.CreateInstance(managerType)!;
    }

    private static bool TryGetConnectionId(INetConnection connection, out Guid connectionId)
    {
        if (TryGetConnectionInfo(connection, out var connectionInfo))
        {
            connectionId = connectionInfo.Id;
            return true;
        }

        connectionId = Guid.Empty;
        return false;
    }

    private static bool TryGetConnectionInfo(INetConnection connection, out NativeConnectionInfo connectionInfo)
    {
        connectionInfo = new NativeConnectionInfo(Guid.Empty, string.Empty, string.Empty, 0);
        var result = connection.GetProperties(out var propertiesPointer);
        if (result != 0 || propertiesPointer == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var properties = Marshal.PtrToStructure<NetConProperties>(propertiesPointer);
            connectionInfo = new NativeConnectionInfo(
                properties.Id,
                Marshal.PtrToStringUni(properties.Name) ?? string.Empty,
                Marshal.PtrToStringUni(properties.DeviceName) ?? string.Empty,
                properties.Status);
            return true;
        }
        finally
        {
            NcFreeNetconProperties(propertiesPointer);
        }
    }

    [DllImport("netshell.dll")]
    private static extern void NcFreeNetconProperties(IntPtr properties);

    [ComImport]
    [Guid("C08956A2-1CD3-11D1-B1C5-00805FC1270E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INetConnectionManager
    {
        [PreserveSig]
        int EnumConnections(NetConManagerEnumFlags flags, out IEnumNetConnection connections);
    }

    [ComImport]
    [Guid("C08956A0-1CD3-11D1-B1C5-00805FC1270E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumNetConnection
    {
        [PreserveSig]
        int Next(
            uint count,
            [MarshalAs(UnmanagedType.Interface)] out INetConnection connection,
            out uint fetched);

        [PreserveSig]
        int Skip(uint count);

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int Clone(out IEnumNetConnection connections);
    }

    [ComImport]
    [Guid("C08956A1-1CD3-11D1-B1C5-00805FC1270E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INetConnection
    {
        [PreserveSig]
        int Connect();

        [PreserveSig]
        int Disconnect();

        [PreserveSig]
        int Delete();

        [PreserveSig]
        int Duplicate([MarshalAs(UnmanagedType.LPWStr)] string duplicateName, out INetConnection connection);

        [PreserveSig]
        int GetProperties(out IntPtr properties);

        [PreserveSig]
        int GetUiObjectClassId(out Guid classId);

        [PreserveSig]
        int Rename([MarshalAs(UnmanagedType.LPWStr)] string newName);
    }

    private enum NetConManagerEnumFlags
    {
        Default = 0
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NetConProperties
    {
        public Guid Id;
        public IntPtr Name;
        public IntPtr DeviceName;
        public int Status;
        public int MediaType;
        public uint Characteristics;
        public Guid ObjectClassId;
        public Guid UiObjectClassId;
    }
}
