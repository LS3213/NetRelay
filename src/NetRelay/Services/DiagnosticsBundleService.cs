using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using NetRelay.Contracts;
using NetRelay.Models;

namespace NetRelay.Services;

public sealed class DiagnosticsBundleService
{
    public string LogsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetRelay",
        "logs");

    public string UpdatesDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetRelay",
        "updates");

    public async Task ExportBundleAsync(
        string zipPath,
        AppConfiguration config,
        bool automationEnabled,
        string? automationDisabledReason,
        UpdateStatusSnapshot updateStatus,
        bool mainWindowCreated,
        CancellationToken cancellationToken)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"NetRelay-Export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await WriteRuntimeSummaryAsync(tempDir, config, automationEnabled, automationDisabledReason, updateStatus, mainWindowCreated, cancellationToken);
            WriteDiagnosticSummary(tempDir, config, automationEnabled, automationDisabledReason, updateStatus, mainWindowCreated);
            CopyLogs(tempDir);
            WriteAdapterDiagnostics(tempDir);

            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            var zipDir = Path.GetDirectoryName(zipPath);
            if (!string.IsNullOrWhiteSpace(zipDir))
            {
                Directory.CreateDirectory(zipDir);
            }

            ZipFile.CreateFromDirectory(tempDir, zipPath);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    public void ClearUpdateCache()
    {
        if (!Directory.Exists(UpdatesDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(UpdatesDirectory, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(UpdatesDirectory, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch
            {
                // Ignore in-use or protected directories.
            }
        }
    }

    public void OpenLogsDirectory()
    {
        Directory.CreateDirectory(LogsDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = LogsDirectory,
            UseShellExecute = true
        });
    }

    public void OpenUpdateCacheDirectory()
    {
        Directory.CreateDirectory(UpdatesDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = UpdatesDirectory,
            UseShellExecute = true
        });
    }

    private static async Task WriteRuntimeSummaryAsync(
        string tempDir,
        AppConfiguration config,
        bool automationEnabled,
        string? automationDisabledReason,
        UpdateStatusSnapshot updateStatus,
        bool mainWindowCreated,
        CancellationToken cancellationToken)
    {
        var runtimeSummary = new
        {
            ProductVersion = Protocol.ProductVersion,
            OsVersion = Environment.OSVersion.ToString(),
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            Framework = RuntimeInformation.FrameworkDescription,
            PrimaryApiBaseUrl = config.PrimaryApiBaseUrl,
            GitHubFallback = new
            {
                config.GithubFallback.Enabled,
                config.GithubFallback.Repository,
                config.GithubFallback.ReleaseTagPrefix,
                config.GithubFallback.AssetName
            },
            UpdateSettings = new
            {
                config.AutoCheckUpdatesOnStartup,
                config.KeepDays
            },
            Automation = new
            {
                automationEnabled,
                automationDisabledReason,
                RuleCount = config.Rules.Count,
                config.ManualDisableProtection
            },
            Process = CaptureProcessSnapshot(mainWindowCreated),
            UpdateStatus = updateStatus
        };

        var runtimeSummaryPath = Path.Combine(tempDir, "runtime-summary.json");
        await File.WriteAllTextAsync(
            runtimeSummaryPath,
            JsonSerializer.Serialize(runtimeSummary, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
    }

    private void CopyLogs(string tempDir)
    {
        var logsDestDir = Path.Combine(tempDir, "logs");
        if (!Directory.Exists(LogsDirectory))
        {
            return;
        }

        Directory.CreateDirectory(logsDestDir);
        foreach (var file in Directory.GetFiles(LogsDirectory)
                     .Where(file => file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
                         || Path.GetFileName(file).StartsWith("updater-", StringComparison.OrdinalIgnoreCase)))
        {
            var destFile = Path.Combine(logsDestDir, Path.GetFileName(file));
            if (Path.GetFileName(file).StartsWith("updater-", StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(destFile, DiagnosticLogService.Sanitize(File.ReadAllText(file)) ?? string.Empty);
            }
            else
            {
                File.Copy(file, destFile, true);
            }
        }
    }

    private static void WriteAdapterDiagnostics(string tempDir)
    {
        var reportPath = Path.Combine(tempDir, "adapter-diagnostics.json");
        AdapterDiagnosticService.WriteReport(reportPath);
    }

    private void WriteDiagnosticSummary(
        string tempDir,
        AppConfiguration config,
        bool automationEnabled,
        string? automationDisabledReason,
        UpdateStatusSnapshot updateStatus,
        bool mainWindowCreated)
    {
        var updateCacheFiles = Directory.Exists(UpdatesDirectory)
            ? Directory.EnumerateFiles(UpdatesDirectory, "*", SearchOption.AllDirectories).Count()
            : 0;
        var updateCacheBytes = Directory.Exists(UpdatesDirectory)
            ? Directory.EnumerateFiles(UpdatesDirectory, "*", SearchOption.AllDirectories)
                .Select(path =>
                {
                    try
                    {
                        return new FileInfo(path).Length;
                    }
                    catch
                    {
                        return 0L;
                    }
                })
                .Sum()
            : 0L;

        var summary = new
        {
            GeneratedAt = DateTimeOffset.Now,
            ProductVersion = Protocol.ProductVersion,
            PrimarySource = config.PrimaryApiBaseUrl,
            GitHubRepository = config.GithubFallback.Repository,
            AutoCheckUpdatesOnStartup = config.AutoCheckUpdatesOnStartup,
            AutomationEnabled = automationEnabled,
            AutomationDisabledReason = automationDisabledReason,
            RuleCount = config.Rules.Count,
            Process = CaptureProcessSnapshot(mainWindowCreated),
            LastCheck = new
            {
                updateStatus.LastCheckedAt,
                updateStatus.LastCheckOutcome,
                updateStatus.LastCheckMessage,
                Source = updateStatus.LastCheckSource.ToString(),
                updateStatus.AvailableVersion
            },
            LastDownload = new
            {
                updateStatus.LastDownloadAt,
                updateStatus.LastDownloadOutcome,
                updateStatus.LastDownloadMessage,
                Source = updateStatus.LastDownloadSource.ToString(),
                updateStatus.LastDownloadedBytes
            },
            LogDirectory = LogsDirectory,
            UpdateCacheDirectory = UpdatesDirectory,
            UpdateCacheFiles = updateCacheFiles,
            UpdateCacheBytes = updateCacheBytes
        };

        File.WriteAllText(
            Path.Combine(tempDir, "diagnostic-summary.json"),
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static object CaptureProcessSnapshot(bool mainWindowCreated)
    {
        try
        {
            using var currentProcess = Process.GetCurrentProcess();
            var now = DateTimeOffset.Now;
            DateTimeOffset? startedAt = null;
            TimeSpan? uptime = null;
            try
            {
                startedAt = currentProcess.StartTime;
                uptime = now - startedAt.Value;
            }
            catch
            {
                // Some process properties can be unavailable under restricted permissions.
            }

            return new
            {
                ProcessId = currentProcess.Id,
                Is64BitProcess = Environment.Is64BitProcess,
                MainWindowCreated = mainWindowCreated,
                StartedAt = startedAt,
                UptimeSeconds = uptime.HasValue ? (long)uptime.Value.TotalSeconds : (long?)null,
                WorkingSetBytes = currentProcess.WorkingSet64,
                PrivateMemoryBytes = currentProcess.PrivateMemorySize64,
                ManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
                ThreadCount = currentProcess.Threads.Count,
                HandleCount = currentProcess.HandleCount
            };
        }
        catch
        {
            return new
            {
                MainWindowCreated = mainWindowCreated,
                SnapshotUnavailable = true
            };
        }
    }
}
