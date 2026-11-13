using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AuditPlatform.Master.Auth;

/// <summary>
/// Time-based one-time passwords, RFC 6238 with the parameters every authenticator app supports:
/// HMAC-SHA1, 30-second step, 6 digits.
/// </summary>
public static class Totp
{
    public const int StepSeconds = 30;
    public const int Digits = 6;
    public const int SecretBytes = 20;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    public static string Compute(ReadOnlySpan<byte> secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> mac = stackalloc byte[HMACSHA1.HashSizeInBytes];
#pragma warning disable CA5350 // HMAC-SHA1 is what RFC 6238 authenticator apps implement; HMAC is not affected by SHA-1 collisions.
        HMACSHA1.HashData(secret, counter, mac);
#pragma warning restore CA5350
        var offset = mac[^1] & 0x0F;
        var binary = BinaryPrimitives.ReadInt32BigEndian(mac.Slice(offset, 4)) & 0x7FFFFFFF;
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Accepts the current step and one step either side (clock drift). Returns the matched step, which the caller
    /// stores so that the same code cannot be used twice; null when the code does not match.
    /// </summary>
    public static long? Verify(ReadOnlySpan<byte> secret, string? code, DateTimeOffset now)
    {
        var normalized = code?.Replace(" ", string.Empty, StringComparison.Ordinal).Trim();
        if (normalized is not { Length: Digits } || !normalized.All(char.IsAsciiDigit))
        {
            return null;
        }

        var current = StepAt(now);
        long? matched = null;
        var candidate = Encoding.ASCII.GetBytes(normalized);
        foreach (var step in (long[])[current - 1, current, current + 1])
        {
            // Compare every candidate in constant time; do not stop at the first match.
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Compute(secret, step)), candidate))
            {
                matched ??= step;
            }
        }

        return matched;
    }

    /// <summary>Key URI understood by Microsoft Authenticator, Google Authenticator and similar apps.</summary>
    public static string OtpAuthUri(string issuer, string account, ReadOnlySpan<byte> secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}" +
        $"?secret={Base32.Encode(secret)}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
}

/// <summary>RFC 4648 Base32 without padding.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(((data.Length * 8) + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return sb.ToString();
    }
}
