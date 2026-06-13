using Microsoft.EntityFrameworkCore;
using NetRelay.Server.Configuration;
using NetRelay.Server.Data;
using NetRelay.Server.Infrastructure;
using NetRelay.Server.Security;

var tests = new (string Name, Action Run)[]
{
    ("Server options accept valid production-shaped configuration", TestValidOptions),
    ("Server options reject insecure public URL", TestInvalidOptions),
    ("Server options reject placeholder deployment values", TestPlaceholderOptions),
    ("Password hashing verifies only the original password", TestPasswordHashing),
    ("TOTP validates an RFC 6238-compatible code", TestTotp),
    ("TOTP rejects invalid bootstrap secrets", TestInvalidTotpSecret),
    ("Tokens hash deterministically without storing plaintext", TestTokens),
    ("UUID v7 contains version, variant, and sortable timestamp", TestUuid7),
    ("EF model enforces the B1 single-admin foundations", TestEfModel)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
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
    var options = new ServerOptions
    {
        PublicBaseUrl = "https://netrelay.cn",
        GithubRepository = "netrelay/netrelay"
    };
    Assert(ServerOptionsValidator.Validate(options) is null, "Valid options were rejected.");
}

static void TestInvalidOptions()
{
    var options = new ServerOptions
    {
        PublicBaseUrl = "http://netrelay.example",
        GithubRepository = "../repository"
    };
    Assert(ServerOptionsValidator.Validate(options) is not null, "Invalid options were accepted.");
}

static void TestPlaceholderOptions()
{
    var options = new ServerOptions
    {
        PublicBaseUrl = "https://localhost",
        GithubRepository = "owner/repository"
    };
    Assert(ServerOptionsValidator.Validate(options) is not null, "Placeholder deployment options were accepted.");
}

static void TestPasswordHashing()
{
    var service = new AdminPasswordService();
    var hash = service.Hash("correct horse battery staple");
    Assert(!hash.Contains("correct horse", StringComparison.Ordinal), "Password hash contains plaintext.");
    Assert(service.Verify(hash, "correct horse battery staple"), "Correct password was rejected.");
    Assert(!service.Verify(hash, "incorrect"), "Incorrect password was accepted.");
}

static void TestTotp()
{
    const string rfcSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
    var timestamp = DateTimeOffset.FromUnixTimeSeconds(59);
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
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
