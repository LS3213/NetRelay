using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NetRelay.Server.Configuration;
using NetRelay.Server.Data;
using NetRelay.Server.Infrastructure;
using NetRelay.Server.Security;

namespace NetRelay.Server.Services;

public sealed class AdminBootstrapService(
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    IOptions<ServerOptions> options,
    ILogger<AdminBootstrapService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await TryBootstrapAsync(stoppingToken))
                {
                    return;
                }
            }
            catch (BootstrapConfigurationException exception)
            {
                logger.LogCritical(exception, "Administrator bootstrap configuration is invalid.");
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Administrator bootstrap is waiting for MySQL.");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task<bool> TryBootstrapAsync(CancellationToken cancellationToken)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NetRelayDbContext>();
        if (options.Value.AutoMigrate)
        {
            await dbContext.Database.MigrateAsync(cancellationToken);
        }

        var accountCount = await dbContext.AdminAccounts.CountAsync(cancellationToken);
        if (accountCount > 1)
        {
            throw new BootstrapConfigurationException("B1 supports exactly one administrator account.");
        }

        if (accountCount == 1)
        {
            logger.LogInformation("Administrator account is initialized.");
            return true;
        }

        var username = configuration["BootstrapAdmin:Username"];
        var password = configuration["BootstrapAdmin:Password"];
        var totpSecret = configuration["BootstrapAdmin:TotpSecret"];
        if (string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(password) ||
            string.IsNullOrWhiteSpace(totpSecret))
        {
            throw new BootstrapConfigurationException(
                "BootstrapAdmin credentials are required when no administrator exists.");
        }

        if (password.Length < 14)
        {
            throw new BootstrapConfigurationException("BootstrapAdmin password must contain at least 14 characters.");
        }

        if (!TotpService.IsValidSecret(totpSecret))
        {
            throw new BootstrapConfigurationException("BootstrapAdmin TOTP secret must be valid Base32.");
        }

        var now = DateTimeOffset.UtcNow;
        var authService = scope.ServiceProvider.GetRequiredService<AdminAuthService>();
        var passwordService = scope.ServiceProvider.GetRequiredService<AdminPasswordService>();
        var account = new AdminAccount
        {
            Id = Uuid7.Create(),
            Username = username,
            PasswordHash = passwordService.Hash(password),
            ProtectedTotpSecret = authService.ProtectTotpSecret(totpSecret),
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.AdminAccounts.Add(account);
        await dbContext.SaveChangesAsync(cancellationToken);

        var audit = scope.ServiceProvider.GetRequiredService<AuditService>();
        await audit.WriteAsync(
            "admin.bootstrap",
            "success",
            "startup",
            account.Id,
            "admin_account",
            account.Id.ToString(),
            cancellationToken: cancellationToken);
        logger.LogInformation("Administrator account initialized.");
        return true;
    }

    private sealed class BootstrapConfigurationException(string message) : Exception(message);
}
