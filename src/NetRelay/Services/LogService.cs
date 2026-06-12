using System.IO;
using System.Text.Json;
using NetRelay.Models;

namespace NetRelay.Services;

public sealed class LogService
{
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
}
