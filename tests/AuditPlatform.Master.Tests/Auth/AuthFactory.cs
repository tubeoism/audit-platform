using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using AuditPlatform.Contracts.Auth;
using AuditPlatform.Master.Auth;
using AuditPlatform.Master.Data;
using AuditPlatform.Master.Data.Entities;
using AuditPlatform.Master.Data.Tests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AuditPlatform.Master.Tests.Auth;

/// <summary>A clock the tests move by hand: token lifetimes, lockout and TOTP steps are all read from it.</summary>
public sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 13, 2, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

public sealed class FakeMail : IEmailSender
{
    public ConcurrentQueue<EmailMessage> Sent { get; } = new();
    public bool Fail { get; set; }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new EmailDeliveryException("Brevo could not be reached.");
        }

        Sent.Enqueue(message);
        return Task.CompletedTask;
    }

    /// <summary>The one-time token from the link in the newest message to this address.</summary>
    public string TokenFor(string email)
    {
        var message = Sent.Last(m => m.ToAddress == email);
        return Regex.Match(message.TextBody, @"\?token=([A-Za-z0-9_-]+)").Groups[1].Value;
    }
}

public sealed record TestUser(Guid Id, string Email, string Password, byte[] Secret, IReadOnlyList<string> RecoveryCodes);

/// <summary>
/// The real master (Program) on a throw-away PostgreSQL database with all migrations applied. Only the clock, the
/// mail sender and the breached-password lookup are replaced.
/// </summary>
public sealed class AuthFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Password = "ghe-da-xanh-luot-song-42";
    private readonly PostgresFixture _pg = new();
    private int _counter;

    public TestClock Clock { get; } = new();
    public FakeMail Mail { get; } = new();
    public string? MasterKey { get; init; } = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    public async ValueTask InitializeAsync() => await _pg.InitializeAsync();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _pg.DisposeAsync();
    }

    /// <summary>Skips the calling test when no PostgreSQL server is configured (AUDIT_TEST_PG).</summary>
    public void RequireDatabase()
    {
        if (_pg.ConnectionString is null)
        {
            Assert.Skip($"Set {PostgresFixture.Variable} to a PostgreSQL admin connection string to run authentication tests.");
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Master", _pg.ConnectionString ?? "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none");
        builder.UseSetting("Auth:MasterKey", MasterKey);
        builder.UseSetting("Auth:PublicBaseUrl", "https://master.test/");
        builder.UseSetting("Auth:RateLimitPerMinute", "100000");
        builder.UseSetting("Auth:UseHibp", "false");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Mail);
        });
    }

    public async Task<T> ScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    public Task<List<AuditLogRow>> AuditAsync() => ScopeAsync(sp =>
        Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            sp.GetRequiredService<MasterDbContext>().AuditLog.OrderBy(a => a.Sequence)));

    /// <summary>The next unused TOTP code: moves the clock one step on, as a real user waits for a new code.</summary>
    public string NextCode(byte[] secret)
    {
        Clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));
        return Totp.Compute(secret, Totp.StepAt(Clock.GetUtcNow()));
    }

    /// <summary>Invites a user from the console and walks the browser pages exactly as the user would.</summary>
    public async Task<TestUser> EnrolAsync(SystemRole role = SystemRole.User, string? email = null)
    {
        email ??= $"user{Interlocked.Increment(ref _counter)}-{Guid.NewGuid():N}"[..20] + "@audit.io.vn";
        var invited = await ScopeAsync(sp => sp.GetRequiredService<AccountService>()
            .InviteAsync(Actor.Console("test"), email, "Test User", role, CancellationToken.None));
        Assert.True(invited.Succeeded, invited.Error?.Message);

        using var browser = CreateClient();
        var token = Mail.TokenFor(email);
        var (secret, ticket) = await SubmitPasswordAsync(browser, token, Password);
        var codes = await ConfirmAsync(browser, token, ticket, Totp.Compute(secret, Totp.StepAt(Clock.GetUtcNow())));
        return new TestUser(invited.Value!.UserId, email, Password, secret, codes);
    }

    public static async Task<(byte[] Secret, string Ticket)> SubmitPasswordAsync(HttpClient browser, string token, string password)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = token, ["password"] = password, ["confirm"] = password,
        });
        using var response = await browser.PostAsync(new Uri("/account/enrol", UriKind.Relative), form);
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode && html.Contains("name=\"ticket\"", StringComparison.Ordinal), html);
        var base32 = Regex.Match(html, "<p class=\"mono\">([A-Z2-7 ]+)</p>").Groups[1].Value.Replace(" ", "", StringComparison.Ordinal);
        var ticket = Regex.Match(html, "name=\"ticket\" value=\"([^\"]+)\"").Groups[1].Value;
        return (FromBase32(base32), System.Net.WebUtility.HtmlDecode(ticket));
    }

    public static async Task<IReadOnlyList<string>> ConfirmAsync(HttpClient browser, string token, string ticket, string code)
    {
        var html = await ConfirmRawAsync(browser, token, ticket, code);
        var codes = Regex.Matches(html, "<li class=\"mono\">([A-Z0-9-]+)</li>").Select(m => m.Groups[1].Value).ToList();
        Assert.True(codes.Count > 0, html);
        return codes;
    }

    public static async Task<string> ConfirmRawAsync(HttpClient browser, string token, string ticket, string code)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token, ["ticket"] = ticket, ["code"] = code });
        using var response = await browser.PostAsync(new Uri("/account/enrol/confirm", UriKind.Relative), form);
        return await response.Content.ReadAsStringAsync();
    }

    public async Task<TokenResponse> SignInAsync(HttpClient client, TestUser user, Guid? installationId = null)
    {
        using var response = await LoginAsync(client, user.Email, user.Password, NextCode(user.Secret), null, installationId);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    public static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password, string? code,
        string? recoveryCode = null, Guid? installationId = null) =>
        client.PostAsJsonAsync(AuthRoutes.Login, new LoginRequest(email, password, code, recoveryCode, installationId ?? Guid.NewGuid(), "LAPTOP-TEST"));

    public static HttpRequestMessage Authorized(HttpMethod method, string path, string accessToken, object? body = null)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new("Bearer", accessToken);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private static byte[] FromBase32(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. bytes];
    }
}
