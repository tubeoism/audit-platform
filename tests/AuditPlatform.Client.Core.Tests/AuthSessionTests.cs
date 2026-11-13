using System.Net;
using System.Net.Http.Json;
using AuditPlatform.Client.Core.Auth;
using AuditPlatform.Contracts.Auth;

namespace AuditPlatform.Client.Core.Tests;

public sealed class AuthSessionTests : IDisposable
{
    private sealed class MemoryStore : IRefreshTokenStore
    {
        public string? Token { get; set; }
        public string? Load() => Token;
        public void Save(string refreshToken) => Token = refreshToken;
        public void Clear() => Token = null;
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 13, 2, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Stands in for the master: answers each request with the next scripted reply.</summary>
    private sealed class Master : HttpMessageHandler
    {
        public List<(string Path, string Body, string? Authorization)> Requests { get; } = [];
        public Func<string, HttpResponseMessage> Reply { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests)
            {
                Requests.Add((request.RequestUri!.AbsolutePath, body, request.Headers.Authorization?.ToString()));
            }

            await Task.Delay(20, cancellationToken);
            return Reply(request.RequestUri.AbsolutePath);
        }
    }

    private static readonly UserInfo An = new(Guid.NewGuid(), "an@audit.io.vn", "An", "User");
    private readonly Master _master = new();
    private readonly MemoryStore _store = new();
    private readonly Clock _clock = new();
    private readonly ClientOptions _options = new() { MasterUrl = new Uri("https://master.test"), ClientId = Guid.NewGuid() };
    private readonly HttpClient _http;
    private readonly AuthSession _session;
    private int _issued;

    public AuthSessionTests()
    {
        _http = new HttpClient(_master);
        _session = new AuthSession(_http, _options, _store, _clock);
    }

    public void Dispose()
    {
        _session.Dispose();
        _http.Dispose();
        _master.Dispose();
    }

    private HttpResponseMessage Tokens()
    {
        var n = Interlocked.Increment(ref _issued);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new TokenResponse($"access-{n}", _clock.Now.AddMinutes(15), $"refresh-{n}",
                _clock.Now.AddDays(14), _clock.Now.AddDays(90), An, null)),
        };
    }

    private static HttpResponseMessage Refused(HttpStatusCode status, string code) =>
        new(status) { Content = JsonContent.Create(new AuthError(code, code)) };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sign_in_sends_the_installation_id_and_stores_the_refresh_token()
    {
        _master.Reply = _ => Tokens();
        var result = await _session.SignInAsync("an@audit.io.vn", "a-long-password-1", "123456", null, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(An, _session.User);
        Assert.Equal("refresh-1", _store.Token);
        Assert.True(_session.HasStoredSession);
        var (path, body, _) = Assert.Single(_master.Requests);
        Assert.Equal(AuthRoutes.Login, path);
        Assert.Contains(_options.ClientId.ToString(), body, StringComparison.Ordinal);
        Assert.Equal("access-1", await _session.GetAccessTokenAsync(Ct));
        Assert.Single(_master.Requests);
    }

    [Fact]
    public async Task Refused_sign_in_reports_the_masters_error_and_stores_nothing()
    {
        _master.Reply = _ => Refused(HttpStatusCode.Unauthorized, "invalid_credentials");
        var result = await _session.SignInAsync("an@audit.io.vn", "wrong", "123456", null, Ct);

        Assert.Equal("invalid_credentials", result.Error!.Error);
        Assert.Null(_store.Token);
        Assert.Null(await _session.GetAccessTokenAsync(Ct));
    }

    [Fact]
    public async Task Access_token_is_renewed_shortly_before_expiry_and_the_rotated_refresh_token_is_stored()
    {
        _master.Reply = _ => Tokens();
        await _session.SignInAsync("an@audit.io.vn", "a-long-password-1", "123456", null, Ct);

        _clock.Now = _clock.Now.AddMinutes(13);
        Assert.Equal("access-1", await _session.GetAccessTokenAsync(Ct));

        _clock.Now = _clock.Now.AddMinutes(1.5);
        Assert.Equal("access-2", await _session.GetAccessTokenAsync(Ct));
        Assert.Equal("refresh-2", _store.Token);
        Assert.Contains("refresh-1", _master.Requests[^1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_refresh_so_a_token_is_never_presented_twice()
    {
        _store.Token = "refresh-from-last-run";
        _master.Reply = _ => Tokens();

        var tokens = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => _session.GetAccessTokenAsync(Ct)));

        Assert.All(tokens, t => Assert.Equal("access-1", t));
        Assert.Single(_master.Requests);
    }

    [Fact]
    public async Task Offline_or_failing_master_keeps_the_stored_session()
    {
        _store.Token = "refresh-from-last-run";
        var signedOut = false;
        _session.SignedOut += (_, _) => signedOut = true;

        _master.Reply = _ => throw new HttpRequestException("no route to host");
        Assert.Null(await _session.GetAccessTokenAsync(Ct));
        _master.Reply = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);
        Assert.Null(await _session.GetAccessTokenAsync(Ct));
        _master.Reply = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        Assert.Null(await _session.GetAccessTokenAsync(Ct));

        Assert.Equal("refresh-from-last-run", _store.Token);
        Assert.False(signedOut);

        _master.Reply = _ => Tokens();
        Assert.Equal("access-1", await _session.GetAccessTokenAsync(Ct));
    }

    [Fact]
    public async Task Session_refused_by_the_master_is_forgotten_and_announced()
    {
        _store.Token = "revoked";
        var signedOut = false;
        _session.SignedOut += (_, _) => signedOut = true;
        _master.Reply = _ => Refused(HttpStatusCode.Unauthorized, "invalid_session");

        Assert.Null(await _session.GetAccessTokenAsync(Ct));
        Assert.Null(_store.Token);
        Assert.True(signedOut);
        Assert.False(_session.HasStoredSession);
    }

    [Fact]
    public async Task Sign_off_grant_is_requested_with_the_access_token_and_bound_to_the_working_paper()
    {
        _master.Reply = path => path == AuthRoutes.StepUp
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new StepUpResponse("grant-1", _clock.Now.AddMinutes(5))) }
            : Tokens();
        await _session.SignInAsync("an@audit.io.vn", "a-long-password-1", "123456", null, Ct);
        var paper = Guid.NewGuid();

        var grant = await _session.RequestSignOffGrantAsync(paper, "abc123", "654321", Ct);

        Assert.Equal("grant-1", grant.Value!.StepUpToken);
        var (path, body, authorization) = _master.Requests[^1];
        Assert.Equal(AuthRoutes.StepUp, path);
        Assert.Equal("Bearer access-1", authorization);
        Assert.Contains(paper.ToString(), body, StringComparison.Ordinal);
        Assert.Contains("abc123", body, StringComparison.Ordinal);

        _master.Reply = _ => Refused(HttpStatusCode.Forbidden, "code_invalid");
        Assert.Equal("code_invalid", (await _session.RequestSignOffGrantAsync(paper, "abc123", "000000", Ct)).Error!.Error);
        Assert.Equal("refresh-1", _store.Token);
    }

    [Fact]
    public async Task Sign_off_grant_is_unavailable_offline()
    {
        _store.Token = "refresh-from-last-run";
        _master.Reply = _ => throw new HttpRequestException("offline");

        var grant = await _session.RequestSignOffGrantAsync(Guid.NewGuid(), "abc123", "654321", Ct);
        Assert.Equal("master_unreachable", grant.Error!.Error);
    }

    [Fact]
    public async Task Sign_out_tells_the_master_and_forgets_the_session_even_when_offline()
    {
        _master.Reply = _ => Tokens();
        await _session.SignInAsync("an@audit.io.vn", "a-long-password-1", "123456", null, Ct);

        _master.Reply = _ => throw new HttpRequestException("offline");
        await _session.SignOutAsync(Ct);

        Assert.Equal(AuthRoutes.Logout, _master.Requests[^1].Path);
        Assert.Null(_store.Token);
        Assert.Null(_session.User);
        Assert.Null(await _session.GetAccessTokenAsync(Ct));
    }
}
