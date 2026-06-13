using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NetRelay.Contracts;
using NetRelay.Server;
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
    ("Admin HTTP authentication workflow enforces session and CSRF boundaries", TestAdminHttpWorkflowAsync)
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
        QuarantineRoot = Path.Combine(root, "quarantine")
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
