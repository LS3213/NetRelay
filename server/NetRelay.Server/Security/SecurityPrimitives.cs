using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;

namespace NetRelay.Server.Security;

public sealed class AdminPasswordService
{
    private readonly PasswordHasher<object> _hasher = new();
    private readonly object _subject = new();
    private readonly string _dummyHash;

    public AdminPasswordService()
    {
        _dummyHash = _hasher.HashPassword(_subject, TokenService.CreateToken());
    }

    public string Hash(string password) => _hasher.HashPassword(_subject, password);

    public bool Verify(string hash, string password) =>
        _hasher.VerifyHashedPassword(_subject, hash, password) != PasswordVerificationResult.Failed;

    public void VerifyDummy(string password) => _hasher.VerifyHashedPassword(_subject, _dummyHash, password);
}

public static class TokenService
{
    public static string CreateToken(int byteCount = 32) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool FixedTimeEquals(string expectedHash, string token)
    {
        var actualHash = Hash(token);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expectedHash),
            Encoding.ASCII.GetBytes(actualHash));
    }
}

public static class TotpService
{
    private const int PeriodSeconds = 30;

    public static bool Verify(string base32Secret, string code, DateTimeOffset now)
    {
        if (code.Length != 6 || !code.All(char.IsAsciiDigit))
        {
            return false;
        }

        var expected = int.Parse(code, CultureInfo.InvariantCulture);
        var secret = DecodeBase32(base32Secret);
        var counter = now.ToUnixTimeSeconds() / PeriodSeconds;

        for (var offset = -1; offset <= 1; offset++)
        {
            if (Generate(secret, counter + offset) == expected)
            {
                return true;
            }
        }

        return false;
    }

    public static string GenerateCode(string base32Secret, DateTimeOffset now) =>
        Generate(DecodeBase32(base32Secret), now.ToUnixTimeSeconds() / PeriodSeconds)
            .ToString("D6", CultureInfo.InvariantCulture);

    public static bool IsValidSecret(string base32Secret)
    {
        try
        {
            return DecodeBase32(base32Secret).Length >= 10;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static int Generate(byte[] secret, long counter)
    {
        Span<byte> counterBytes = stackalloc byte[8];
        for (var index = 7; index >= 0; index--)
        {
            counterBytes[index] = (byte)(counter & 0xff);
            counter >>= 8;
        }

        using var hmac = new HMACSHA1(secret);
        var hash = hmac.ComputeHash(counterBytes.ToArray());
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) |
                     ((hash[offset + 1] & 0xff) << 16) |
                     ((hash[offset + 2] & 0xff) << 8) |
                     (hash[offset + 3] & 0xff);
        return binary % 1_000_000;
    }

    private static byte[] DecodeBase32(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var normalized = value.Trim().TrimEnd('=').Replace(" ", string.Empty).ToUpperInvariant();
        var output = new List<byte>();
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var character in normalized)
        {
            var index = alphabet.IndexOf(character);
            if (index < 0)
            {
                throw new FormatException("TOTP secret is not valid Base32.");
            }

            buffer = (buffer << 5) | index;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                output.Add((byte)(buffer >> bitsLeft));
                buffer &= (1 << bitsLeft) - 1;
            }
        }

        return output.ToArray();
    }
}
