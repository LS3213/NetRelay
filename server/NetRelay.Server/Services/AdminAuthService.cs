using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NetRelay.Contracts;
using NetRelay.Server.Configuration;
using NetRelay.Server.Data;
using NetRelay.Server.Infrastructure;
using NetRelay.Server.Security;

namespace NetRelay.Server.Services;

public sealed record LoginResult(bool Success, string? ErrorCode, AdminLoginChallenge? Challenge);

public sealed record SessionCreationResult(
    bool Success,
    string? ErrorCode,
    AdminSession? Session,
    string? SessionToken,
    string? CsrfToken);

public sealed class AdminAuthService
{
    public const string SessionCookieName = "__Host-NetRelayAdmin";

    private readonly NetRelayDbContext _dbContext;
    private readonly AdminPasswordService _passwordService;
    private readonly IDataProtector _challengeProtector;
    private readonly IDataProtector _totpProtector;
    private readonly ServerOptions _options;

    public AdminAuthService(
        NetRelayDbContext dbContext,
        AdminPasswordService passwordService,
        IDataProtectionProvider protectionProvider,
        IOptions<ServerOptions> options)
    {
        _dbContext = dbContext;
        _passwordService = passwordService;
        _challengeProtector = protectionProvider.CreateProtector("NetRelay.AdminLoginChallenge.v1");
        _totpProtector = protectionProvider.CreateProtector("NetRelay.AdminTotpSecret.v1");
        _options = options.Value;
    }

    public async Task<LoginResult> LoginAsync(
        string username,
        string password,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var account = await _dbContext.AdminAccounts.SingleOrDefaultAsync(
            item => item.Username == username,
            cancellationToken);
        if (account is null)
        {
            _passwordService.VerifyDummy(password);
            return new LoginResult(false, ErrorCodes.AdminAuthenticationRequired, null);
        }

        if (account.LockedUntil > now)
        {
            _passwordService.VerifyDummy(password);
            return new LoginResult(false, ErrorCodes.AdminAuthenticationRequired, null);
        }

        if (!_passwordService.Verify(account.PasswordHash, password))
        {
            account.FailedLoginCount++;
            if (account.FailedLoginCount >= _options.MaximumLoginFailures)
            {
                account.LockedUntil = now.AddMinutes(_options.AccountLockoutMinutes);
                account.FailedLoginCount = 0;
            }

            account.UpdatedAt = now;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return new LoginResult(false, ErrorCodes.AdminAuthenticationRequired, null);
        }

        account.FailedLoginCount = 0;
        account.LockedUntil = null;
        account.UpdatedAt = now;
        var challengeId = Uuid7.Create();
        // MySQL datetime(6) stores microseconds. Normalize before protecting the
        // payload so its timestamp remains identical after the database round trip.
        var expiresAt = NormalizeMySqlTimestamp(now.AddMinutes(_options.LoginChallengeMinutes));
        _dbContext.AdminLoginChallenges.Add(new AdminLoginChallengeRecord
        {
            Id = challengeId,
            AdminAccountId = account.Id,
            CreatedAt = now,
            ExpiresAt = expiresAt
        });
        await _dbContext.SaveChangesAsync(cancellationToken);

        var payload = JsonSerializer.Serialize(new LoginChallengePayload(challengeId, account.Id, expiresAt));
        return new LoginResult(
            true,
            null,
            new AdminLoginChallenge(_challengeProtector.Protect(payload), expiresAt));
    }

    public async Task<SessionCreationResult> CompleteTotpAsync(
        string challengeToken,
        string code,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        LoginChallengePayload challenge;
        try
        {
            challenge = JsonSerializer.Deserialize<LoginChallengePayload>(
                _challengeProtector.Unprotect(challengeToken)) ??
                throw new InvalidOperationException("Missing challenge payload.");
        }
        catch
        {
            return new SessionCreationResult(false, ErrorCodes.AdminTotpRequired, null, null, null);
        }

        var challengeRecord = await _dbContext.AdminLoginChallenges.FindAsync([challenge.Id], cancellationToken);
        if (challenge.ExpiresAt <= now ||
            challengeRecord is null ||
            challengeRecord.AdminAccountId != challenge.AdminAccountId ||
            challengeRecord.ExpiresAt != challenge.ExpiresAt ||
            challengeRecord.ConsumedAt is not null)
        {
            return new SessionCreationResult(false, ErrorCodes.AdminTotpRequired, null, null, null);
        }

        var account = await _dbContext.AdminAccounts.FindAsync([challenge.AdminAccountId], cancellationToken);
        if (account is null || !TotpService.Verify(_totpProtector.Unprotect(account.ProtectedTotpSecret), code, now))
        {
            return new SessionCreationResult(false, ErrorCodes.AdminTotpRequired, null, null, null);
        }

        var sessionToken = TokenService.CreateToken();
        var csrfToken = TokenService.CreateToken();
        challengeRecord.ConsumedAt = now;
        var session = new AdminSession
        {
            Id = Uuid7.Create(),
            AdminAccountId = account.Id,
            SessionTokenHash = TokenService.Hash(sessionToken),
            CsrfTokenHash = TokenService.Hash(csrfToken),
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(_options.AdminSessionMinutes),
            ReauthenticatedUntil = now.AddMinutes(_options.AdminReauthenticationMinutes),
            LastSeenAt = now
        };
        session.AdminAccount = account;
        _dbContext.AdminSessions.Add(session);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new SessionCreationResult(false, ErrorCodes.AdminTotpRequired, null, null, null);
        }

        return new SessionCreationResult(true, null, session, sessionToken, csrfToken);
    }

    public async Task<AdminSession?> ResolveSessionAsync(
        string? sessionToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return null;
        }

        var tokenHash = TokenService.Hash(sessionToken);
        var session = await _dbContext.AdminSessions
            .Include(item => item.AdminAccount)
            .SingleOrDefaultAsync(item => item.SessionTokenHash == tokenHash, cancellationToken);
        if (session is null || session.RevokedAt is not null || session.ExpiresAt <= now)
        {
            return null;
        }

        session.LastSeenAt = now;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return session;
    }

    public bool VerifyCsrf(AdminSession session, string? csrfToken) =>
        !string.IsNullOrWhiteSpace(csrfToken) && TokenService.FixedTimeEquals(session.CsrfTokenHash, csrfToken);

    public bool VerifyPassword(AdminSession session, string password) =>
        session.AdminAccount is not null && _passwordService.Verify(session.AdminAccount.PasswordHash, password);

    public async Task<string> RotateCsrfAsync(AdminSession session, CancellationToken cancellationToken)
    {
        var token = TokenService.CreateToken();
        session.CsrfTokenHash = TokenService.Hash(token);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return token;
    }

    public string ProtectTotpSecret(string base32Secret) => _totpProtector.Protect(base32Secret);

    private static DateTimeOffset NormalizeMySqlTimestamp(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % 10), TimeSpan.Zero);

    private sealed record LoginChallengePayload(Guid Id, Guid AdminAccountId, DateTimeOffset ExpiresAt);
}
