using System.IO;
using System.Security.Cryptography;
using System.Text;
using AuditPlatform.Client.Core.Auth;

namespace AuditPlatform.Client.Wpf;

/// <summary>
/// Refresh token on disk, encrypted with Windows DPAPI for the current user (ADR-0005): another Windows account
/// on the same laptop, or a copy of the file on another machine, cannot read it.
/// </summary>
internal sealed class DpapiRefreshTokenStore(string dataDirectory) : IRefreshTokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AuditPlatform/refresh-token/v1");
    private readonly string _path = Path.Combine(dataDirectory, "session.bin");

    public string? Load()
    {
        try
        {
            return File.Exists(_path)
                ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser))
                : null;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException)
        {
            return null;
        }
    }

    public void Save(string refreshToken)
    {
        var temp = _path + ".tmp";
        File.WriteAllBytes(temp, ProtectedData.Protect(Encoding.UTF8.GetBytes(refreshToken), Entropy, DataProtectionScope.CurrentUser));
        File.Move(temp, _path, overwrite: true);
    }

    public void Clear() => File.Delete(_path);
}
