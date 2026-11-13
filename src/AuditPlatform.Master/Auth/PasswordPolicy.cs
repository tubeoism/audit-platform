using System.Collections.Frozen;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace AuditPlatform.Master.Auth;

public interface IBreachedPasswordChecker
{
    /// <summary>True when the password is known from a breach or a common-password list.</summary>
    ValueTask<bool> IsBreachedAsync(string password, CancellationToken cancellationToken);
}

/// <summary>
/// Bundled list of common passwords of 12 characters or more (from SecLists, MIT licence), compared case-insensitively.
/// Works without network access; the fallback when haveibeenpwned.com cannot be reached.
/// </summary>
public sealed class LocalPasswordList
{
    private readonly Lazy<FrozenSet<string>> _passwords = new(Load);

    public bool Contains(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return _passwords.Value.Contains(password.ToLowerInvariant());
    }

    private static FrozenSet<string> Load()
    {
        var assembly = typeof(LocalPasswordList).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("common-passwords.txt.gz", StringComparison.Ordinal));
        using var gzip = new GZipStream(assembly.GetManifestResourceStream(name)!, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        var set = new HashSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0)
            {
                set.Add(line);
            }
        }

        return set.ToFrozenSet(StringComparer.Ordinal);
    }
}

/// <summary>
/// haveibeenpwned.com range API: only the first five hex characters of the SHA-1 are sent, never the password or its
/// full hash. Any failure (timeout, network policy, outage) falls back to the bundled list rather than blocking the user.
/// </summary>
public sealed partial class HibpPasswordChecker(
    HttpClient http, LocalPasswordList local, IOptions<AuthOptions> options, ILogger<HibpPasswordChecker> logger)
    : IBreachedPasswordChecker
{
    public async ValueTask<bool> IsBreachedAsync(string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (local.Contains(password))
        {
            return true;
        }

        if (!options.Value.UseHibp)
        {
            return false;
        }

#pragma warning disable CA5350 // SHA-1 is the lookup key defined by the haveibeenpwned API, not a security control here.
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password)));
#pragma warning restore CA5350
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://api.pwnedpasswords.com/range/{hash[..5]}"));
            request.Headers.Add("Add-Padding", "true");
            using var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            foreach (var line in body.AsSpan().EnumerateLines())
            {
                // SUFFIX:COUNT; padded entries have count 0.
                var colon = line.IndexOf(':');
                if (colon == 35 && line[..35].Equals(hash.AsSpan(5), StringComparison.OrdinalIgnoreCase))
                {
                    return !line[(colon + 1)..].Trim().SequenceEqual("0");
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogHibpUnavailable(logger, ex.GetType().Name);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Breached-password service unavailable ({Reason}); bundled list used instead.")]
    private static partial void LogHibpUnavailable(ILogger logger, string reason);
}

public sealed class PasswordPolicy(IBreachedPasswordChecker breached, IOptions<AuthOptions> options)
{
    /// <summary>Returns null when the password is acceptable, otherwise the reason shown to the user.</summary>
    public async ValueTask<PasswordProblem?> CheckAsync(string? password, string email, CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (password is null || password.Length < o.PasswordMinLength)
        {
            return PasswordProblem.TooShort;
        }

        if (password.Length > o.PasswordMaxLength)
        {
            return PasswordProblem.TooLong;
        }

        var local = email.Split('@')[0];
        if (password.Distinct().Count() < 5
            || (local.Length >= 4 && password.Contains(local, StringComparison.OrdinalIgnoreCase)))
        {
            return PasswordProblem.TooPredictable;
        }

        return await breached.IsBreachedAsync(password, cancellationToken) ? PasswordProblem.Breached : null;
    }
}

public enum PasswordProblem
{
    TooShort,
    TooLong,
    TooPredictable,
    Breached,
}
