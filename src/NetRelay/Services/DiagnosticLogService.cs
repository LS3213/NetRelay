using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetRelay.Models;

namespace NetRelay.Services;

public sealed partial class DiagnosticLogService
{
    private static readonly SemaphoreSlim WriteLock = new(1, 1);
    private readonly string _logDirectory;

    public DiagnosticLogService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetRelay",
            "logs"))
    {
    }

    public DiagnosticLogService(string logDirectory)
    {
        _logDirectory = logDirectory;
    }

    public Task InfoAsync(string component, string operation, string outcome, string? requestId = null, int? httpStatus = null, long? bytes = null, string? detail = null)
        => WriteAsync(new DiagnosticEvent(DateTimeOffset.UtcNow, "info", component, operation, outcome, SanitizeId(requestId), httpStatus, bytes, Detail: Sanitize(detail)));

    public Task ErrorAsync(string component, string operation, Exception exception, string outcome = "failed", string? requestId = null, int? httpStatus = null, long? bytes = null, string? detail = null)
        => WriteAsync(new DiagnosticEvent(
            DateTimeOffset.UtcNow,
            "error",
            component,
            operation,
            outcome,
            SanitizeId(requestId),
            httpStatus,
            bytes,
            exception.GetType().FullName,
            exception.HResult,
            Sanitize(detail ?? exception.Message)));

    private async Task WriteAsync(DiagnosticEvent item)
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);
            var path = Path.Combine(_logDirectory, $"diagnostic-{DateTime.Today:yyyy-MM-dd}.jsonl");
            var line = JsonSerializer.Serialize(item) + Environment.NewLine;
            await WriteLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await File.AppendAllTextAsync(path, line).ConfigureAwait(false);
            }
            finally
            {
                WriteLock.Release();
            }
        }
        catch
        {
            // Diagnostics must never interrupt the application.
        }
    }

    private static string? SanitizeId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length <= 64 ? value : value[..64];
    }

    public static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var sanitized = value.Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        sanitized = UrlQueryRegex().Replace(sanitized, "$1?<redacted>");
        sanitized = SecretRegex().Replace(sanitized, "$1=<redacted>");
        sanitized = LongIdentifierRegex().Replace(sanitized, "<redacted-id>");
        return sanitized.Length <= 500 ? sanitized : sanitized[..500];
    }

    [GeneratedRegex(@"(https?://[^\s?]+)\?[^\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlQueryRegex();

    [GeneratedRegex(@"(?i)\b(token|secret|password|authorization|cookie|signature|receipt|csrf)\s*[:=]\s*[^\s,;]+")]
    private static partial Regex SecretRegex();

    [GeneratedRegex(@"\b[A-Fa-f0-9]{48,}\b")]
    private static partial Regex LongIdentifierRegex();
}
