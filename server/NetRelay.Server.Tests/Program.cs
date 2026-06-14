using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using NetRelay.Contracts.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NetRelay.Contracts;
using NetRelay.Server;
using System.IO;
using System.IO.Compression;
using NetRelay.Server.Services;

using NetRelay.Server.Configuration;
using NetRelay.Server.Data;
using NetRelay.Server.Infrastructure;
using NetRelay.Server.Installation;
using NetRelay.Server.Security;
using NetRelay.Server.Services;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Server options accept valid production-shaped configuration", () => RunSync(TestValidOptions)),
    ("Server options reject insecure public URL", () => RunSync(TestInvalidOptions)),
    ("Server options reject placeholder deployment values", () => RunSync(TestPlaceholderOptions)),
    ("Server options reject nested storage roots", () => RunSync(TestNestedStorageOptions)),
    ("Password hashing verifies only the original password", () => RunSync(TestPasswordHashing)),
    ("Unknown-user password verification follows the protected path", () => RunSync(TestDummyPasswordVerification)),
    ("TOTP validates an RFC 6238-compatible code", () => RunSync(TestTotp)),
    ("TOTP rejects invalid bootstrap secrets", () => RunSync(TestInvalidTotpSecret)),
    ("Tokens hash deterministically without storing plaintext", () => RunSync(TestTokens)),
    ("UUID v7 contains version, variant, and sortable timestamp", () => RunSync(TestUuid7)),
    ("EF model enforces the B1 single-admin foundations", () => RunSync(TestEfModel)),
    ("Managed storage rejects traversal and moves staged files", TestManagedStorageAsync),
    ("Admin authentication consumes TOTP challenges once", TestAdminAuthenticationAsync),
    ("Audit records form a verifiable hash chain", TestAuditHashChainAsync),
    ("Runtime OpenAPI contract is included in server output", () => RunSync(TestRuntimeOpenApi)),
    ("Installation state requires a protected token and permanent lock", () => RunSync(TestInstallationState)),
    ("Installation mode exposes only the protected installer", TestInstallationHttpBoundaryAsync),
    ("Admin HTTP authentication workflow enforces session and CSRF boundaries", TestAdminHttpWorkflowAsync),
    ("SignedEnvelope and OperationalKeyCertificate verification work correctly", () => RunSync(TestSignedEnvelopeAndCertificate)),
    ("Device fingerprint activation matching logic behaves correctly", TestDeviceFingerprintActivationMatchingAsync),
    ("Device activation and connectivity challenge endpoints work correctly", TestDeviceActivationAndChallengeApiAsync),
    ("Update management and download endpoints behave correctly", TestUpdateApiAsync),
    ("Feedback submission, listing, status updates, and download behavior work correctly", TestFeedbackApiAsync),
    ("Announcement lifecycle (creation, editing, signing, and retrieval) works correctly", TestAnnouncementApiAsync),
    ("Device block and global policies (evaluate, block, revoke, and version evaluation) work correctly", TestPolicyApiAsync)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS: {test.Name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.WriteLine($"FAIL: {test.Name}");
        Console.WriteLine(exception);
    }
}

if (failed > 0)
{
    Environment.ExitCode = 1;
    return;
}

Console.WriteLine($"{tests.Length} B1 server foundation tests passed.");

static void TestValidOptions()
{
    var options = CreateOptions();
    Assert(ServerOptionsValidator.Validate(options) is null, "Valid options were rejected.");
}

static void TestInvalidOptions()
{
    var options = CreateOptions() with
    {
        PublicBaseUrl = "http://netrelay.example",
        GithubRepository = "../repository"
    };
    Assert(ServerOptionsValidator.Validate(options) is not null, "Invalid options were accepted.");
}

static void TestPlaceholderOptions()
{
    var options = CreateOptions() with
    {
        PublicBaseUrl = "https://localhost",
        GithubRepository = "owner/repository"
    };
    Assert(ServerOptionsValidator.Validate(options) is not null, "Placeholder deployment options were accepted.");
}

static void TestNestedStorageOptions()
{
    var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "netrelay-nested-options"));
    var options = CreateOptions(root) with
    {
        FeedbackRoot = Path.Combine(root, "releases", "feedback")
    };
    Assert(ServerOptionsValidator.Validate(options) is not null, "Nested storage roots were accepted.");
}

static void TestPasswordHashing()
{
    var service = new AdminPasswordService();
    var hash = service.Hash("correct horse battery staple");
    Assert(!hash.Contains("correct horse", StringComparison.Ordinal), "Password hash contains plaintext.");
    Assert(service.Verify(hash, "correct horse battery staple"), "Correct password was rejected.");
    Assert(!service.Verify(hash, "incorrect"), "Incorrect password was accepted.");
}

static void TestDummyPasswordVerification()
{
    var service = new AdminPasswordService();
    service.VerifyDummy("attacker-controlled-password");
}

static void TestTotp()
{
    const string rfcSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
    var timestamp = DateTimeOffset.FromUnixTimeSeconds(59);
    Assert(TotpService.GenerateCode(rfcSecret, timestamp) == "287082", "Known TOTP vector was not generated.");
    Assert(TotpService.Verify(rfcSecret, "287082", timestamp), "Known TOTP vector was rejected.");
    Assert(!TotpService.Verify(rfcSecret, "287083", timestamp), "Incorrect TOTP was accepted.");
}

static void TestInvalidTotpSecret()
{
    Assert(TotpService.IsValidSecret("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"), "Valid Base32 secret was rejected.");
    Assert(!TotpService.IsValidSecret("not-a-secret"), "Invalid Base32 secret was accepted.");
}

static void TestTokens()
{
    var token = TokenService.CreateToken();
    var hash = TokenService.Hash(token);
    Assert(token.Length >= 40, "Token entropy is unexpectedly low.");
    Assert(hash.Length == 64, "Token hash is not SHA-256.");
    Assert(TokenService.FixedTimeEquals(hash, token), "Token hash comparison failed.");
    Assert(!TokenService.FixedTimeEquals(hash, token + "x"), "Wrong token matched.");
}

static void TestUuid7()
{
    var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var value = Uuid7.Create();
    var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    Span<byte> bytes = stackalloc byte[16];
    Assert(value.TryWriteBytes(bytes, bigEndian: true, out var written) && written == 16, "UUID bytes unavailable.");
    Assert((bytes[6] >> 4) == 7, "UUID version is not 7.");
    Assert((bytes[8] & 0xc0) == 0x80, "UUID variant is invalid.");
    var timestamp = ((long)bytes[0] << 40) |
                    ((long)bytes[1] << 32) |
                    ((long)bytes[2] << 24) |
                    ((long)bytes[3] << 16) |
                    ((long)bytes[4] << 8) |
                    bytes[5];
    Assert(timestamp >= before && timestamp <= after, "UUID timestamp is outside the creation window.");
}

static void TestEfModel()
{
    var options = new DbContextOptionsBuilder<NetRelayDbContext>()
        .UseMySql(
            "Server=localhost;Database=netrelay;User=test;Password=test",
            new MySqlServerVersion(new Version(8, 0, 0)))
        .Options;
    using var dbContext = new NetRelayDbContext(options);
    var account = dbContext.Model.FindEntityType(typeof(AdminAccount)) ??
        throw new InvalidOperationException("Admin account entity is missing.");
    var session = dbContext.Model.FindEntityType(typeof(AdminSession)) ??
        throw new InvalidOperationException("Admin session entity is missing.");
    var audit = dbContext.Model.FindEntityType(typeof(AuditLog)) ??
        throw new InvalidOperationException("Audit entity is missing.");
    var challenge = dbContext.Model.FindEntityType(typeof(AdminLoginChallengeRecord)) ??
        throw new InvalidOperationException("Login challenge entity is missing.");
    Assert(account.GetTableName() == "admin_accounts", "Admin account table name is invalid.");
    Assert(account.FindProperty(nameof(AdminAccount.Id))?.GetColumnType() == "binary(16)",
        "Admin account UUID must use binary(16).");
    Assert(account.FindProperty(nameof(AdminAccount.PasswordHash))?.GetColumnName() == "password_hash",
        "Database columns must use snake_case.");
    Assert(account.GetIndexes().Any(index =>
            index.IsUnique && index.Properties.Single().Name == nameof(AdminAccount.SingletonKey)),
        "Database must enforce a single administrator account.");
    Assert(account.GetIndexes().Single(index => index.Properties.Single().Name == nameof(AdminAccount.Username)).IsUnique,
        "Admin username must be unique.");
    Assert(session.GetIndexes().Any(index => index.IsUnique), "Session token must have a unique index.");
    Assert(audit.GetTableName() == "audit_logs", "Audit table name is invalid.");
    Assert(challenge.GetTableName() == "admin_login_challenges", "Login challenge table name is invalid.");
    Assert(challenge.FindProperty(nameof(AdminLoginChallengeRecord.ConsumedAt))?.IsConcurrencyToken == true,
        "Login challenge consumption must be concurrency protected.");
}

static async Task TestManagedStorageAsync()
{
    var root = CreateTemporaryDirectory();
    try
    {
        var options = CreateOptions(root);
        var storage = new ManagedFileStorage(Options.Create(options));
        storage.EnsureDirectories();
        var stagingPath = storage.Resolve(StorageArea.Staging, "upload.tmp");
        await File.WriteAllTextAsync(stagingPath, "payload");
        await storage.MoveFromStagingAsync("upload.tmp", StorageArea.Releases, Path.Combine("1.0.0", "asset.bin"));
        Assert(File.Exists(storage.Resolve(StorageArea.Releases, "1.0.0", "asset.bin")), "Staged file was not moved.");
        AssertThrows<InvalidOperationException>(() => storage.Resolve(StorageArea.Feedback, "..", "escape.txt"));
        AssertThrows<InvalidOperationException>(() => storage.Resolve(StorageArea.Releases, Path.GetFullPath("escape")));
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task TestAdminAuthenticationAsync()
{
    const string secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
    var root = CreateTemporaryDirectory();
    try
    {
        await using var provider = CreateServiceProvider(root, "auth-" + Guid.NewGuid());
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NetRelayDbContext>();
        var auth = scope.ServiceProvider.GetRequiredService<AdminAuthService>();
        var password = scope.ServiceProvider.GetRequiredService<AdminPasswordService>();
        var now = DateTimeOffset.UtcNow;
        var account = new AdminAccount
        {
            Id = Uuid7.Create(now),
            Username = "admin",
            PasswordHash = password.Hash("correct horse battery staple"),
            ProtectedTotpSecret = auth.ProtectTotpSecret(secret),
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.AdminAccounts.Add(account);
        await dbContext.SaveChangesAsync();

        var wrong = await auth.LoginAsync("admin", "wrong", now, CancellationToken.None);
        Assert(!wrong.Success, "Wrong password was accepted.");
        var login = await auth.LoginAsync("admin", "correct horse battery staple", now, CancellationToken.None);
        Assert(login.Success && login.Challenge is not null, "Correct password did not create a challenge.");
        var challengeRecord = await dbContext.AdminLoginChallenges.SingleAsync();
        Assert(
            challengeRecord.ExpiresAt.UtcTicks % 10 == 0,
            "Login challenge expiration must fit MySQL datetime(6) precision.");
        var code = TotpService.GenerateCode(secret, now);
        var session = await auth.CompleteTotpAsync(login.Challenge!.ChallengeToken, code, now, CancellationToken.None);
        Assert(session.Success && session.SessionToken is not null && session.CsrfToken is not null,
            "TOTP did not create a session.");
        var replay = await auth.CompleteTotpAsync(login.Challenge.ChallengeToken, code, now, CancellationToken.None);
        Assert(!replay.Success, "Consumed TOTP challenge was accepted again.");
        var resolved = await auth.ResolveSessionAsync(session.SessionToken, now, CancellationToken.None);
        Assert(resolved is not null, "Created session could not be resolved.");
        Assert(auth.VerifyCsrf(resolved!, session.CsrfToken), "Valid CSRF token was rejected.");
        Assert(!auth.VerifyCsrf(resolved!, session.CsrfToken + "x"), "Invalid CSRF token was accepted.");
        var expired = await auth.ResolveSessionAsync(session.SessionToken, now.AddDays(1), CancellationToken.None);
        Assert(expired is null, "Expired session remained usable.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task TestAuditHashChainAsync()
{
    var root = CreateTemporaryDirectory();
    try
    {
        await using var provider = CreateServiceProvider(root, "audit-" + Guid.NewGuid());
        await using var scope = provider.CreateAsyncScope();
        var audit = scope.ServiceProvider.GetRequiredService<AuditService>();
        var dbContext = scope.ServiceProvider.GetRequiredService<NetRelayDbContext>();
        await audit.WriteAsync("test.first", "success", "request-1");
        await audit.WriteAsync("test.second", "failure", "request-2");
        var records = await dbContext.AuditLogs.OrderBy(item => item.Id).ToListAsync();
        Assert(records.Count == 2, "Audit records were not written.");
        Assert(records[0].PreviousHash is null, "First audit record must start the chain.");
        Assert(records[1].PreviousHash == records[0].EntryHash, "Audit hash chain is broken.");
        Assert(records.All(item => item.EntryHash.Length == 64), "Audit entry hash is invalid.");
        var valid = await audit.VerifyChainAsync();
        Assert(valid.Valid && valid.VerifiedRecords == 2, "Valid audit chain was rejected.");
        records[0].Result = "tampered";
        await dbContext.SaveChangesAsync();
        var invalid = await audit.VerifyChainAsync();
        Assert(!invalid.Valid && invalid.InvalidRecordId == records[0].Id, "Tampered audit chain was accepted.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void TestRuntimeOpenApi()
{
    var path = Path.Combine(AppContext.BaseDirectory, "OpenApi", "netrelay-v1.yaml");
    Assert(File.Exists(path), "Runtime OpenAPI contract was not copied to output.");
    Assert(File.ReadAllText(path).Contains("/admin/auth/login:", StringComparison.Ordinal),
        "Runtime OpenAPI contract does not contain implemented authentication endpoints.");
}

static void TestInstallationState()
{
    var root = CreateTemporaryDirectory();
    var previous = new Dictionary<string, string?>
    {
        [InstallationState.ConfigDirectoryEnvironment] =
            Environment.GetEnvironmentVariable(InstallationState.ConfigDirectoryEnvironment),
        [InstallationState.InstallModeEnvironment] =
            Environment.GetEnvironmentVariable(InstallationState.InstallModeEnvironment),
        [InstallationState.InstallTokenEnvironment] =
            Environment.GetEnvironmentVariable(InstallationState.InstallTokenEnvironment)
    };
    try
    {
        Environment.SetEnvironmentVariable(InstallationState.ConfigDirectoryEnvironment, root);
        Environment.SetEnvironmentVariable(InstallationState.InstallModeEnvironment, "true");
        Environment.SetEnvironmentVariable(InstallationState.InstallTokenEnvironment, null);
        File.WriteAllText(Path.Combine(root, "install.token"), new string('a', 64));
        var state = new InstallationState();
        Assert(state.IsInstallMode && !state.IsInstalled, "Fresh portable deployment did not enter install mode.");
        Assert(state.VerifyInstallToken(new string('a', 64)), "Valid install token was rejected.");
        Assert(!state.VerifyInstallToken("short"), "Invalid-length install token was accepted.");
        File.WriteAllText(state.InstallLockPath, "{}");
        var lockedWithoutConfig = new InstallationState();
        Assert(
            !lockedWithoutConfig.IsInstalled && !lockedWithoutConfig.IsInstallMode,
            "An install lock without runtime configuration reopened the installer.");
        File.WriteAllText(state.RuntimeConfigPath, "{}");
        var installed = new InstallationState();
        Assert(installed.IsInstalled && !installed.IsInstallMode, "Install lock did not permanently disable install mode.");
    }
    finally
    {
        RestoreEnvironment(previous);
        Directory.Delete(root, recursive: true);
    }
}

static async Task TestInstallationHttpBoundaryAsync()
{
    var root = CreateTemporaryDirectory();
    var previous = new Dictionary<string, string?>
    {
        [InstallationState.ConfigDirectoryEnvironment] =
            Environment.GetEnvironmentVariable(InstallationState.ConfigDirectoryEnvironment),
        [InstallationState.InstallModeEnvironment] =
            Environment.GetEnvironmentVariable(InstallationState.InstallModeEnvironment),
        [InstallationState.InstallTokenEnvironment] =
            Environment.GetEnvironmentVariable(InstallationState.InstallTokenEnvironment)
    };
    try
    {
        Environment.SetEnvironmentVariable(InstallationState.ConfigDirectoryEnvironment, root);
        Environment.SetEnvironmentVariable(InstallationState.InstallModeEnvironment, "true");
        Environment.SetEnvironmentVariable(InstallationState.InstallTokenEnvironment, new string('b', 64));
        await using var factory = new InstallServerFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        var installer = await client.GetAsync("/install/");
        Assert(installer.StatusCode == HttpStatusCode.OK, "Install page was not available in install mode.");
        var ordinaryApi = await client.GetAsync("/api/v1/admin/auth/me");
        Assert(ordinaryApi.StatusCode == HttpStatusCode.Redirect, "Ordinary API was exposed in install mode.");
        var missingToken = await client.PostAsJsonAsync(
            "/install/api/test-database",
            new DatabaseInstallationRequest("127.0.0.1", 3306, "netrelay", "netrelay", "password"));
        Assert(missingToken.StatusCode == HttpStatusCode.Forbidden, "Installer API accepted a missing install token.");
        using var wrongTokenRequest = new HttpRequestMessage(HttpMethod.Post, "/install/api/test-database")
        {
            Content = JsonContent.Create(
                new DatabaseInstallationRequest("127.0.0.1", 3306, "netrelay", "netrelay", "password"))
        };
        wrongTokenRequest.Headers.Add(InstallationEndpoints.InstallTokenHeader, "short");
        var wrongToken = await client.SendAsync(wrongTokenRequest);
        Assert(wrongToken.StatusCode == HttpStatusCode.Forbidden, "Installer API accepted an invalid install token.");

        using var totpRequest = new HttpRequestMessage(HttpMethod.Post, "/install/api/totp-setup")
        {
            Content = JsonContent.Create(new TotpSetupRequest("admin"))
        };
        totpRequest.Headers.Add(InstallationEndpoints.InstallTokenHeader, new string('b', 64));
        var totpResponse = await client.SendAsync(totpRequest);
        Assert(totpResponse.StatusCode == HttpStatusCode.OK, "Protected TOTP setup endpoint was unavailable.");
        var totpSetup = await totpResponse.Content.ReadFromJsonAsync<TotpSetupResponse>() ??
            throw new InvalidOperationException("TOTP setup response was missing.");
        Assert(TotpService.IsValidSecret(totpSetup.Secret), "Generated TOTP secret was invalid.");
        Assert(
            totpSetup.QrCodeDataUri.StartsWith("data:image/svg+xml;base64,", StringComparison.Ordinal),
            "TOTP setup did not return an inline SVG QR code.");
        using var verifyTotpRequest = new HttpRequestMessage(HttpMethod.Post, "/install/api/totp-verify")
        {
            Content = JsonContent.Create(
                new TotpVerificationRequest(
                    totpSetup.Secret,
                    TotpService.GenerateCode(totpSetup.Secret, DateTimeOffset.UtcNow)))
        };
        verifyTotpRequest.Headers.Add(InstallationEndpoints.InstallTokenHeader, new string('b', 64));
        var verifyTotpResponse = await client.SendAsync(verifyTotpRequest);
        Assert(verifyTotpResponse.StatusCode == HttpStatusCode.OK, "Generated TOTP setup could not be verified.");
    }
    finally
    {
        RestoreEnvironment(previous);
        Directory.Delete(root, recursive: true);
    }
}

static async Task TestAdminHttpWorkflowAsync()
{
    const string password = "correct horse battery staple";
    const string secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
    var root = CreateTemporaryDirectory();
    var environment = ApplyTestEnvironment(root, password, secret);
    try
    {
        await using var factory = new TestServerFactory(root, password, secret);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
            AllowAutoRedirect = false
        });

        ApiResponse<AdminLoginChallenge>? login = null;
        for (var attempt = 0; attempt < 20 && login is null; attempt++)
        {
            var response = await SendJsonAsync(
                client,
                HttpMethod.Post,
                "/api/v1/admin/auth/login",
                new AdminLoginRequest("admin", password));
            if (response.StatusCode == HttpStatusCode.OK)
            {
                login = await response.Content.ReadFromJsonAsync<ApiResponse<AdminLoginChallenge>>();
                break;
            }

            await Task.Delay(50);
        }

        Assert(login is not null, "Administrator bootstrap or password login did not complete.");
        var totp = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/v1/admin/auth/totp",
            new AdminTotpRequest(login!.Data.ChallengeToken, TotpService.GenerateCode(secret, DateTimeOffset.UtcNow)));
        Assert(totp.StatusCode == HttpStatusCode.OK, "TOTP endpoint rejected a valid code.");
        var session = await totp.Content.ReadFromJsonAsync<ApiResponse<AdminSessionResponse>>() ??
            throw new InvalidOperationException("Session response was missing.");

        var replay = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/v1/admin/auth/totp",
            new AdminTotpRequest(login.Data.ChallengeToken, TotpService.GenerateCode(secret, DateTimeOffset.UtcNow)));
        Assert(replay.StatusCode == HttpStatusCode.Unauthorized, "TOTP challenge replay was accepted.");

        var malformedTotp = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/v1/admin/auth/totp",
            new { challengeToken = login.Data.ChallengeToken });
        Assert(malformedTotp.StatusCode == HttpStatusCode.BadRequest, "Malformed TOTP request did not return 400.");

        var identity = await SendAsync(client, HttpMethod.Get, "/api/v1/admin/auth/me");
        Assert(identity.StatusCode == HttpStatusCode.OK, "Authenticated session was rejected.");
        var csrfResponse = await SendAsync(client, HttpMethod.Get, "/api/v1/admin/auth/csrf");
        Assert(csrfResponse.StatusCode == HttpStatusCode.OK, "Authenticated CSRF rotation was rejected.");
        var rotatedCsrf = await csrfResponse.Content.ReadFromJsonAsync<ApiResponse<AdminCsrfResponse>>() ??
            throw new InvalidOperationException("CSRF rotation response was missing.");

        var missingCsrf = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/v1/admin/auth/reauthenticate",
            new AdminReauthenticateRequest(password));
        Assert(missingCsrf.StatusCode == HttpStatusCode.Forbidden, "Reauthentication without CSRF was accepted.");
        var oldCsrf = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/v1/admin/auth/reauthenticate",
            new AdminReauthenticateRequest(password),
            session.Data.CsrfToken);
        Assert(oldCsrf.StatusCode == HttpStatusCode.Forbidden, "Rotated-out CSRF token remained valid.");

        var reauthenticated = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/v1/admin/auth/reauthenticate",
            new AdminReauthenticateRequest(password),
            rotatedCsrf.Data.CsrfToken);
        Assert(reauthenticated.StatusCode == HttpStatusCode.NoContent, "Valid reauthentication was rejected.");

        var audit = await SendAsync(client, HttpMethod.Get, "/api/v1/admin/audit/verify");
        Assert(audit.StatusCode == HttpStatusCode.OK, "Authenticated audit verification was rejected.");
        var auditResult = await audit.Content.ReadFromJsonAsync<ApiResponse<AuditChainVerificationResult>>() ??
            throw new InvalidOperationException("Audit verification response was missing.");
        Assert(auditResult.Data.Valid, "HTTP workflow produced an invalid audit chain.");

        var logout = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/v1/admin/auth/logout",
            new { },
            rotatedCsrf.Data.CsrfToken);
        Assert(logout.StatusCode == HttpStatusCode.NoContent, "Valid logout was rejected.");
        var afterLogout = await SendAsync(client, HttpMethod.Get, "/api/v1/admin/auth/me");
        Assert(afterLogout.StatusCode == HttpStatusCode.Unauthorized, "Revoked session remained usable.");

        var rateLimited = false;
        for (var attempt = 0; attempt < 15; attempt++)
        {
            var response = await SendJsonAsync(
                client,
                HttpMethod.Post,
                "/api/v1/admin/auth/login",
                new AdminLoginRequest("unknown", "invalid"));
            rateLimited |= response.StatusCode == HttpStatusCode.TooManyRequests;
        }
        Assert(rateLimited, "Administrator login rate limiting did not return 429.");
    }
    finally
    {
        RestoreEnvironment(environment);
        Directory.Delete(root, recursive: true);
    }
}

static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string? csrf = null)
{
    var request = new HttpRequestMessage(method, path);
    request.Headers.Add(Protocol.VersionHeader, Protocol.CurrentVersion.ToString());
    if (csrf is not null)
    {
        request.Headers.Add(Protocol.CsrfHeader, csrf);
    }

    return client.SendAsync(request);
}

static Task<HttpResponseMessage> SendJsonAsync<T>(
    HttpClient client,
    HttpMethod method,
    string path,
    T body,
    string? csrf = null)
{
    var request = new HttpRequestMessage(method, path)
    {
        Content = JsonContent.Create(body, options: new JsonSerializerOptions(JsonSerializerDefaults.Web))
    };
    request.Headers.Add(Protocol.VersionHeader, Protocol.CurrentVersion.ToString());
    if (csrf is not null)
    {
        request.Headers.Add(Protocol.CsrfHeader, csrf);
    }

    return client.SendAsync(request);
}

static Task<HttpResponseMessage> SendGetAsync(
    HttpClient client,
    string path)
{
    var request = new HttpRequestMessage(HttpMethod.Get, path);
    request.Headers.Add(Protocol.VersionHeader, Protocol.CurrentVersion.ToString());
    return client.SendAsync(request);
}


static ServiceProvider CreateServiceProvider(string root, string databaseName)
{
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddDbContext<NetRelayDbContext>(options => options.UseInMemoryDatabase(databaseName));
    services.AddDataProtection()
        .SetApplicationName("NetRelay.Server.Tests")
        .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "keys")));
    services.AddSingleton<AdminPasswordService>();
    services.AddScoped<AdminAuthService>();
    services.AddScoped<AuditService>();
    services.AddSingleton<KeyManagementService>();
    services.AddScoped<DeviceActivationService>();
    services.AddSingleton<IOptions<ServerOptions>>(Options.Create(CreateOptions(root)));
    return services.BuildServiceProvider();
}

static ServerOptions CreateOptions(string? root = null)
{
    root ??= Path.GetFullPath(Path.Combine(Path.GetTempPath(), "netrelay-options"));
    return new ServerOptions
    {
        PublicBaseUrl = "https://netrelay.cn",
        GithubRepository = "netrelay/netrelay",
        ReleasesRoot = Path.Combine(root, "releases"),
        FeedbackRoot = Path.Combine(root, "feedback"),
        StagingRoot = Path.Combine(root, "staging"),
        QuarantineRoot = Path.Combine(root, "quarantine"),
        KeysRoot = Path.Combine(root, "keys_root")
    };
}

static string CreateTemporaryDirectory()
{
    var path = Path.Combine(Path.GetTempPath(), "NetRelay.Server.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
}

static Dictionary<string, string?> ApplyTestEnvironment(string root, string password, string secret)
{
    var values = new Dictionary<string, string?>
    {
        ["ConnectionStrings__NetRelay"] = "Server=unused;Database=unused;User=unused;Password=unused",
        ["NetRelay__PublicBaseUrl"] = "https://netrelay.cn",
        ["NetRelay__GithubRepository"] = "netrelay/netrelay",
        ["NetRelay__ReleasesRoot"] = Path.Combine(root, "releases"),
        ["NetRelay__FeedbackRoot"] = Path.Combine(root, "feedback"),
        ["NetRelay__StagingRoot"] = Path.Combine(root, "staging"),
        ["NetRelay__QuarantineRoot"] = Path.Combine(root, "quarantine"),
        ["NetRelay__KeysRoot"] = Path.Combine(root, "keys_root"),
        ["NetRelay__AutoMigrate"] = "false",
        ["BootstrapAdmin__Username"] = "admin",
        ["BootstrapAdmin__Password"] = password,
        ["BootstrapAdmin__TotpSecret"] = secret,
        ["DataProtection__KeysPath"] = Path.Combine(root, "keys")
    };
    var previous = values.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
    foreach (var value in values)
    {
        Environment.SetEnvironmentVariable(value.Key, value.Value);
    }

    return previous;
}

static void RestoreEnvironment(IReadOnlyDictionary<string, string?> previous)
{
    foreach (var value in previous)
    {
        Environment.SetEnvironmentVariable(value.Key, value.Value);
    }
}

static Task RunSync(Action action)
{
    action();
    return Task.CompletedTask;
}

static void AssertThrows<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name} was not thrown.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void TestSignedEnvelopeAndCertificate()
{
    using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var operationKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    
    var now = DateTimeOffset.UtcNow;
    var notBefore = now.AddMinutes(-5);
    var notAfter = now.AddDays(30);
    
    // Create operational key certificate
    var cert = OperationalKeyCertificate.Create(
        "key-1",
        operationKey.ExportSubjectPublicKeyInfo(),
        new[] { "device-activation", "connectivity-challenge" },
        notBefore,
        notAfter,
        rootKey);
        
    Assert(cert.KeyId == "key-1", "Certificate KeyId mismatch.");
    Assert(cert.Verify(now, rootKey, "device-activation"), "Valid certificate was rejected.");
    Assert(!cert.Verify(now, rootKey, "unauthorized-purpose"), "Certificate was verified for unauthorized purpose.");
    Assert(!cert.Verify(now.AddDays(31), rootKey, "device-activation"), "Expired certificate was verified.");
    
    // Test SignedEnvelope
    var payload = new Dictionary<string, object?> { ["test"] = "value" };
    var envelope = SignedEnvelope.Create("device-activation", "nonce-123", now, now.AddMinutes(5), payload, operationKey);
    
    Assert(envelope.Verify("device-activation", "nonce-123", now, operationKey), "Valid envelope was rejected.");
    Assert(!envelope.Verify("device-activation", "nonce-123", now.AddMinutes(6), operationKey), "Expired envelope was verified.");
    Assert(!envelope.Verify("device-activation", "wrong-nonce", now, operationKey), "Envelope with mismatched nonce was verified.");
    Assert(!envelope.Verify("wrong-purpose", "nonce-123", now, operationKey), "Envelope with mismatched purpose was verified.");
}

static async Task TestDeviceFingerprintActivationMatchingAsync()
{
    var root = CreateTemporaryDirectory();
    try
    {
        await using var provider = CreateServiceProvider(root, "device-activate-" + Guid.NewGuid());
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NetRelayDbContext>();
        var activationService = scope.ServiceProvider.GetRequiredService<DeviceActivationService>();
        
        var installationId1 = Guid.NewGuid();
        var request1 = new DeviceActivationRequest
        {
            InstallationId = installationId1,
            FingerprintVersion = 1,
            DeviceId = "hash-1",
            AcceptedTermsVersion = "1.0",
            AcceptedPrivacyVersion = "1.0",
            ClientVersion = "1.0.0",
            OsVersion = "Windows 10",
            ProtocolVersion = 1,
            Evidence = new Dictionary<string, List<string>>
            {
                ["hardware.windowsDeviceId"] = new List<string> { "win-id-1" },
                ["hardware.machineGuid"] = new List<string> { "guid-1" },
                ["hardware.systemUuid"] = new List<string> { "uuid-1" },
                ["network.physicalCandidate.mac"] = new List<string> { "mac-1" }
            }
        };
        
        var response1 = await activationService.ActivateDeviceAsync(request1, CancellationToken.None);
        Assert(response1 != null, "Activation failed.");
        Assert(!string.IsNullOrEmpty(response1.MachineCode), "MachineCode is empty.");
        
        var devicesCount = await dbContext.Devices.CountAsync();
        Assert(devicesCount == 1, $"Expected 1 device, got {devicesCount}.");
        
        // 1. Test cloning/matching with threshold met:
        // We match 2 core features: windowsDeviceId and machineGuid.
        // score: 2 * 20 (core) + 0 * 2 (network) = 40. Matches default min score 40, and min core match 2.
        var installationId2 = Guid.NewGuid();
        var request2 = new DeviceActivationRequest
        {
            InstallationId = installationId2,
            FingerprintVersion = 1,
            DeviceId = "hash-2",
            AcceptedTermsVersion = "1.0",
            AcceptedPrivacyVersion = "1.0",
            ClientVersion = "1.0.0",
            OsVersion = "Windows 10",
            ProtocolVersion = 1,
            Evidence = new Dictionary<string, List<string>>
            {
                ["hardware.windowsDeviceId"] = new List<string> { "win-id-1" }, // MATCH 1
                ["hardware.machineGuid"] = new List<string> { "guid-1" },       // MATCH 2
                ["hardware.systemUuid"] = new List<string> { "uuid-different" }, // mismatch
                ["network.physicalCandidate.mac"] = new List<string> { "mac-different" }
            }
        };
        
        var response2 = await activationService.ActivateDeviceAsync(request2, CancellationToken.None);
        Assert(response2.MachineCode == response1.MachineCode, "Cloned device failed to match existing device.");
        
        devicesCount = await dbContext.Devices.CountAsync();
        var installationsCount = await dbContext.DeviceInstallations.CountAsync();
        Assert(devicesCount == 1, $"Expected device count to stay 1, got {devicesCount}.");
        Assert(installationsCount == 2, $"Expected installation count to be 2, got {installationsCount}.");
        
        // 2. Test mismatched device (below threshold):
        // Only 1 core feature matches: windowsDeviceId.
        // coreMatches = 1 < 2, so it shouldn't match.
        var installationId3 = Guid.NewGuid();
        var request3 = new DeviceActivationRequest
        {
            InstallationId = installationId3,
            FingerprintVersion = 1,
            DeviceId = "hash-3",
            AcceptedTermsVersion = "1.0",
            AcceptedPrivacyVersion = "1.0",
            ClientVersion = "1.0.0",
            OsVersion = "Windows 10",
            ProtocolVersion = 1,
            Evidence = new Dictionary<string, List<string>>
            {
                ["hardware.windowsDeviceId"] = new List<string> { "win-id-1" }, // MATCH 1
                ["hardware.machineGuid"] = new List<string> { "guid-different-three" },
                ["hardware.systemUuid"] = new List<string> { "uuid-different-three" },
                ["network.physicalCandidate.mac"] = new List<string> { "mac-1" } // network match (doesn't count towards core count)
            }
        };
        
        var response3 = await activationService.ActivateDeviceAsync(request3, CancellationToken.None);
        Assert(response3.MachineCode != response1.MachineCode, "Mismatched device incorrectly matched existing device.");
        
        devicesCount = await dbContext.Devices.CountAsync();
        Assert(devicesCount == 2, $"Expected 2 devices in db, got {devicesCount}.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task TestDeviceActivationAndChallengeApiAsync()
{
    const string password = "correct horse battery staple";
    const string secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
    var root = CreateTemporaryDirectory();
    var environment = ApplyTestEnvironment(root, password, secret);
    try
    {
        await using var factory = new TestServerFactory(root, password, secret);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
            AllowAutoRedirect = false
        });

        // 1. Activate device
        var installationId = Guid.NewGuid();
        var activateRequest = new DeviceActivationRequest
        {
            InstallationId = installationId,
            FingerprintVersion = 1,
            DeviceId = "device-id-hash",
            AcceptedTermsVersion = "1.0",
            AcceptedPrivacyVersion = "1.0",
            ClientVersion = "1.0.0",
            OsVersion = "Windows 10",
            ProtocolVersion = 1,
            Evidence = new Dictionary<string, List<string>>
            {
                ["hardware.windowsDeviceId"] = new List<string> { "win-id" },
                ["hardware.machineGuid"] = new List<string> { "machine-guid" }
            }
        };

        var activateResponseMsg = await SendJsonAsync(client, HttpMethod.Post, "/api/v1/devices/activate", activateRequest);
        Assert(activateResponseMsg.StatusCode == HttpStatusCode.OK, $"Device activation failed: {activateResponseMsg.StatusCode}");
        
        var activateResponse = await activateResponseMsg.Content.ReadFromJsonAsync<ApiResponse<DeviceActivationResponse>>();
        Assert(activateResponse != null && activateResponse.Data != null, "Activation response payload is null.");
        Assert(!string.IsNullOrEmpty(activateResponse.Data.MachineCode), "MachineCode is empty.");
        
        var envelope = activateResponse.Data.Envelope;
        
        // 2. Perform Connectivity Challenge
        var challengeRequest = new ConnectivityChallengeRequest { Nonce = "nonce-val-123" };
        var challengeResponseMsg = await SendJsonAsync(client, HttpMethod.Post, "/api/v1/connectivity/challenge", challengeRequest);
        Assert(challengeResponseMsg.StatusCode == HttpStatusCode.OK, $"Challenge failed: {challengeResponseMsg.StatusCode}");
        
        var challengeResponse = await challengeResponseMsg.Content.ReadFromJsonAsync<ApiResponse<ConnectivityChallengeResponse>>();
        Assert(challengeResponse != null && challengeResponse.Data != null, "Challenge response payload is null.");
        
        // Verify challenge signature
        using var rootKey = ECDsa.Create();
        rootKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(OperationalKeyCertificate.DefaultRootPublicKeyBase64), out _);
        
        Assert(challengeResponse.Data.Certificate.Verify(DateTimeOffset.UtcNow, rootKey, "connectivity-challenge"), "Challenge certificate verification failed.");
        
        using var opKey = ECDsa.Create();
        opKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(challengeResponse.Data.Certificate.PublicKey), out _);
        Assert(challengeResponse.Data.Envelope.Verify("connectivity-challenge", challengeResponse.Data.Envelope.Nonce, DateTimeOffset.UtcNow, opKey), "Challenge envelope signature verification failed.");
        
        // 3. Heartbeat
        var heartbeatRequest = new DeviceHeartbeatRequest
        {
            InstallationId = installationId,
            MachineCode = activateResponse.Data.MachineCode,
            ClientVersion = "1.0.0",
            OsVersion = "Windows 10",
            ReceiptEnvelope = envelope
        };
        
        var heartbeatResponseMsg = await SendJsonAsync(client, HttpMethod.Post, "/api/v1/devices/heartbeat", heartbeatRequest);
        Assert(heartbeatResponseMsg.StatusCode == HttpStatusCode.OK, $"Heartbeat failed: {heartbeatResponseMsg.StatusCode}");
        
        var heartbeatResponse = await heartbeatResponseMsg.Content.ReadFromJsonAsync<ApiResponse<string>>();
        Assert(heartbeatResponse != null && heartbeatResponse.Data != null, "Heartbeat response payload is null.");
        
        // Due to 24h throttling, the second heartbeat should be throttled (but still return 200 OK with throttling message)
        var heartbeatResponseMsg2 = await SendJsonAsync(client, HttpMethod.Post, "/api/v1/devices/heartbeat", heartbeatRequest);
        Assert(heartbeatResponseMsg2.StatusCode == HttpStatusCode.OK, $"Heartbeat 2 failed: {heartbeatResponseMsg2.StatusCode}");
        
        var heartbeatResponse2 = await heartbeatResponseMsg2.Content.ReadFromJsonAsync<ApiResponse<string>>();
        Assert(heartbeatResponse2 != null && heartbeatResponse2.Data.Contains("throttled"), "Heartbeat was not throttled on second call.");
    }
    finally
    {
        RestoreEnvironment(environment);
        Directory.Delete(root, recursive: true);
    }
}

static async Task TestUpdateApiAsync()
{
    const string password = "correct horse battery staple";
    const string secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
    var root = CreateTemporaryDirectory();
    var environment = ApplyTestEnvironment(root, password, secret);
    try
    {
        await using var factory = new TestServerFactory(root, password, secret);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
            AllowAutoRedirect = false
        });

        // 1. Check latest updates (before any release uploaded) -> expect 404 UpdateNotAvailable
        var initCheck = await SendGetAsync(client, "/api/v1/updates/latest?channel=stable&architecture=win-x64&currentVersion=1.0.0");
        Assert(initCheck.StatusCode == HttpStatusCode.NotFound, $"Initial check should fail: {initCheck.StatusCode}");
        var initCheckResponse = await initCheck.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert(initCheckResponse != null && initCheckResponse.Error != null && initCheckResponse.Error.Code == ErrorCodes.UpdateNotAvailable, "Error code should be UPDATE_NOT_AVAILABLE");

        // 2. Perform admin login
        var loginResponseMsg = await SendJsonAsync(client, HttpMethod.Post, "/api/v1/admin/auth/login", new AdminLoginRequest("admin", password));
        Assert(loginResponseMsg.StatusCode == HttpStatusCode.OK, "Login failed");
        var login = await loginResponseMsg.Content.ReadFromJsonAsync<ApiResponse<AdminLoginChallenge>>() ?? throw new InvalidOperationException();
        
        var totpResponseMsg = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/v1/admin/auth/totp",
            new AdminTotpRequest(login.Data.ChallengeToken, TotpService.GenerateCode(secret, DateTimeOffset.UtcNow)));
        Assert(totpResponseMsg.StatusCode == HttpStatusCode.OK, "TOTP validation failed");
        var session = await totpResponseMsg.Content.ReadFromJsonAsync<ApiResponse<AdminSessionResponse>>() ?? throw new InvalidOperationException();
        var csrf = session.Data.CsrfToken;

        // 3. Create dummy update zip
        var dummyZipPath = Path.Combine(root, "win-x64.zip");
        using (var archive = ZipFile.Open(dummyZipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("test.txt");
            using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("hello update");
        }

        // 4. Create Release (draft)
        using var fileStream = File.OpenRead(dummyZipPath);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("1.3.0"), "version");
        form.Add(new StringContent("stable"), "channel");
        form.Add(new StringContent("win-x64"), "architecture");
        form.Add(new StringContent("1.0.0"), "minUpgradableVersion");
        form.Add(new StringContent("Changelog message"), "changelog");
        
        var streamContent = new StreamContent(fileStream);
        streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
        form.Add(streamContent, "file", "win-x64.zip");

        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/releases")
        {
            Content = form
        };
        createRequest.Headers.Add(Protocol.VersionHeader, Protocol.CurrentVersion.ToString());
        createRequest.Headers.Add(Protocol.CsrfHeader, csrf);

        var createResponseMsg = await client.SendAsync(createRequest);
        Assert(createResponseMsg.StatusCode == HttpStatusCode.OK, $"Create release failed: {createResponseMsg.StatusCode}");
        
        var createResponse = await createResponseMsg.Content.ReadFromJsonAsync<ApiResponse<Release>>();
        Assert(createResponse != null && createResponse.Data != null, "Create release response null");
        var releaseId = createResponse.Data.Id;
        Assert(createResponse.Data.Status == "draft", "Status should be draft");

        // 5. Check latest updates -> expect 404 (draft releases should not be visible to clients)
        var draftCheck = await SendGetAsync(client, "/api/v1/updates/latest?channel=stable&architecture=win-x64&currentVersion=1.0.0");
        Assert(draftCheck.StatusCode == HttpStatusCode.NotFound, "Draft release should not be visible");

        // 6. Reauthenticate admin for sensitive operations
        var reauthResponseMsg = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/v1/admin/auth/reauthenticate",
            new AdminReauthenticateRequest(password),
            csrf);
        Assert(reauthResponseMsg.StatusCode == HttpStatusCode.NoContent, "Reauthentication failed");

        // 7. Publish Release
        var publishResponseMsg = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/v1/admin/releases/{releaseId}/publish",
            new { },
            csrf);
        Assert(publishResponseMsg.StatusCode == HttpStatusCode.OK, $"Publish release failed: {publishResponseMsg.StatusCode}");

        // 8. Check latest updates -> expect 200 OK with signed UpdateCheckResponse
        var checkResult = await SendGetAsync(client, "/api/v1/updates/latest?channel=stable&architecture=win-x64&currentVersion=1.0.0");
        Assert(checkResult.StatusCode == HttpStatusCode.OK, $"Check updates after publish failed: {checkResult.StatusCode}");

        var checkResponse = await checkResult.Content.ReadFromJsonAsync<ApiResponse<UpdateCheckResponse>>();
        Assert(checkResponse != null && checkResponse.Data != null, "Check updates payload null");

        // Validate check updates response signature
        using var rootKey = ECDsa.Create();
        rootKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(OperationalKeyCertificate.DefaultRootPublicKeyBase64), out _);
        Assert(checkResponse.Data.Certificate.Verify(DateTimeOffset.UtcNow, rootKey, "update-manifest"), "Cert verification failed");

        using var opKey = ECDsa.Create();
        opKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(checkResponse.Data.Certificate.PublicKey), out _);
        Assert(checkResponse.Data.Envelope.Verify("update-manifest", checkResponse.Data.Envelope.Nonce, DateTimeOffset.UtcNow, opKey), "Envelope verification failed");

        var manifest = JsonSerializer.Deserialize<UpdateManifest>(checkResponse.Data.Envelope.PayloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert(manifest != null, "Manifest deserialization failed");
        Assert(manifest.Version == "1.3.0", "Version mismatch");
        Assert(manifest.Sha256 == createResponse.Data.Sha256, "Sha256 hash mismatch");

        // 9. Download package
        var downloadResult = await SendGetAsync(client, "/api/v1/updates/1.3.0/download/win-x64.zip");
        Assert(downloadResult.StatusCode == HttpStatusCode.OK, $"Download package failed: {downloadResult.StatusCode}");
        
        var bytes = await downloadResult.Content.ReadAsByteArrayAsync();
        Assert(bytes.Length == createResponse.Data.PackageSize, "Downloaded package size mismatch");

        // 10. Reauthenticate and Revoke Release
        var reauthResponseMsg2 = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/v1/admin/auth/reauthenticate",
            new AdminReauthenticateRequest(password),
            csrf);
        Assert(reauthResponseMsg2.StatusCode == HttpStatusCode.NoContent, "Reauthentication 2 failed");

        var revokeResponseMsg = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/v1/admin/releases/{releaseId}/revoke",
            new { },
            csrf);
        Assert(revokeResponseMsg.StatusCode == HttpStatusCode.OK, $"Revoke release failed: {revokeResponseMsg.StatusCode}");

        // 11. Check latest updates -> expect 404 UpdateNotAvailable
        var finalCheck = await SendGetAsync(client, "/api/v1/updates/latest?channel=stable&architecture=win-x64&currentVersion=1.0.0");
        Assert(finalCheck.StatusCode == HttpStatusCode.NotFound, "Revoked release should not be visible");
    }
    finally
    {
        RestoreEnvironment(environment);
        Directory.Delete(root, recursive: true);
    }
}

static async Task TestFeedbackApiAsync()
{
    const string password = "correct horse battery staple";
    const string secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
    var root = CreateTemporaryDirectory();
    var environment = ApplyTestEnvironment(root, password, secret);
    try
    {
        await using var factory = new TestServerFactory(root, password, secret);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
            AllowAutoRedirect = false
        });

        // 1. Submit invalid feedback (missing type/title/content)
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new StringContent(""), "type");
            form.Add(new StringContent("Some title"), "title");
            form.Add(new StringContent("Some content"), "content");
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/feedback") { Content = form };
            req.Headers.Add(Protocol.VersionHeader, Protocol.CurrentVersion.ToString());
            var response = await client.SendAsync(req);
            Assert(response.StatusCode == HttpStatusCode.BadRequest, $"Empty type should be rejected: {response.StatusCode}");
        }

        // 2. Submit invalid feedback type
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new StringContent("invalid_type"), "type");
            form.Add(new StringContent("Some title"), "title");
            form.Add(new StringContent("Some content"), "content");
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/feedback") { Content = form };
            req.Headers.Add(Protocol.VersionHeader, Protocol.CurrentVersion.ToString());
            var response = await client.SendAsync(req);
            Assert(response.StatusCode == HttpStatusCode.BadRequest, $"Invalid type should be rejected: {response.StatusCode}");
        }

        // 3. Submit feedback with non-zip attachment
        var badFilePath = Path.Combine(root, "bad.txt");
        await File.WriteAllTextAsync(badFilePath, "not a zip");
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new StringContent("bug"), "type");
            form.Add(new StringContent("Some title"), "title");
            form.Add(new StringContent("Some content"), "content");
            using var fileStream = File.OpenRead(badFilePath);
            var streamContent = new StreamContent(fileStream);
            streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
            form.Add(streamContent, "file", "bad.txt");
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/feedback") { Content = form };
            req.Headers.Add(Protocol.VersionHeader, Protocol.CurrentVersion.ToString());
            var response = await client.SendAsync(req);
            Assert(response.StatusCode == HttpStatusCode.BadRequest, $"Non-zip extension should be rejected: {response.StatusCode}");
        }

        // 4. Submit valid feedback with a ZIP attachment
        var goodZipPath = Path.Combine(root, "logs.zip");
        using (var archive = ZipFile.Open(goodZipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("log.txt");
            using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("system diagnostic logs content");
        }

        Guid feedbackId;
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new StringContent("bug"), "type");
            form.Add(new StringContent("UI Crash"), "title");
            form.Add(new StringContent("App crashed when clicking feedback button"), "content");
            form.Add(new StringContent("user@example.com"), "contact");
            using var fileStream = File.OpenRead(goodZipPath);
            var streamContent = new StreamContent(fileStream);
            streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
            form.Add(streamContent, "file", "logs.zip");

            var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/feedback") { Content = form };
            req.Headers.Add(Protocol.VersionHeader, Protocol.CurrentVersion.ToString());
            var response = await client.SendAsync(req);
            Assert(response.StatusCode == HttpStatusCode.OK, $"Submit valid feedback failed: {response.StatusCode}");
            var apiResponse = await response.Content.ReadFromJsonAsync<ApiResponse<Feedback>>();
            Assert(apiResponse != null && apiResponse.Data != null, "Response should have feedback data");
            Assert(apiResponse.Data.Type == "bug", "Type mismatch");
            Assert(apiResponse.Data.HasAttachment == true, "HasAttachment should be true");
            Assert(apiResponse.Data.AttachmentFilename == "logs.zip", "Attachment filename mismatch");
            feedbackId = apiResponse.Data.Id;
        }

        // 5. Admin check feedbacks (unauthenticated)
        var listUnauth = await SendGetAsync(client, "/api/v1/admin/feedback");
        Assert(listUnauth.StatusCode == HttpStatusCode.Unauthorized, $"Admin list should be protected: {listUnauth.StatusCode}");

        // 6. Admin Login
        var loginResponseMsg = await SendJsonAsync(client, HttpMethod.Post, "/api/v1/admin/auth/login", new AdminLoginRequest("admin", password));
        Assert(loginResponseMsg.StatusCode == HttpStatusCode.OK, "Login failed");
        var login = await loginResponseMsg.Content.ReadFromJsonAsync<ApiResponse<AdminLoginChallenge>>() ?? throw new InvalidOperationException();
        
        var totpResponseMsg = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/v1/admin/auth/totp",
            new AdminTotpRequest(login.Data.ChallengeToken, TotpService.GenerateCode(secret, DateTimeOffset.UtcNow)));
        Assert(totpResponseMsg.StatusCode == HttpStatusCode.OK, "TOTP validation failed");
        var session = await totpResponseMsg.Content.ReadFromJsonAsync<ApiResponse<AdminSessionResponse>>() ?? throw new InvalidOperationException();
        var csrf = session.Data.CsrfToken;

        // 7. Admin check feedbacks (authenticated)
        var listAuth = await SendGetAsync(client, "/api/v1/admin/feedback");
        Assert(listAuth.StatusCode == HttpStatusCode.OK, $"Admin list should succeed: {listAuth.StatusCode}");
        var feedbacksList = await listAuth.Content.ReadFromJsonAsync<ApiResponse<List<Feedback>>>();
        Assert(feedbacksList != null && feedbacksList.Data != null, "Feedbacks list should not be null");
        Assert(feedbacksList.Data.Any(f => f.Id == feedbackId), "List must contain submitted feedback");

        // 8. Re-authenticate
        var reauthResponseMsg = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/v1/admin/auth/reauthenticate",
            new AdminReauthenticateRequest(password),
            csrf);
        Assert(reauthResponseMsg.StatusCode == HttpStatusCode.NoContent, "Reauthentication failed");

        // 9. Update feedback status -> succeeds
        var updateStatusMsg = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/v1/admin/feedback/{feedbackId}/status",
            new FeedbackStatusUpdateRequest { Status = "resolved" },
            csrf);
        Assert(updateStatusMsg.StatusCode == HttpStatusCode.OK, $"Status update should succeed: {updateStatusMsg.StatusCode}");
        var updatedFeedback = await updateStatusMsg.Content.ReadFromJsonAsync<ApiResponse<Feedback>>();
        Assert(updatedFeedback != null && updatedFeedback.Data != null && updatedFeedback.Data.Status == "resolved", "Status should be updated to resolved");

        // 10. Download attachment
        var downloadMsg = await SendGetAsync(client, $"/api/v1/admin/feedback/{feedbackId}/attachment");
        Assert(downloadMsg.StatusCode == HttpStatusCode.OK, $"Download attachment should succeed: {downloadMsg.StatusCode}");
        var downloadBytes = await downloadMsg.Content.ReadAsByteArrayAsync();
        var uploadBytes = await File.ReadAllBytesAsync(goodZipPath);
        Assert(downloadBytes.SequenceEqual(uploadBytes), "Downloaded attachment bytes should match uploaded zip");
    }
    finally
    {
        RestoreEnvironment(environment);
        Directory.Delete(root, recursive: true);
    }
}

static async Task TestAnnouncementApiAsync()
{
    const string password = "correct horse battery staple";
    const string secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
    var root = CreateTemporaryDirectory();
    var environment = ApplyTestEnvironment(root, password, secret);
    try
    {
        await using var factory = new TestServerFactory(root, password, secret);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
            AllowAutoRedirect = false
        });

        // 1. Check active announcements initially -> empty signed response
        var initCheck = await SendGetAsync(client, "/api/v1/announcements/active?clientVersion=1.0.0");
        Assert(initCheck.StatusCode == HttpStatusCode.OK, $"Initial check failed: {initCheck.StatusCode}");
        var initRes = await initCheck.Content.ReadFromJsonAsync<ApiResponse<AnnouncementCheckResponse>>();
        Assert(initRes != null && initRes.Data != null, "Response should have payload");
        Assert(initRes.Data.Envelope != null, "Envelope should not be null");
        Assert(initRes.Data.Certificate != null, "Certificate should not be null");

        var payloadJson = initRes.Data.Envelope.PayloadJson;
        var payloadObj = JsonSerializer.Deserialize<Dictionary<string, object>>(payloadJson);
        Assert(payloadObj != null && payloadObj.ContainsKey("announcements"), "Payload must contain announcements key");
        var announcementsJson = payloadObj["announcements"].ToString();
        var initList = JsonSerializer.Deserialize<List<object>>(announcementsJson!);
        Assert(initList != null && initList.Count == 0, "Initial active announcements list should be empty");

        // 2. Admin Login
        var loginResponseMsg = await SendJsonAsync(client, HttpMethod.Post, "/api/v1/admin/auth/login", new AdminLoginRequest("admin", password));
        Assert(loginResponseMsg.StatusCode == HttpStatusCode.OK, "Login failed");
        var login = await loginResponseMsg.Content.ReadFromJsonAsync<ApiResponse<AdminLoginChallenge>>() ?? throw new InvalidOperationException();
        
        var totpResponseMsg = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/v1/admin/auth/totp",
            new AdminTotpRequest(login.Data.ChallengeToken, TotpService.GenerateCode(secret, DateTimeOffset.UtcNow)));
        Assert(totpResponseMsg.StatusCode == HttpStatusCode.OK, "TOTP validation failed");
        var session = await totpResponseMsg.Content.ReadFromJsonAsync<ApiResponse<AdminSessionResponse>>() ?? throw new InvalidOperationException();
        var csrf = session.Data.CsrfToken;

        // 3. Create announcement (draft)
        var createReq = new AnnouncementCreateRequest
        {
            Title = "Scheduled Maintenance",
            Content = "System will be down for 2 hours.",
            Severity = "important",
            TargetVersionMin = "1.2.0",
            TargetVersionMax = "1.5.0",
            DisplayTrigger = "once_per_device",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        };

        var createMsg = await SendJsonAsync(client, HttpMethod.Post, "/api/v1/admin/announcements", createReq, csrf);
        Assert(createMsg.StatusCode == HttpStatusCode.OK, $"Create draft failed: {createMsg.StatusCode}");
        var createRes = await createMsg.Content.ReadFromJsonAsync<ApiResponse<Announcement>>();
        Assert(createRes != null && createRes.Data != null, "Create result is null");
        Assert(createRes.Data.Status == "draft", "Status should be draft");
        var announcementId = createRes.Data.Id;

        // 4. Edit announcement
        var editReq = new AnnouncementCreateRequest
        {
            Title = "Scheduled Maintenance (Updated)",
            Content = "System will be down for 1 hour.",
            Severity = "important",
            TargetVersionMin = "1.2.0",
            TargetVersionMax = "1.5.0",
            DisplayTrigger = "once_per_device",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        };
        var editMsg = await SendJsonAsync(client, HttpMethod.Put, $"/api/v1/admin/announcements/{announcementId}", editReq, csrf);
        Assert(editMsg.StatusCode == HttpStatusCode.OK, $"Edit draft failed: {editMsg.StatusCode}");
        var editRes = await editMsg.Content.ReadFromJsonAsync<ApiResponse<Announcement>>();
        Assert(editRes != null && editRes.Data != null && editRes.Data.Title == "Scheduled Maintenance (Updated)", "Edit verification failed");

        // 5. Re-authenticate
        var reauthResponseMsg = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/v1/admin/auth/reauthenticate",
            new AdminReauthenticateRequest(password),
            csrf);
        Assert(reauthResponseMsg.StatusCode == HttpStatusCode.NoContent, "Reauthentication failed");

        // 6. Publish announcement -> succeeds
        var publishMsg = await SendJsonAsync(client, HttpMethod.Post, $"/api/v1/admin/announcements/{announcementId}/publish", new { }, csrf);
        Assert(publishMsg.StatusCode == HttpStatusCode.OK, $"Publish failed: {publishMsg.StatusCode}");

        // 8. Try editing after published -> expect 400
        var editAfterPublishMsg = await SendJsonAsync(client, HttpMethod.Put, $"/api/v1/admin/announcements/{announcementId}", editReq, csrf);
        Assert(editAfterPublishMsg.StatusCode == HttpStatusCode.BadRequest, $"Editing published announcement should fail: {editAfterPublishMsg.StatusCode}");

        // 9. Query active announcements with clientVersion matching target
        var matchCheck = await SendGetAsync(client, "/api/v1/announcements/active?clientVersion=1.3.0");
        Assert(matchCheck.StatusCode == HttpStatusCode.OK, "Check active announcements failed");
        var matchRes = await matchCheck.Content.ReadFromJsonAsync<ApiResponse<AnnouncementCheckResponse>>();
        Assert(matchRes != null && matchRes.Data != null, "Match response should have data");
        
        var matchPayloadJson = matchRes.Data.Envelope.PayloadJson;
        var matchPayloadObj = JsonSerializer.Deserialize<Dictionary<string, object>>(matchPayloadJson);
        var matchAnnouncementsJson = matchPayloadObj!["announcements"].ToString();
        var matchAnnouncements = JsonSerializer.Deserialize<List<AnnouncementDto>>(matchAnnouncementsJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert(matchAnnouncements != null && matchAnnouncements.Count == 1, "Should return 1 active announcement");
        Assert(matchAnnouncements[0].Id == announcementId, "ID mismatch");
        Assert(matchAnnouncements[0].Title == "Scheduled Maintenance (Updated)", "Title mismatch");

        // Validate signing using SignedEnvelope verification helper
        using (var ecdsa = ECDsa.Create())
        {
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(matchRes.Data.Certificate.PublicKey), out _);
            var verified = matchRes.Data.Envelope.Verify("announcement", matchRes.Data.Envelope.Nonce, DateTimeOffset.UtcNow, ecdsa);
            Assert(verified, "Signature verification failed using operational certificate public key");
        }

        // 10. Query active announcements with clientVersion NOT matching target
        var mismatchCheck = await SendGetAsync(client, "/api/v1/announcements/active?clientVersion=1.1.0");
        Assert(mismatchCheck.StatusCode == HttpStatusCode.OK, "Check mismatch active announcements failed");
        var mismatchRes = await mismatchCheck.Content.ReadFromJsonAsync<ApiResponse<AnnouncementCheckResponse>>();
        var mismatchPayloadJson = mismatchRes!.Data!.Envelope.PayloadJson;
        var mismatchPayloadObj = JsonSerializer.Deserialize<Dictionary<string, object>>(mismatchPayloadJson);
        var mismatchAnnouncementsJson = mismatchPayloadObj!["announcements"].ToString();
        var mismatchAnnouncements = JsonSerializer.Deserialize<List<AnnouncementDto>>(mismatchAnnouncementsJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert(mismatchAnnouncements != null && mismatchAnnouncements.Count == 0, "Mismatching version should return empty active announcements");

        // 11. Re-authenticate and Revoke announcement
        var reauthResponseMsg2 = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/v1/admin/auth/reauthenticate",
            new AdminReauthenticateRequest(password),
            csrf);
        Assert(reauthResponseMsg2.StatusCode == HttpStatusCode.NoContent, "Reauthentication 2 failed");

        var revokeMsg = await SendJsonAsync(client, HttpMethod.Post, $"/api/v1/admin/announcements/{announcementId}/revoke", new { }, csrf);
        Assert(revokeMsg.StatusCode == HttpStatusCode.OK, $"Revoke failed: {revokeMsg.StatusCode}");

        // 12. Check active announcements again -> empty
        var postRevokeCheck = await SendGetAsync(client, "/api/v1/announcements/active?clientVersion=1.3.0");
        var postRevokeRes = await postRevokeCheck.Content.ReadFromJsonAsync<ApiResponse<AnnouncementCheckResponse>>();
        var postRevokePayloadJson = postRevokeRes!.Data!.Envelope.PayloadJson;
        var postRevokePayloadObj = JsonSerializer.Deserialize<Dictionary<string, object>>(postRevokePayloadJson);
        var postRevokeAnnouncementsJson = postRevokePayloadObj!["announcements"].ToString();
        var postRevokeAnnouncements = JsonSerializer.Deserialize<List<AnnouncementDto>>(postRevokeAnnouncementsJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert(postRevokeAnnouncements != null && postRevokeAnnouncements.Count == 0, "Revoked announcement should not be returned");
    }
    finally
    {
        RestoreEnvironment(environment);
        Directory.Delete(root, recursive: true);
    }
}

static async Task TestPolicyApiAsync()
{
    const string password = "correct horse battery staple";
    const string secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
    var root = CreateTemporaryDirectory();
    var environment = ApplyTestEnvironment(root, password, secret);
    try
    {
        await using var factory = new TestServerFactory(root, password, secret);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
            AllowAutoRedirect = false
        });

        // 1. Activate device
        var installationId = Guid.NewGuid();
        var activateRequest = new DeviceActivationRequest
        {
            InstallationId = installationId,
            FingerprintVersion = 1,
            DeviceId = "test-device-id-hash",
            AcceptedTermsVersion = "1.0",
            AcceptedPrivacyVersion = "1.0",
            ClientVersion = "1.0.0",
            OsVersion = "Windows 10",
            ProtocolVersion = 1,
            Evidence = new Dictionary<string, List<string>>
            {
                ["hardware.windowsDeviceId"] = new List<string> { "win-id-123" },
                ["hardware.machineGuid"] = new List<string> { "machine-guid-123" }
            }
        };

        var activateResponseMsg = await SendJsonAsync(client, HttpMethod.Post, "/api/v1/devices/activate", activateRequest);
        Assert(activateResponseMsg.StatusCode == HttpStatusCode.OK, $"Device activation failed: {activateResponseMsg.StatusCode}");

        // 2. Evaluate policy initially -> not blocked
        var evalReq = new PolicyEvaluateRequest
        {
            DeviceId = "test-device-id-hash",
            InstallationId = installationId,
            ClientVersion = "1.0.0"
        };
        var evalResponseMsg = await SendJsonAsync(client, HttpMethod.Post, "/api/v1/policies/evaluate", evalReq);
        Assert(evalResponseMsg.StatusCode == HttpStatusCode.OK, $"Policy evaluation failed: {evalResponseMsg.StatusCode}");
        var evalRes = await evalResponseMsg.Content.ReadFromJsonAsync<ApiResponse<PolicyEvaluateResponse>>();
        Assert(evalRes != null && evalRes.Data != null, "Policy response payload is null.");
        
        // Double-verify signatures
        using (var rootKey = ECDsa.Create())
        {
            rootKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(OperationalKeyCertificate.DefaultRootPublicKeyBase64), out _);
            Assert(evalRes.Data.Certificate.Verify(DateTimeOffset.UtcNow, rootKey, "policy"), "Certificate verification failed.");
            
            using var opKey = ECDsa.Create();
            opKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(evalRes.Data.Certificate.PublicKey), out _);
            Assert(evalRes.Data.Envelope.Verify("policy", evalRes.Data.Envelope.Nonce, DateTimeOffset.UtcNow, opKey), "Envelope verification failed.");
        }

        var payload = JsonSerializer.Deserialize<PolicyEvaluationResult>(evalRes.Data.Envelope.PayloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert(payload != null && !payload.IsBlocked, "Device should not be blocked initially.");

        // 3. Admin Login
        var loginResponseMsg = await SendJsonAsync(client, HttpMethod.Post, "/api/v1/admin/auth/login", new AdminLoginRequest("admin", password));
        Assert(loginResponseMsg.StatusCode == HttpStatusCode.OK, "Login failed");
        var login = await loginResponseMsg.Content.ReadFromJsonAsync<ApiResponse<AdminLoginChallenge>>() ?? throw new InvalidOperationException();
        
        var totpResponseMsg = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/v1/admin/auth/totp",
            new AdminTotpRequest(login.Data.ChallengeToken, TotpService.GenerateCode(secret, DateTimeOffset.UtcNow)));
        Assert(totpResponseMsg.StatusCode == HttpStatusCode.OK, "TOTP validation failed");
        var session = await totpResponseMsg.Content.ReadFromJsonAsync<ApiResponse<AdminSessionResponse>>() ?? throw new InvalidOperationException();
        var csrf = session.Data.CsrfToken;

        // Re-auth
        var reauthResponseMsg = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/v1/admin/auth/reauthenticate",
            new AdminReauthenticateRequest(password),
            csrf);
        Assert(reauthResponseMsg.StatusCode == HttpStatusCode.NoContent, "Reauthentication failed");

        // Fetch Device.Id & DeviceInstallation.Id from database
        Guid dbDeviceId;
        Guid dbInstallationId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NetRelayDbContext>();
            var device = await db.Devices.FirstAsync(d => d.DeviceIdHash == "test-device-id-hash");
            var inst = await db.DeviceInstallations.FirstAsync(i => i.InstallationId == installationId);
            dbDeviceId = device.Id;
            dbInstallationId = inst.Id;
        }

        // 4. Create DeviceBlock
        var blockReq = new DeviceBlockCreateRequest
        {
            DeviceId = dbDeviceId,
            Reason = "Test Block Reason",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
        };
        var createBlockMsg = await SendJsonAsync(client, HttpMethod.Post, "/api/v1/admin/device-blocks", blockReq, csrf);
        Assert(createBlockMsg.StatusCode == HttpStatusCode.OK, $"Create block failed: {createBlockMsg.StatusCode}");
        var blockRes = await createBlockMsg.Content.ReadFromJsonAsync<ApiResponse<DeviceBlockDto>>();
        Assert(blockRes != null && blockRes.Data != null, "Create block response payload is null.");
        Assert(blockRes.Data.Status == "active", "Block status should be active.");
        var blockId = blockRes.Data.Id;

        // 5. Evaluate policy again -> blocked
        var evalResponseMsg2 = await SendJsonAsync(client, HttpMethod.Post, "/api/v1/policies/evaluate", evalReq);
        Assert(evalResponseMsg2.StatusCode == HttpStatusCode.OK, $"Policy evaluation failed: {evalResponseMsg2.StatusCode}");
        var evalRes2 = await evalResponseMsg2.Content.ReadFromJsonAsync<ApiResponse<PolicyEvaluateResponse>>();
        var payload2 = JsonSerializer.Deserialize<PolicyEvaluationResult>(evalRes2!.Data.Envelope.PayloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert(payload2 != null && payload2.IsBlocked, "Device should be blocked.");
        Assert(payload2.Reason == "Test Block Reason", "Reason should match.");

        // 6. Revoke block
        var revokeBlockMsg = await SendJsonAsync(client, HttpMethod.Post, $"/api/v1/admin/device-blocks/{blockId}/revoke", new { }, csrf);
        Assert(revokeBlockMsg.StatusCode == HttpStatusCode.OK, $"Revoke block failed: {revokeBlockMsg.StatusCode}");

        // 7. Evaluate policy again -> unblocked
        var evalResponseMsg3 = await SendJsonAsync(client, HttpMethod.Post, "/api/v1/policies/evaluate", evalReq);
        var evalRes3 = await evalResponseMsg3.Content.ReadFromJsonAsync<ApiResponse<PolicyEvaluateResponse>>();
        var payload3 = JsonSerializer.Deserialize<PolicyEvaluationResult>(evalRes3!.Data.Envelope.PayloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert(payload3 != null && !payload3.IsBlocked, "Device should be unblocked.");

        // 8. Create Global Policy of type version_range
        var policyReq = new GlobalPolicyCreateRequest
        {
            Type = "version_range",
            TargetVersionMin = "1.0.0",
            TargetVersionMax = "1.0.5",
            Reason = "Vulnerable client version",
            AllowUpdate = true,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
        };
        var createPolicyMsg = await SendJsonAsync(client, HttpMethod.Post, "/api/v1/admin/policies", policyReq, csrf);
        Assert(createPolicyMsg.StatusCode == HttpStatusCode.OK, $"Create global policy failed: {createPolicyMsg.StatusCode}");
        var policyRes = await createPolicyMsg.Content.ReadFromJsonAsync<ApiResponse<GlobalPolicyDto>>();
        Assert(policyRes != null && policyRes.Data != null, "Create policy response payload is null.");
        var policyId = policyRes.Data.Id;

        // 9. Evaluate policy with ClientVersion in range -> blocked
        var evalReq4 = new PolicyEvaluateRequest
        {
            DeviceId = "test-device-id-hash",
            InstallationId = installationId,
            ClientVersion = "1.0.3"
        };
        var evalResponseMsg4 = await SendJsonAsync(client, HttpMethod.Post, "/api/v1/policies/evaluate", evalReq4);
        var evalRes4 = await evalResponseMsg4.Content.ReadFromJsonAsync<ApiResponse<PolicyEvaluateResponse>>();
        var payload4 = JsonSerializer.Deserialize<PolicyEvaluationResult>(evalRes4!.Data.Envelope.PayloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert(payload4 != null && payload4.IsBlocked, "Device should be version-range blocked.");
        Assert(payload4.Reason == "Vulnerable client version", "Reason should match.");

        // 10. Evaluate policy with ClientVersion out of range -> unblocked
        var evalReq5 = new PolicyEvaluateRequest
        {
            DeviceId = "test-device-id-hash",
            InstallationId = installationId,
            ClientVersion = "1.0.6"
        };
        var evalResponseMsg5 = await SendJsonAsync(client, HttpMethod.Post, "/api/v1/policies/evaluate", evalReq5);
        var evalRes5 = await evalResponseMsg5.Content.ReadFromJsonAsync<ApiResponse<PolicyEvaluateResponse>>();
        var payload5 = JsonSerializer.Deserialize<PolicyEvaluationResult>(evalRes5!.Data.Envelope.PayloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert(payload5 != null && !payload5.IsBlocked, "Device should not be blocked.");

        // 11. Revoke Global Policy
        var revokePolicyMsg = await SendJsonAsync(client, HttpMethod.Post, $"/api/v1/admin/policies/{policyId}/revoke", new { }, csrf);
        Assert(revokePolicyMsg.StatusCode == HttpStatusCode.OK, $"Revoke policy failed: {revokePolicyMsg.StatusCode}");

        // 12. Evaluate policy with ClientVersion in range again -> unblocked
        var evalResponseMsg6 = await SendJsonAsync(client, HttpMethod.Post, "/api/v1/policies/evaluate", evalReq4);
        var evalRes6 = await evalResponseMsg6.Content.ReadFromJsonAsync<ApiResponse<PolicyEvaluateResponse>>();
        var payload6 = JsonSerializer.Deserialize<PolicyEvaluationResult>(evalRes6!.Data.Envelope.PayloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert(payload6 != null && !payload6.IsBlocked, "Device should be unblocked after policy revocation.");
    }
    finally
    {
        RestoreEnvironment(environment);
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Ignore clean up failures
        }
    }
}

sealed class TestServerFactory(string root, string password, string secret) : WebApplicationFactory<ServerApplication>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(FindServerContentRoot());
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:NetRelay"] = "Server=unused;Database=unused;User=unused;Password=unused",
                ["NetRelay:PublicBaseUrl"] = "https://netrelay.cn",
                ["NetRelay:GithubRepository"] = "netrelay/netrelay",
                ["NetRelay:ReleasesRoot"] = Path.Combine(root, "releases"),
                ["NetRelay:FeedbackRoot"] = Path.Combine(root, "feedback"),
                ["NetRelay:StagingRoot"] = Path.Combine(root, "staging"),
                ["NetRelay:QuarantineRoot"] = Path.Combine(root, "quarantine"),
                ["NetRelay:KeysRoot"] = Path.Combine(root, "keys_root"),
                ["NetRelay:AutoMigrate"] = "false",
                ["BootstrapAdmin:Username"] = "admin",
                ["BootstrapAdmin:Password"] = password,
                ["BootstrapAdmin:TotpSecret"] = secret,
                ["DataProtection:KeysPath"] = Path.Combine(root, "keys")
            });
        });
        builder.ConfigureServices(services =>
        {
            var databaseName = "http-" + Guid.NewGuid();
            services.RemoveAll<DbContextOptions<NetRelayDbContext>>();
            services.RemoveAll<NetRelayDbContext>();
            services.AddDbContext<NetRelayDbContext>(
                options => options.UseInMemoryDatabase(databaseName));
        });
    }

    private static string FindServerContentRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "server", "NetRelay.Server", "NetRelay.Server.csproj");
            if (File.Exists(candidate))
            {
                return Path.GetDirectoryName(candidate)!;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("NetRelay.Server project directory was not found.");
    }
}

sealed class InstallServerFactory : WebApplicationFactory<ServerApplication>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(FindServerContentRoot());
        builder.UseEnvironment("Testing");
    }

    private static string FindServerContentRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "server", "NetRelay.Server", "NetRelay.Server.csproj");
            if (File.Exists(candidate))
            {
                return Path.GetDirectoryName(candidate)!;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("NetRelay.Server project directory was not found.");
    }
}

