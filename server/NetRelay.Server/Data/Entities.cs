namespace NetRelay.Server.Data;

public sealed class AdminAccount
{
    public Guid Id { get; set; }
    public byte SingletonKey { get; set; } = 1;
    public required string Username { get; set; }
    public required string PasswordHash { get; set; }
    public required string ProtectedTotpSecret { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AdminSession
{
    public Guid Id { get; set; }
    public Guid AdminAccountId { get; set; }
    public required string SessionTokenHash { get; set; }
    public required string CsrfTokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset ReauthenticatedUntil { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public AdminAccount? AdminAccount { get; set; }
}

public sealed class AdminLoginChallengeRecord
{
    public Guid Id { get; set; }
    public Guid AdminAccountId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}

public sealed class AuditLog
{
    public long Id { get; set; }
    public Guid? AdminAccountId { get; set; }
    public required string Action { get; set; }
    public required string Result { get; set; }
    public required string RequestId { get; set; }
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string? DetailsJson { get; set; }
    public string? PreviousHash { get; set; }
    public required string EntryHash { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
