using System.Security.Cryptography;

namespace NetRelay.Server.Infrastructure;

public static class Uuid7
{
    public static Guid Create(DateTimeOffset? timestamp = null)
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);

        var unixMilliseconds = (timestamp ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
        bytes[0] = (byte)(unixMilliseconds >> 40);
        bytes[1] = (byte)(unixMilliseconds >> 32);
        bytes[2] = (byte)(unixMilliseconds >> 24);
        bytes[3] = (byte)(unixMilliseconds >> 16);
        bytes[4] = (byte)(unixMilliseconds >> 8);
        bytes[5] = (byte)unixMilliseconds;
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x70);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);

        return new Guid(bytes, bigEndian: true);
    }
}
