using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRelay.Server.Data;

namespace NetRelay.Server.Services;

public sealed record AuditChainVerificationResult(bool Valid, long? InvalidRecordId, int VerifiedRecords);

public static class AuditHash
{
    public static DateTimeOffset NormalizeTimestamp(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % 10), TimeSpan.Zero);

    public static string Compute(
        string? previousHash,
        DateTimeOffset occurredAt,
        Guid? adminAccountId,
        string action,
        string result,
        string requestId,
        string? targetType,
        string? targetId,
        string? detailsJson)
    {
        var canonical = string.Join(
            "|",
            previousHash ?? string.Empty,
            NormalizeTimestamp(occurredAt).ToString("O"),
            adminAccountId?.ToString() ?? string.Empty,
            action,
            result,
            requestId,
            targetType ?? string.Empty,
            targetId ?? string.Empty,
            detailsJson ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

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
            var occurredAt = AuditHash.NormalizeTimestamp(DateTimeOffset.UtcNow);
            var previousHash = await dbContext.AuditLogs
                .OrderByDescending(item => item.Id)
                .Select(item => item.EntryHash)
                .FirstOrDefaultAsync(cancellationToken);
            var detailsJson = details is null ? null : JsonSerializer.Serialize(details);
            var entryHash = AuditHash.Compute(
                previousHash,
                occurredAt,
                adminAccountId,
                action,
                result,
                requestId,
                targetType,
                targetId,
                detailsJson);

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

    public async Task<AuditChainVerificationResult> VerifyChainAsync(CancellationToken cancellationToken = default)
    {
        var records = await dbContext.AuditLogs
            .AsNoTracking()
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken);
        string? previousHash = null;
        var verified = 0;
        foreach (var record in records)
        {
            var expected = AuditHash.Compute(
                previousHash,
                record.OccurredAt,
                record.AdminAccountId,
                record.Action,
                record.Result,
                record.RequestId,
                record.TargetType,
                record.TargetId,
                record.DetailsJson);
            if (!string.Equals(record.PreviousHash, previousHash, StringComparison.Ordinal) ||
                !string.Equals(record.EntryHash, expected, StringComparison.Ordinal))
            {
                return new AuditChainVerificationResult(false, record.Id, verified);
            }

            previousHash = record.EntryHash;
            verified++;
        }

        return new AuditChainVerificationResult(true, null, verified);
    }
}
