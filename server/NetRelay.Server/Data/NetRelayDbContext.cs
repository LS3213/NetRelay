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
