using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace AuditPlatform.Master.Auth;

/// <summary>
/// Keys derived (HKDF-SHA256) from the single master key held on the VM. Separate keys per use, so that a weakness
/// in one use does not expose another. When no master key is configured the keys are random and
/// <see cref="IsConfigured"/> is false: nothing issued can be validated after a restart, and endpoints refuse to work.
/// </summary>
public sealed class AuthKeys
{
    public const int MinimumKeyBytes = 32;

    public AuthKeys(IOptions<AuthOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        byte[] master;
        var configured = options.Value.MasterKey;
        if (string.IsNullOrWhiteSpace(configured))
        {
            master = RandomNumberGenerator.GetBytes(MinimumKeyBytes);
        }
        else
        {
            master = Convert.FromBase64String(configured.Trim());
            if (master.Length < MinimumKeyBytes)
            {
                throw new InvalidOperationException($"Auth:MasterKey must be base64 of at least {MinimumKeyBytes} random bytes.");
            }

            IsConfigured = true;
        }

        Signing = Derive(master, "audit-platform/auth/jwt-signing/v1");
        Encryption = Derive(master, "audit-platform/auth/secret-encryption/v1");
        Hashing = Derive(master, "audit-platform/auth/token-hashing/v1");
        CryptographicOperations.ZeroMemory(master);
    }

    public bool IsConfigured { get; }

    internal byte[] Signing { get; }
    internal byte[] Encryption { get; }
    internal byte[] Hashing { get; }

    /// <summary>A URL-safe random token with 256 bits of entropy.</summary>
    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Keyed hash of a bearer secret for storage. The database never holds a usable token, and a leaked database
    /// cannot be searched for short secrets (recovery codes) without the master key.
    /// </summary>
    public string Hash(string purpose, string value)
    {
        var data = Encoding.UTF8.GetBytes(purpose + "\n" + value);
        return Convert.ToHexStringLower(HMACSHA256.HashData(Hashing, data));
    }

    /// <summary>AES-256-GCM. <paramref name="context"/> binds the ciphertext to its owner (for example a user id).</summary>
    public string Protect(ReadOnlySpan<byte> plaintext, string context)
    {
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        using var aes = new AesGcm(Encryption, tag.Length);
        aes.Encrypt(nonce, plaintext, cipher, tag, Encoding.UTF8.GetBytes(context));
        return "v1." + Base64Url([.. nonce, .. cipher, .. tag]);
    }

    /// <summary>Returns null when the value was not produced by this key for this context, or was altered.</summary>
    public byte[]? Unprotect(string? protectedValue, string context)
    {
        if (protectedValue is null || !protectedValue.StartsWith("v1.", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var raw = FromBase64Url(protectedValue[3..]);
            var nonceSize = AesGcm.NonceByteSizes.MaxSize;
            var tagSize = AesGcm.TagByteSizes.MaxSize;
            if (raw.Length < nonceSize + tagSize)
            {
                return null;
            }

            var plain = new byte[raw.Length - nonceSize - tagSize];
            using var aes = new AesGcm(Encryption, tagSize);
            aes.Decrypt(raw.AsSpan(0, nonceSize), raw.AsSpan(nonceSize, plain.Length), raw.AsSpan(nonceSize + plain.Length),
                plain, Encoding.UTF8.GetBytes(context));
            return plain;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    internal static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '='));
    }

    private static byte[] Derive(byte[] master, string info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, salt: null, info: Encoding.UTF8.GetBytes(info));
}
