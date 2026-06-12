using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace NetRelay.Services;

public static class AdapterDiagnosticService
{
    public static string WriteReport(string? requestedPath = null)
    {
        var reportPath = string.IsNullOrWhiteSpace(requestedPath)
            ? CreateDefaultReportPath()
            : Path.GetFullPath(requestedPath);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);

        var nativeService = new NativeNetworkConnectionService();
        var nativeConnections = nativeService.GetConnections()
            .Select(connection => new
            {
                connection.Id,
                connection.Name,
                connection.DeviceName,
                Status = (int)connection.Status,
                StatusName = connection.Status.ToString(),
                connection.IsEnabled
            })
            .ToArray();
        var dotNetAdapters = NetworkInterface.GetAllNetworkInterfaces()
            .Select(adapter => new
            {
                adapter.Id,
                adapter.Name,
                adapter.Description,
                InterfaceType = adapter.NetworkInterfaceType.ToString(),
                OperationalStatus = adapter.OperationalStatus.ToString(),
                adapter.Speed
            })
            .ToArray();

        var report = new
        {
            GeneratedAt = DateTimeOffset.Now,
            Environment.OSVersion,
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Framework = RuntimeInformation.FrameworkDescription,
            NativeConnectionCount = nativeConnections.Length,
            nativeService.LastObservedConnectionCount,
            nativeService.LastReturnedConnectionCount,
            nativeService.LastEnumerationError,
            DotNetAdapterCount = dotNetAdapters.Length,
            NativeConnections = nativeConnections,
            DotNetAdapters = dotNetAdapters
        };

        File.WriteAllText(
            reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return reportPath;
    }

    private static string CreateDefaultReportPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(
            localAppData,
            "NetRelay",
            "diagnostics",
            $"adapter-diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}.json");
    }
}
