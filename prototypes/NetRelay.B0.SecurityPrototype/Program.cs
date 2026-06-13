using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var failures = new List<string>();

await RunAsync("Signed envelope validation", VerifySignedEnvelopeAsync);
await RunAsync("Root and operational key hierarchy", VerifyKeyHierarchyAsync);
await RunAsync("Bootstrap client configuration validation", VerifyBootstrapConfigurationAsync);
await RunAsync("Update artifact consistency and rollback", VerifyUpdatePrototypeAsync);
await RunAsync("Layered fingerprint matching invariants", VerifyFingerprintMatchingAsync);

Console.WriteLine(failures.Count == 0
    ? "B0 security prototype checks passed."
    : $"B0 security prototype checks failed: {failures.Count}");

return failures.Count == 0 ? 0 : 1;

async Task RunAsync(string name, Func<Task> check)
{
    try
    {
        await check();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{name}: {exception.Message}");
        Console.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

static Task VerifySignedEnvelopeAsync()
{
    using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var replayCache = new HashSet<string>(StringComparer.Ordinal);
    var now = DateTimeOffset.UtcNow;
    var payload = new SortedDictionary<string, object?>(StringComparer.Ordinal)
    {
        ["channel"] = "stable",
        ["sha256"] = new string('a', 64),
        ["version"] = "1.2.3"
    };

    var envelope = SignedEnvelope.Create("update-manifest", "nonce-1", now, now.AddMinutes(5), payload, signingKey);
    Require(envelope.Verify("update-manifest", "nonce-1", now, signingKey, replayCache), "Valid envelope was rejected.");
    Require(!envelope.Verify("update-manifest", "nonce-1", now, signingKey, replayCache), "Replay was accepted.");
    Require(!envelope.Verify("update-manifest", "nonce-2", now, signingKey, new HashSet<string>()), "Wrong nonce was accepted.");
    Require(!envelope.Verify("block-policy", "nonce-1", now, signingKey, new HashSet<string>()), "Wrong purpose was accepted.");
    Require(!envelope.Verify("update-manifest", "nonce-1", now, otherKey, new HashSet<string>()), "Wrong key was accepted.");
    Require(!envelope.Verify("update-manifest", "nonce-1", now.AddMinutes(10), signingKey, new HashSet<string>()), "Expired envelope was accepted.");

    var tampered = envelope with { PayloadJson = envelope.PayloadJson.Replace("1.2.3", "9.9.9", StringComparison.Ordinal) };
    Require(!tampered.Verify("update-manifest", "nonce-1", now, signingKey, new HashSet<string>()), "Tampered payload was accepted.");
    return Task.CompletedTask;
}

static Task VerifyKeyHierarchyAsync()
{
    using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var operationalKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var now = DateTimeOffset.UtcNow;

    var certificate = OperationalKeyCertificate.Create(
        "operations-2026-06",
        operationalKey.ExportSubjectPublicKeyInfo(),
        ["connectivity-challenge", "client-configuration"],
        now.AddDays(-1),
        now.AddDays(30),
        rootKey);

    Require(certificate.Verify(now, rootKey, "connectivity-challenge"), "Valid operational certificate was rejected.");
    Require(!certificate.Verify(now, rootKey, "update-manifest"), "Operational key was allowed to sign an update manifest.");
    Require(!certificate.Verify(now.AddDays(31), rootKey, "connectivity-challenge"), "Expired operational certificate was accepted.");

    using var wrongRoot = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    Require(!certificate.Verify(now, wrongRoot, "connectivity-challenge"), "Certificate signed by an untrusted root was accepted.");
    return Task.CompletedTask;
}

static Task VerifyBootstrapConfigurationAsync()
{
    Require(BootstrapConfigurationValidator.IsValid(new BootstrapConfiguration(
        "https://api.netrelay.app/api/v1",
        "owner/netrelay",
        "stable")), "Valid bootstrap configuration was rejected.");
    Require(!BootstrapConfigurationValidator.IsValid(new BootstrapConfiguration(
        "http://netrelay.example.com/api/v1",
        "owner/netrelay",
        "stable")), "Non-HTTPS primary API was accepted.");
    Require(!BootstrapConfigurationValidator.IsValid(new BootstrapConfiguration(
        "https://netrelay.example/api/v1",
        "owner/repository",
        "stable")), "Placeholder bootstrap configuration was accepted.");
    Require(!BootstrapConfigurationValidator.IsValid(new BootstrapConfiguration(
        "https://api.netrelay.app/api/v1",
        "invalid-repository",
        "stable")), "Invalid GitHub repository was accepted.");
    return Task.CompletedTask;
}

static async Task VerifyUpdatePrototypeAsync()
{
    var root = Path.Combine(Path.GetTempPath(), $"NetRelay-B0-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);

    try
    {
        var primary = Path.Combine(root, "primary.zip");
        var fallback = Path.Combine(root, "fallback.zip");
        var damaged = Path.Combine(root, "damaged.zip");
        await File.WriteAllTextAsync(primary, "same signed release bytes", Encoding.UTF8);
        File.Copy(primary, fallback);
        await File.WriteAllTextAsync(damaged, "tampered release bytes", Encoding.UTF8);

        var expectedHash = await HashFileAsync(primary);
        Require(await HashFileAsync(fallback) == expectedHash, "Primary and fallback artifacts differ.");
        Require(await HashFileAsync(damaged) != expectedHash, "Damaged update artifact was not detected.");

        var install = Path.Combine(root, "install");
        var staging = Path.Combine(root, "staging");
        var backup = Path.Combine(root, "backup");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(install, "NetRelay.exe"), "old");
        await File.WriteAllTextAsync(Path.Combine(staging, "NetRelay.exe"), "new");

        Directory.Move(install, backup);
        Directory.Move(staging, install);
        Directory.Delete(install, recursive: true);
        Directory.Move(backup, install);

        Require(await File.ReadAllTextAsync(Path.Combine(install, "NetRelay.exe")) == "old",
            "Rollback did not restore the previous installation.");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static Task VerifyFingerprintMatchingAsync()
{
    var baseline = FingerprintSample.Create(
        core: ["systemUuid", "motherboard", "machineGuid", "processor", "systemDrive"],
        network: ["adapterGuid-a", "mac-a", "model-a"]);

    var sameCoreNewNetwork = FingerprintSample.Create(
        core: ["systemUuid", "motherboard", "machineGuid", "processor", "systemDrive"],
        network: ["adapterGuid-b", "mac-b", "model-b"]);

    var sameNetworkDifferentCore = FingerprintSample.Create(
        core: ["other-system", "other-board", "other-machine", "other-processor", "other-drive"],
        network: ["adapterGuid-a", "mac-a", "model-a"]);

    var oneCoreAndSameNetwork = FingerprintSample.Create(
        core: ["systemUuid", "other-board", "other-machine", "other-processor", "other-drive"],
        network: ["adapterGuid-a", "mac-a", "model-a"]);

    Require(FingerprintMatcher.IsLikelySameDevice(baseline, sameCoreNewNetwork),
        "Replacing all network adapters incorrectly created a new device.");
    Require(!FingerprintMatcher.IsLikelySameDevice(baseline, sameNetworkDifferentCore),
        "Matching network evidence alone incorrectly matched a device.");
    Require(!FingerprintMatcher.IsLikelySameDevice(baseline, oneCoreAndSameNetwork),
        "One core match plus network evidence incorrectly matched a device.");
    return Task.CompletedTask;
}

static async Task<string> HashFileAsync(string path)
{
    await using var stream = File.OpenRead(path);
    return Convert.ToHexString(await SHA256.HashDataAsync(stream));
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed record SignedEnvelope(
    string Purpose,
    string Nonce,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string PayloadJson,
    string Signature)
{
    public static SignedEnvelope Create(
        string purpose,
        string nonce,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        IReadOnlyDictionary<string, object?> payload,
        ECDsa signingKey)
    {
        var payloadJson = CanonicalJson.Serialize(payload);
        var unsigned = CreateUnsignedBytes(purpose, nonce, issuedAt, expiresAt, payloadJson);
        return new SignedEnvelope(
            purpose,
            nonce,
            issuedAt,
            expiresAt,
            payloadJson,
            Convert.ToBase64String(signingKey.SignData(unsigned, HashAlgorithmName.SHA256)));
    }

    public bool Verify(
        string expectedPurpose,
        string expectedNonce,
        DateTimeOffset now,
        ECDsa verificationKey,
        ISet<string> replayCache)
    {
        if (!string.Equals(Purpose, expectedPurpose, StringComparison.Ordinal)
            || !string.Equals(Nonce, expectedNonce, StringComparison.Ordinal)
            || IssuedAt > now.AddMinutes(1)
            || ExpiresAt <= now
            || ExpiresAt <= IssuedAt
            || replayCache.Contains(Nonce))
        {
            return false;
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(Signature);
        }
        catch (FormatException)
        {
            return false;
        }

        var valid = verificationKey.VerifyData(
            CreateUnsignedBytes(Purpose, Nonce, IssuedAt, ExpiresAt, PayloadJson),
            signature,
            HashAlgorithmName.SHA256);

        if (valid)
        {
            replayCache.Add(Nonce);
        }

        return valid;
    }

    private static byte[] CreateUnsignedBytes(
        string purpose,
        string nonce,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        string payloadJson) =>
        Encoding.UTF8.GetBytes(
            $"{purpose}\n{nonce}\n{issuedAt.ToUniversalTime():O}\n{expiresAt.ToUniversalTime():O}\n{payloadJson}");
}

internal static class CanonicalJson
{
    public static string Serialize(IReadOnlyDictionary<string, object?> values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(pair.Key);
                JsonSerializer.Serialize(writer, pair.Value, pair.Value?.GetType() ?? typeof(object));
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

internal sealed record FingerprintSample(
    IReadOnlySet<string> Core,
    IReadOnlySet<string> Network)
{
    public static FingerprintSample Create(IEnumerable<string> core, IEnumerable<string> network) =>
        new(core.ToHashSet(StringComparer.Ordinal), network.ToHashSet(StringComparer.Ordinal));
}

internal static class FingerprintMatcher
{
    public static bool IsLikelySameDevice(FingerprintSample existing, FingerprintSample candidate)
    {
        var coreMatches = existing.Core.Intersect(candidate.Core, StringComparer.Ordinal).Count();
        var networkMatches = existing.Network.Intersect(candidate.Network, StringComparer.Ordinal).Count();

        // Prototype safety invariant, not the final production threshold.
        return coreMatches >= 2 && (coreMatches * 20) + (networkMatches * 2) >= 40;
    }
}

internal sealed record OperationalKeyCertificate(
    string KeyId,
    string PublicKey,
    IReadOnlyList<string> AllowedPurposes,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    string RootSignature)
{
    public static OperationalKeyCertificate Create(
        string keyId,
        byte[] publicKey,
        IReadOnlyList<string> allowedPurposes,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter,
        ECDsa rootKey)
    {
        var normalizedPurposes = allowedPurposes.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var publicKeyBase64 = Convert.ToBase64String(publicKey);
        var unsigned = GetUnsignedBytes(keyId, publicKeyBase64, normalizedPurposes, notBefore, notAfter);
        return new OperationalKeyCertificate(
            keyId,
            publicKeyBase64,
            normalizedPurposes,
            notBefore,
            notAfter,
            Convert.ToBase64String(rootKey.SignData(unsigned, HashAlgorithmName.SHA256)));
    }

    public bool Verify(DateTimeOffset now, ECDsa rootKey, string requiredPurpose)
    {
        if (now < NotBefore || now >= NotAfter || !AllowedPurposes.Contains(requiredPurpose, StringComparer.Ordinal))
        {
            return false;
        }

        return rootKey.VerifyData(
            GetUnsignedBytes(KeyId, PublicKey, AllowedPurposes, NotBefore, NotAfter),
            Convert.FromBase64String(RootSignature),
            HashAlgorithmName.SHA256);
    }

    private static byte[] GetUnsignedBytes(
        string keyId,
        string publicKey,
        IEnumerable<string> purposes,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter) =>
        Encoding.UTF8.GetBytes(
            $"{keyId}\n{publicKey}\n{string.Join(',', purposes)}\n{notBefore.ToUniversalTime():O}\n{notAfter.ToUniversalTime():O}");
}

internal sealed record BootstrapConfiguration(
    string PrimaryApiBaseUrl,
    string GithubRepository,
    string UpdateChannel);

internal static class BootstrapConfigurationValidator
{
    public static bool IsValid(BootstrapConfiguration configuration)
    {
        if (!Uri.TryCreate(configuration.PrimaryApiBaseUrl, UriKind.Absolute, out var primaryUri)
            || primaryUri.Scheme != Uri.UriSchemeHttps
            || primaryUri.Host.EndsWith(".example", StringComparison.OrdinalIgnoreCase)
            || primaryUri.Host.EndsWith(".example.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(configuration.GithubRepository, "owner/repository", StringComparison.OrdinalIgnoreCase)
            || configuration.GithubRepository.Count(character => character == '/') != 1
            || configuration.GithubRepository.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var repositoryParts = configuration.GithubRepository.Split('/');
        return repositoryParts.All(part => part.Length > 0)
            && string.Equals(configuration.UpdateChannel, "stable", StringComparison.Ordinal);
    }
}
