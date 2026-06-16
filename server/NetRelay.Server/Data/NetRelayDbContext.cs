using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Text;

namespace NetRelay.Server.Data;

public sealed class NetRelayDbContext(DbContextOptions<NetRelayDbContext> options) : DbContext(options)
{
    public DbSet<AdminAccount> AdminAccounts => Set<AdminAccount>();
    public DbSet<AdminSession> AdminSessions => Set<AdminSession>();
    public DbSet<AdminLoginChallengeRecord> AdminLoginChallenges => Set<AdminLoginChallengeRecord>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceInstallation> DeviceInstallations => Set<DeviceInstallation>();
    public DbSet<DeviceEvidence> DeviceEvidences => Set<DeviceEvidence>();
    public DbSet<ActivationReceipt> ActivationReceipts => Set<ActivationReceipt>();
    public DbSet<Release> Releases => Set<Release>();
    public DbSet<Feedback> Feedbacks => Set<Feedback>();
    public DbSet<Announcement> Announcements => Set<Announcement>();
    public DbSet<DeviceBlock> DeviceBlocks => Set<DeviceBlock>();
    public DbSet<GlobalPolicy> GlobalPolicies => Set<GlobalPolicy>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var guidConverter = new ValueConverter<Guid, byte[]>(
            value => GuidToBytes(value),
            value => BytesToGuid(value));
        var nullableGuidConverter = new ValueConverter<Guid?, byte[]?>(
            value => NullableGuidToBytes(value),
            value => BytesToNullableGuid(value));

        modelBuilder.Entity<AdminAccount>(entity =>
        {
            entity.ToTable("admin_accounts");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasConversion(guidConverter).HasColumnType("binary(16)");
            entity.Property(item => item.Username).HasMaxLength(100);
            entity.Property(item => item.PasswordHash).HasMaxLength(500);
            entity.Property(item => item.ProtectedTotpSecret).HasMaxLength(1000);
            entity.HasIndex(item => item.Username).IsUnique();
            entity.HasIndex(item => item.SingletonKey).IsUnique();
            entity.ToTable(table => table.HasCheckConstraint("ck_admin_accounts_singleton", "`singleton_key` = 1"));
        });

        modelBuilder.Entity<AdminSession>(entity =>
        {
            entity.ToTable("admin_sessions");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasConversion(guidConverter).HasColumnType("binary(16)");
            entity.Property(item => item.AdminAccountId).HasConversion(guidConverter).HasColumnType("binary(16)");
            entity.Property(item => item.SessionTokenHash).HasMaxLength(64).IsFixedLength();
            entity.Property(item => item.CsrfTokenHash).HasMaxLength(64).IsFixedLength();
            entity.HasIndex(item => item.SessionTokenHash).IsUnique();
            entity.HasIndex(item => item.ExpiresAt);
            entity.HasOne(item => item.AdminAccount)
                .WithMany()
                .HasForeignKey(item => item.AdminAccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AdminLoginChallengeRecord>(entity =>
        {
            entity.ToTable("admin_login_challenges");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasConversion(guidConverter).HasColumnType("binary(16)");
            entity.Property(item => item.AdminAccountId).HasConversion(guidConverter).HasColumnType("binary(16)");
            entity.Property(item => item.ConsumedAt).IsConcurrencyToken();
            entity.HasIndex(item => item.ExpiresAt);
            entity.HasOne<AdminAccount>()
                .WithMany()
                .HasForeignKey(item => item.AdminAccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.ToTable("audit_logs");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.AdminAccountId).HasConversion(nullableGuidConverter).HasColumnType("binary(16)");
            entity.Property(item => item.Action).HasMaxLength(100);
            entity.Property(item => item.Result).HasMaxLength(50);
            entity.Property(item => item.RequestId).HasMaxLength(100);
            entity.Property(item => item.TargetType).HasMaxLength(100);
            entity.Property(item => item.TargetId).HasMaxLength(200);
            entity.Property(item => item.PreviousHash).HasMaxLength(64).IsFixedLength();
            entity.Property(item => item.EntryHash).HasMaxLength(64).IsFixedLength();
            entity.HasIndex(item => item.OccurredAt);
            entity.HasIndex(item => new { item.Action, item.OccurredAt });
        });

        modelBuilder.Entity<Device>(entity =>
        {
            entity.ToTable("devices");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasConversion(guidConverter).HasColumnType("binary(16)");
            entity.Property(item => item.MachineCode).HasMaxLength(100);
            entity.Property(item => item.DeviceIdHash).HasMaxLength(64);
            entity.HasIndex(item => item.MachineCode).IsUnique();
            entity.HasIndex(item => item.DeviceIdHash);
            entity.HasIndex(item => item.LastSeenAt);
        });

        modelBuilder.Entity<DeviceInstallation>(entity =>
        {
            entity.ToTable("device_installations");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasConversion(guidConverter).HasColumnType("binary(16)");
            entity.Property(item => item.DeviceId).HasConversion(guidConverter).HasColumnType("binary(16)");
            entity.Property(item => item.InstallationId).HasConversion(guidConverter).HasColumnType("binary(16)");
            entity.Property(item => item.ClientVersion).HasMaxLength(50);
            entity.Property(item => item.OsVersion).HasMaxLength(100);
            entity.HasIndex(item => item.InstallationId).IsUnique();
            entity.HasIndex(item => new { item.DeviceId, item.LastSeenAt });
            entity.HasOne(item => item.Device)
                .WithMany()
                .HasForeignKey(item => item.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DeviceEvidence>(entity =>
        {
            entity.ToTable("device_evidence");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasConversion(guidConverter).HasColumnType("binary(16)");
            entity.Property(item => item.DeviceId).HasConversion(guidConverter).HasColumnType("binary(16)");
            entity.Property(item => item.Category).HasMaxLength(100);
            entity.Property(item => item.EvidenceHash).HasMaxLength(64);
            entity.HasIndex(item => new { item.DeviceId, item.Category, item.EvidenceHash }).IsUnique();
            entity.HasOne(item => item.Device)
                .WithMany()
                .HasForeignKey(item => item.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ActivationReceipt>(entity =>
        {
            entity.ToTable("activation_receipts");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasConversion(guidConverter).HasColumnType("binary(16)");
            entity.Property(item => item.InstallationId).HasConversion(guidConverter).HasColumnType("binary(16)");
            entity.Property(item => item.ReceiptId).HasConversion(guidConverter).HasColumnType("binary(16)");
            entity.Property(item => item.KeyId).HasMaxLength(100);
            entity.HasIndex(item => item.ReceiptId).IsUnique();
            entity.HasIndex(item => item.ExpiresAt);
        });

        modelBuilder.Entity<Release>(entity =>
        {
            entity.ToTable("releases");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasConversion(guidConverter).HasColumnType("binary(16)");
            entity.Property(item => item.Version).HasMaxLength(50);
            entity.Property(item => item.Channel).HasMaxLength(50);
            entity.Property(item => item.Architecture).HasMaxLength(50);
            entity.Property(item => item.MinUpgradableVersion).HasMaxLength(50);
            entity.Property(item => item.Sha256).HasMaxLength(64).IsFixedLength();
            entity.Property(item => item.AssetPath).HasMaxLength(500);
            entity.Property(item => item.Status).HasMaxLength(50);
            entity.Property(item => item.Changelog).HasMaxLength(4000);
            entity.Property(item => item.PackageDeletedAt).HasColumnName("package_deleted_at");
            entity.HasIndex(item => new { item.Version, item.Channel, item.Architecture }).IsUnique();
            entity.HasIndex(item => item.Status);
        });

        modelBuilder.Entity<Feedback>(entity =>
        {
            entity.ToTable("feedbacks");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasConversion(guidConverter).HasColumnType("binary(16)");
            entity.Property(item => item.Type).HasMaxLength(50);
            entity.Property(item => item.Title).HasMaxLength(200);
            entity.Property(item => item.Content).HasMaxLength(4000);
            entity.Property(item => item.Contact).HasMaxLength(200);
            entity.Property(item => item.AttachmentFilename).HasMaxLength(200);
            entity.Property(item => item.Status).HasMaxLength(50);
            entity.Property(item => item.DeviceIdHash).HasMaxLength(64);
            entity.Property(item => item.InstallationId).HasConversion(nullableGuidConverter).HasColumnType("binary(16)");
            entity.Property(item => item.ClientVersion).HasMaxLength(50);
            entity.Property(item => item.OsVersion).HasMaxLength(100);
            entity.HasIndex(item => item.CreatedAt);
            entity.HasIndex(item => item.Status);
        });

        modelBuilder.Entity<Announcement>(entity =>
        {
            entity.ToTable("announcements");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasConversion(guidConverter).HasColumnType("binary(16)");
            entity.Property(item => item.Title).HasMaxLength(200);
            entity.Property(item => item.Content).HasMaxLength(4000);
            entity.Property(item => item.Severity).HasMaxLength(50);
            entity.Property(item => item.TargetVersionMin).HasMaxLength(50);
            entity.Property(item => item.TargetVersionMax).HasMaxLength(50);
            entity.Property(item => item.DisplayTrigger).HasMaxLength(50);
            entity.Property(item => item.Status).HasMaxLength(50);
            entity.HasIndex(item => item.Status);
            entity.HasIndex(item => item.PublishedAt);
            entity.HasIndex(item => item.ExpiresAt);
        });

        modelBuilder.Entity<DeviceBlock>(entity =>
        {
            entity.ToTable("device_blocks");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasConversion(guidConverter).HasColumnType("binary(16)");
            entity.Property(item => item.DeviceId).HasConversion(nullableGuidConverter).HasColumnType("binary(16)");
            entity.Property(item => item.InstallationId).HasConversion(nullableGuidConverter).HasColumnType("binary(16)");
            entity.Property(item => item.Reason).HasMaxLength(1000);
            entity.Property(item => item.Status).HasMaxLength(50);
            entity.HasIndex(item => item.Status);
            entity.HasIndex(item => item.ExpiresAt);
            entity.HasOne(item => item.Device)
                .WithMany()
                .HasForeignKey(item => item.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.DeviceInstallation)
                .WithMany()
                .HasForeignKey(item => item.InstallationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GlobalPolicy>(entity =>
        {
            entity.ToTable("global_policies");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasConversion(guidConverter).HasColumnType("binary(16)");
            entity.Property(item => item.Type).HasMaxLength(50);
            entity.Property(item => item.TargetVersionMin).HasMaxLength(50);
            entity.Property(item => item.TargetVersionMax).HasMaxLength(50);
            entity.Property(item => item.Reason).HasMaxLength(1000);
            entity.Property(item => item.Status).HasMaxLength(50);
            entity.HasIndex(item => item.Status);
            entity.HasIndex(item => item.Type);
            entity.HasIndex(item => item.ExpiresAt);
        });

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    private static byte[] GuidToBytes(Guid value) => value.ToByteArray(bigEndian: true);

    private static Guid BytesToGuid(byte[] value) => new(value, bigEndian: true);

    private static byte[]? NullableGuidToBytes(Guid? value) =>
        value.HasValue ? value.Value.ToByteArray(bigEndian: true) : null;

    private static Guid? BytesToNullableGuid(byte[]? value) =>
        value is null ? null : new Guid(value, bigEndian: true);

    private static string ToSnakeCase(string value)
    {
        var result = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsUpper(character) && index > 0)
            {
                result.Append('_');
            }

            result.Append(char.ToLowerInvariant(character));
        }

        return result.ToString();
    }
}
