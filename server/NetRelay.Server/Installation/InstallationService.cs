using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MySqlConnector;
using NetRelay.Server.Configuration;
using NetRelay.Server.Data;
using NetRelay.Server.Infrastructure;
using NetRelay.Server.Security;
using NetRelay.Server.Services;

namespace NetRelay.Server.Installation;

public sealed class InstallationService(InstallationState state)
{
    private static readonly SemaphoreSlim InstallGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task TestDatabaseAsync(
        DatabaseInstallationRequest request,
        CancellationToken cancellationToken)
    {
        ValidateDatabase(request);
        await using var connection = new MySqlConnection(BuildConnectionString(request));
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task InstallAsync(InstallationRequest request, CancellationToken cancellationToken)
    {
        await InstallGate.WaitAsync(cancellationToken);
        try
        {
            if (state.IsInstalled)
            {
                throw new InvalidOperationException("NetRelay is already installed.");
            }

            var options = Validate(request);
            await TestDatabaseAsync(request.Database, cancellationToken);
            Directory.CreateDirectory(state.ConfigDirectory);
            foreach (var root in GetStorageRoots(options))
            {
                Directory.CreateDirectory(root);
            }

            var dataProtectionPath = Path.Combine(request.DataRoot, "data-protection");
            Directory.CreateDirectory(dataProtectionPath);
            var connectionString = BuildConnectionString(request.Database);
            var dbOptions = new DbContextOptionsBuilder<NetRelayDbContext>()
                .UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 0)))
                .Options;
            await using var dbContext = new NetRelayDbContext(dbOptions);
            await dbContext.Database.MigrateAsync(cancellationToken);
            if (await dbContext.AdminAccounts.AnyAsync(cancellationToken))
            {
                throw new InvalidOperationException("The selected database already contains an administrator.");
            }

            var dataProtection = DataProtectionProvider.Create(
                new DirectoryInfo(dataProtectionPath),
                configuration => configuration.SetApplicationName("NetRelay.Server"));
            var passwordService = new AdminPasswordService();
            var authService = new AdminAuthService(
                dbContext,
                passwordService,
                dataProtection,
                Options.Create(options));
            var now = DateTimeOffset.UtcNow;
            var account = new AdminAccount
            {
                Id = Uuid7.Create(now),
                Username = request.AdminUsername.Trim(),
                PasswordHash = passwordService.Hash(request.AdminPassword),
                ProtectedTotpSecret = authService.ProtectTotpSecret(request.TotpSecret),
                CreatedAt = now,
                UpdatedAt = now
            };
            dbContext.AdminAccounts.Add(account);
            await dbContext.SaveChangesAsync(cancellationToken);
            var audit = new AuditService(dbContext);
            await audit.WriteAsync(
                "admin.install",
                "success",
                "installer",
                account.Id,
                "admin_account",
                account.Id.ToString(),
                cancellationToken: cancellationToken);

            var runtimeConfig = new
            {
                ConnectionStrings = new { NetRelay = connectionString },
                NetRelay = options,
                DataProtection = new { KeysPath = dataProtectionPath }
            };
            await WriteAtomicAsync(
                state.RuntimeConfigPath,
                JsonSerializer.Serialize(runtimeConfig, JsonOptions),
                cancellationToken);
            await WriteAtomicAsync(
                state.InstallLockPath,
                JsonSerializer.Serialize(new { installedAt = DateTimeOffset.UtcNow }, JsonOptions),
                cancellationToken);
            ProtectConfigFiles();
            DeleteInstallToken();
        }
        finally
        {
            InstallGate.Release();
        }
    }

    private static ServerOptions Validate(InstallationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Database);
        ValidateDatabase(request.Database);
        if (string.IsNullOrWhiteSpace(request.AdminUsername) ||
            request.AdminUsername.Trim().Length is < 3 or > 100)
        {
            throw new ArgumentException("管理员用户名长度必须为 3-100 个字符。");
        }
        if (string.IsNullOrEmpty(request.AdminPassword) || request.AdminPassword.Length < 8)
        {
            throw new ArgumentException("管理员密码至少需要 8 个字符。");
        }
        if (string.IsNullOrWhiteSpace(request.TotpSecret) || !TotpService.IsValidSecret(request.TotpSecret))
        {
            throw new ArgumentException("TOTP Secret 必须为有效 Base32 字符串。");
        }
        if (string.IsNullOrWhiteSpace(request.TotpCode) ||
            !TotpService.Verify(request.TotpSecret, request.TotpCode, DateTimeOffset.UtcNow))
        {
            throw new ArgumentException("TOTP 动态验证码无效，请确认验证器和服务器时间已经同步。");
        }
        if (string.IsNullOrWhiteSpace(request.DataRoot) || !Path.IsPathFullyQualified(request.DataRoot))
        {
            throw new ArgumentException("数据目录必须为绝对路径。");
        }
        if (string.IsNullOrWhiteSpace(request.PublicBaseUrl) ||
            string.IsNullOrWhiteSpace(request.GithubRepository))
        {
            throw new ArgumentException("公开地址和 GitHub 备用仓库不能为空。");
        }

        var root = Path.GetFullPath(request.DataRoot);
        var options = new ServerOptions
        {
            PublicBaseUrl = request.PublicBaseUrl.Trim(),
            GithubRepository = request.GithubRepository.Trim(),
            ReleasesRoot = Path.Combine(root, "releases"),
            FeedbackRoot = Path.Combine(root, "feedback-attachments"),
            StagingRoot = Path.Combine(root, "staging"),
            QuarantineRoot = Path.Combine(root, "quarantine"),
            KeysRoot = Path.Combine(root, "keys"),
            AutoMigrate = false
        };
        var error = ServerOptionsValidator.Validate(options);
        if (error is not null)
        {
            throw new ArgumentException(error);
        }

        return options;
    }

    private static void ValidateDatabase(DatabaseInstallationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Host) ||
            request.Port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(request.Database) ||
            string.IsNullOrWhiteSpace(request.Username) ||
            request.Database.Length > 64 ||
            request.Username.Length > 100)
        {
            throw new ArgumentException("数据库连接参数无效。");
        }
    }

    private static string BuildConnectionString(DatabaseInstallationRequest request) =>
        new MySqlConnectionStringBuilder
        {
            Server = request.Host.Trim(),
            Port = (uint)request.Port,
            Database = request.Database.Trim(),
            UserID = request.Username.Trim(),
            Password = request.Password ?? string.Empty,
            SslMode = MySqlSslMode.Preferred,
            ConnectionTimeout = 5,
            DefaultCommandTimeout = 30,
            AllowUserVariables = false
        }.ConnectionString;

    private static IEnumerable<string> GetStorageRoots(ServerOptions options)
    {
        yield return options.ReleasesRoot;
        yield return options.FeedbackRoot;
        yield return options.StagingRoot;
        yield return options.QuarantineRoot;
        yield return options.KeysRoot;
    }

    private static async Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporary, content, cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }

    private void ProtectConfigFiles()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        File.SetUnixFileMode(
            state.RuntimeConfigPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.SetUnixFileMode(
            state.InstallLockPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private void DeleteInstallToken()
    {
        if (File.Exists(state.InstallTokenPath))
        {
            File.Delete(state.InstallTokenPath);
        }
    }
}
