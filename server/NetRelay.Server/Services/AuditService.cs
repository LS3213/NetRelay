using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRelay.Server.Data;

namespace NetRelay.Server.Services;

public sealed class AuditService(NetRelayDbContext dbContext)
{
    private static readonly SemaphoreSlim WriteGate = new(1, 1);

    public async Task WriteAsync(
        string action,
        string result,
        string requestId,
        Guid? adminAccountId = null,
        string? targetType = null,
        string? targetId = null,
        object? details = null,
        CancellationToken cancellationToken = default)
    {
        await WriteGate.WaitAsync(cancellationToken);
        try
        {
            var occurredAt = DateTimeOffset.UtcNow;
            var previousHash = await dbContext.AuditLogs
                .OrderByDescending(item => item.Id)
                .Select(item => item.EntryHash)
                .FirstOrDefaultAsync(cancellationToken);
            var detailsJson = details is null ? null : JsonSerializer.Serialize(details);
            var canonical = string.Join(
                "|",
                previousHash ?? string.Empty,
                occurredAt.ToString("O"),
                adminAccountId?.ToString() ?? string.Empty,
                action,
                result,
                requestId,
                targetType ?? string.Empty,
                targetId ?? string.Empty,
                detailsJson ?? string.Empty);
            var entryHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

            dbContext.AuditLogs.Add(new AuditLog
            {
                Action = action,
                Result = result,
                RequestId = requestId,
                AdminAccountId = adminAccountId,
                TargetType = targetType,
                TargetId = targetId,
                DetailsJson = detailsJson,
                PreviousHash = previousHash,
                EntryHash = entryHash,
                OccurredAt = occurredAt
            });
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            WriteGate.Release();
        }
    }
}
