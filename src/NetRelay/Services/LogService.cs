using System.IO;
using System.Text.Json;
using NetRelay.Models;

namespace NetRelay.Services;

public sealed class LogService
{
    public const int DefaultKeepDays = 30;

    private readonly string _logDirectory;

    public LogService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _logDirectory = Path.Combine(appData, "NetRelay", "logs");
    }

    // Constructor for custom directory (useful for testing)
    public LogService(string customDirectory)
    {
        _logDirectory = customDirectory;
    }

    public async Task<List<ExecutionRecord>> LoadLogsAsync(int daysLimit = 7)
    {
        var records = new List<ExecutionRecord>();

        try
        {
            if (!Directory.Exists(_logDirectory))
            {
                return records;
            }

            var logFiles = Directory.GetFiles(_logDirectory, "execution-*.jsonl");
            if (logFiles.Length == 0)
            {
                return records;
            }

            // Parse dates from filenames to sort them descending
            var filesWithDates = logFiles
                .Select(filepath =>
                {
                    var filename = Path.GetFileNameWithoutExtension(filepath);
                    var dateStr = filename.Replace("execution-", "");
                    if (DateTime.TryParse(dateStr, out var date))
                    {
                        return new { Path = filepath, Date = date };
                    }
                    return new { Path = filepath, Date = DateTime.MinValue };
                })
                .Where(x => x.Date != DateTime.MinValue)
                .OrderByDescending(x => x.Date)
                .Take(daysLimit)
                .ToList();

            foreach (var fileInfo in filesWithDates)
            {
                try
                {
                    if (!File.Exists(fileInfo.Path)) continue;

                    var lines = await File.ReadAllLinesAsync(fileInfo.Path);
                    foreach (var line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        try
                        {
                            var record = JsonSerializer.Deserialize<ExecutionRecord>(line);
                            if (record != null)
                            {
                                records.Add(record);
                            }
                        }
                        catch
                        {
                            // Skip corrupted log line to keep the logs viewer robust
                        }
                    }
                }
                catch
                {
                    // Skip inaccessible files
                }
            }
        }
        catch
        {
            // Fail-safe default
        }

        // Return ordered by started time descending
        return records.OrderByDescending(r => r.StartedAt).ToList();
    }

    public Task ClearAllLogsAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                if (Directory.Exists(_logDirectory))
                {
                    var files = Directory.GetFiles(_logDirectory, "execution-*.jsonl");
                    foreach (var file in files)
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch
                        {
                            // Continue on lock files or permission issues
                        }
                    }
                }
            }
            catch
            {
                // Fail silently
            }
        });
    }

    public Task RotateLogsAsync(int keepDays = DefaultKeepDays)
    {
        var validatedKeepDays = keepDays is >= 1 and <= 90 ? keepDays : DefaultKeepDays;
        return Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(_logDirectory))
                {
                    return;
                }

                var logFiles = Directory.GetFiles(_logDirectory, "execution-*.jsonl");
                var cutoffDate = DateTime.Today.AddDays(-validatedKeepDays);

                foreach (var file in logFiles)
                {
                    try
                    {
                        var filename = Path.GetFileNameWithoutExtension(file);
                        var dateStr = filename.Replace("execution-", "");
                        if (DateTime.TryParse(dateStr, out var date))
                        {
                            if (date < cutoffDate)
                            {
                                File.Delete(file);
                            }
                        }
                    }
                    catch
                    {
                        // Ignore individual file deletion errors
                    }
                }
            }
            catch
            {
                // Ignore general exceptions
            }
        });
    }

    public Task CreateDiagnosticZipAsync(string zipPath)
    {
        return Task.Run(() =>
        {
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            var dir = Path.GetDirectoryName(zipPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create);
            if (Directory.Exists(_logDirectory))
            {
                var files = Directory.GetFiles(_logDirectory, "execution-*.jsonl");
                foreach (var file in files)
                {
                    try
                    {
                        var entryName = Path.GetFileName(file);
                        using var fileStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        var entry = zip.CreateEntry(entryName);
                        using var entryStream = entry.Open();
                        fileStream.CopyTo(entryStream);
                    }
                    catch
                    {
                        // Skip if it fails to read or archive
                    }
                }
            }
        });
    }
}
