using System.Net;
using System.Net.Http.Json;
using AuditPlatform.Contracts.Auth;
using AuditPlatform.Contracts.Sync;

namespace AuditPlatform.Client.Core.Auth;

/// <summary>
/// Keeps the refresh token between runs. The Windows client encrypts it with DPAPI for the current user
/// (ADR-0005); the token never leaves the laptop except to the master's refresh endpoint.
/// </summary>
public interface IRefreshTokenStore
{
    string? Load();
    void Save(string refreshToken);
    void Clear();
}

/// <summary>Outcome of a call that the master may refuse; <see cref="Error"/> is null on success.</summary>
public sealed record AuthResult<T>(T? Value, AuthError? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// The client's signed-in state. Signs in with password and TOTP, hands the sync transport a valid access token,
/// rotates the refresh token, and asks for the sign-off step-up grant.
/// Offline, the cached session stays in place: nothing is cleared until the master itself refuses the token.
/// </summary>
public sealed class AuthSession(HttpClient http, ClientOptions options, IRefreshTokenStore store, TimeProvider clock) : IDisposable
{
    private static readonly TimeSpan RenewBefore = TimeSpan.FromSeconds(60);
    private static readonly AuthError Unreachable = new("master_unreachable",
        "Không kết nối được máy chủ. / The master could not be reached.");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt;

    /// <summary>Raised when the master refuses the stored session (revoked, expired, user disabled): a full sign-in is needed.</summary>
    public event EventHandler? SignedOut;

    public UserInfo? User { get; private set; }

    /// <summary>True when a session is stored on this laptop, whether or not the master is reachable right now.</summary>
    public bool HasStoredSession => store.Load() is not null;

    public async Task<AuthResult<UserInfo>> SignInAsync(
        string email, string password, string? code, string? recoveryCode, CancellationToken cancellationToken)
    {
        var request = new LoginRequest(email, password, code, recoveryCode, options.ClientId, Environment.MachineName);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var result = await PostAsync<TokenResponse>(AuthRoutes.Login, request, null, cancellationToken);
            if (result.Value is { } tokens)
            {
                Accept(tokens);
                return new AuthResult<UserInfo>(tokens.User, null);
            }

            return new AuthResult<UserInfo>(null, result.Error);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A valid access token, refreshed when it is about to expire; null when there is no session or the master is
    /// unreachable. Calls are serialised so that one refresh token is never presented twice.
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_accessToken is not null && clock.GetUtcNow() < _accessTokenExpiresAt - RenewBefore)
            {
                return _accessToken;
            }

            if (store.Load() is not { } refreshToken)
            {
                return null;
            }

            var result = await PostAsync<TokenResponse>(AuthRoutes.Refresh, new RefreshRequest(refreshToken, options.ClientId), null, cancellationToken);
            if (result.Value is { } tokens)
            {
                Accept(tokens);
                return _accessToken;
            }

            if (result.Error != Unreachable)
            {
                Forget();
                SignedOut?.Invoke(this, EventArgs.Empty);
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Sign-off step-up: exchanges a fresh TOTP code for a single-use grant bound to this working paper and content
    /// hash. Needs the master, so a working paper cannot be signed off while offline.
    /// </summary>
    public async Task<AuthResult<StepUpResponse>> RequestSignOffGrantAsync(
        Guid workingPaperId, string contentHash, string code, CancellationToken cancellationToken)
    {
        if (await GetAccessTokenAsync(cancellationToken) is not { } accessToken)
        {
            return new AuthResult<StepUpResponse>(null, Unreachable);
        }

        return await PostAsync<StepUpResponse>(AuthRoutes.StepUp, new StepUpRequest(code, workingPaperId, contentHash), accessToken, cancellationToken);
    }

    /// <summary>
    /// Signs a working paper off at one review level: buys the step-up grant with a fresh TOTP code for the content
    /// the reviewer is looking at (<paramref name="contentHash"/>, from the local replica), then asks the master to
    /// seal it. The master refuses if it holds different content, so what is sealed is what the reviewer saw.
    /// </summary>
    public async Task<AuthResult<SignOffResponse>> SignOffAsync(
        Guid engagementId, Guid workingPaperId, string level, string contentHash, string code, CancellationToken cancellationToken)
    {
        var grant = await RequestSignOffGrantAsync(workingPaperId, contentHash, code, cancellationToken);
        if (grant.Value is not { } stepUp)
        {
            return new AuthResult<SignOffResponse>(null, grant.Error ?? Unreachable);
        }

        if (await GetAccessTokenAsync(cancellationToken) is not { } accessToken)
        {
            return new AuthResult<SignOffResponse>(null, Unreachable);
        }

        return await PostAsync<SignOffResponse>(SyncRoutes.SignOff,
            new SignOffRequest(engagementId, workingPaperId, level, stepUp.StepUpToken), accessToken, cancellationToken);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (store.Load() is { } refreshToken)
            {
                // Best effort: the local session is forgotten even when the master cannot be told.
                await PostAsync<object>(AuthRoutes.Logout, new LogoutRequest(refreshToken), null, cancellationToken);
            }

            Forget();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private void Accept(TokenResponse tokens)
    {
        // Persist the rotated refresh token before anything else uses the new access token.
        store.Save(tokens.RefreshToken);
        _accessToken = tokens.AccessToken;
        _accessTokenExpiresAt = tokens.AccessTokenExpiresAt;
        User = tokens.User;
    }

    private void Forget()
    {
        store.Clear();
        _accessToken = null;
        User = null;
    }

    private async Task<AuthResult<T>> PostAsync<T>(string route, object body, string? accessToken, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.MasterUrl, route)) { Content = JsonContent.Create(body) };
            if (accessToken is not null)
            {
                request.Headers.Authorization = new("Bearer", accessToken);
            }

            using var response = await http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return new AuthResult<T>(response.StatusCode == HttpStatusCode.NoContent
                    ? null
                    : await response.Content.ReadFromJsonAsync<T>(cancellationToken), null);
            }

            // 5xx, 429 and proxy errors say nothing about the session: treat them like an outage and keep it.
            if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest or HttpStatusCode.Conflict))
            {
                return new AuthResult<T>(null, Unreachable);
            }

            AuthError? error = null;
            try
            {
                error = await response.Content.ReadFromJsonAsync<AuthError>(cancellationToken);
            }
            catch (System.Text.Json.JsonException)
            {
                // No body, or not ours.
            }

            return new AuthResult<T>(null, error ?? new AuthError("refused", $"HTTP {(int)response.StatusCode}"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new AuthResult<T>(null, Unreachable);
        }
    }
}
