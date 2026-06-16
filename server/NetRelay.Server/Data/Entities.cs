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

public sealed class Device
{
    public Guid Id { get; set; }
    public required string MachineCode { get; set; }
    public required string DeviceIdHash { get; set; }
    public int FingerprintVersion { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

public sealed class DeviceInstallation
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public Guid InstallationId { get; set; }
    public required string ClientVersion { get; set; }
    public required string OsVersion { get; set; }
    public int ProtocolVersion { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }

    public Device? Device { get; set; }
}

public sealed class DeviceEvidence
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public required string Category { get; set; }
    public required string EvidenceHash { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }

    public Device? Device { get; set; }
}

public sealed class ActivationReceipt
{
    public Guid Id { get; set; }
    public Guid InstallationId { get; set; }
    public Guid ReceiptId { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public required string KeyId { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class Release
{
    public Guid Id { get; set; }
    public required string Version { get; set; }
    public required string Channel { get; set; }
    public required string Architecture { get; set; }
    public required string MinUpgradableVersion { get; set; }
    public long PackageSize { get; set; }
    public required string Sha256 { get; set; }
    public DateTimeOffset ReleaseDate { get; set; }
    public required string Changelog { get; set; }
    public bool IsMandatory { get; set; }
    public required string AssetPath { get; set; }
    public required string Status { get; set; } // "draft" | "published" | "revoked"
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class Feedback
{
    public Guid Id { get; set; }
    public required string Type { get; set; } // "bug" | "suggestion" | "other"
    public required string Title { get; set; }
    public required string Content { get; set; }
    public string? Contact { get; set; }
    public bool HasAttachment { get; set; }
    public string? AttachmentFilename { get; set; }
    public long? AttachmentSize { get; set; }
    public required string Status { get; set; } // "pending" | "resolved" | "ignored"
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StatusUpdatedAt { get; set; }
    public string? DeviceIdHash { get; set; }
    public Guid? InstallationId { get; set; }
    public string? ClientVersion { get; set; }
    public string? OsVersion { get; set; }
}

public sealed class Announcement
{
    public Guid Id { get; set; }
    public required string Title { get; set; }
    public required string Content { get; set; }
    public required string Severity { get; set; } // "normal" | "important" | "critical"
    public string? TargetVersionMin { get; set; }
    public string? TargetVersionMax { get; set; }
    public required string DisplayTrigger { get; set; } // "once_per_device" | "every_startup"
    public required string Status { get; set; } // "draft" | "published" | "revoked"
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class DeviceBlock
{
    public Guid Id { get; set; }
    public Guid? DeviceId { get; set; }
    public Guid? InstallationId { get; set; }
    public required string Reason { get; set; }
    public required string Status { get; set; } // "active" | "revoked"
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public Device? Device { get; set; }
    public DeviceInstallation? DeviceInstallation { get; set; }
}

public sealed class GlobalPolicy
{
    public Guid Id { get; set; }
    public required string Type { get; set; } // "global" | "version_range"
    public string? TargetVersionMin { get; set; }
    public string? TargetVersionMax { get; set; }
    public required string Reason { get; set; }
    public bool AllowUpdate { get; set; }
    public required string Status { get; set; } // "active" | "revoked"
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
