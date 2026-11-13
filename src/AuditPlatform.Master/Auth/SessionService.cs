using System.Security.Claims;
using AuditPlatform.Contracts.Auth;
using AuditPlatform.Master.Data;
using AuditPlatform.Master.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AuditPlatform.Master.Auth;

/// <summary>
/// Proof that the signer entered a fresh TOTP code for exactly this working paper at exactly this content.
/// The sign-off feature calls <see cref="ConsumeAsync"/> before it records a sign-off.
/// </summary>
public interface ISignOffStepUp
{
    /// <summary>True once per grant, and only for the user, working paper and content hash it was issued for.</summary>
    Task<bool> ConsumeAsync(Guid userId, string? stepUpToken, Guid workingPaperId, string contentHash, CancellationToken cancellationToken);
}

/// <summary>
/// Sign-in, token issue and rotation, revocation checks and sign-off step-up (ADR-0005).
/// Access tokens are short-lived JWTs; refresh tokens are opaque, stored only as keyed hashes, and rotate on every use.
/// </summary>
public sealed class SessionService(
    MasterDbContext db, AuthKeys keys, IPasswordHasher<UserRow> hasher, AuthAudit audit, IOptions<AuthOptions> options,
    TimeProvider clock) : ISignOffStepUp
{
    public const string SessionClaim = "sid";
    public const string SignOffPurpose = "sign-off";

    private static readonly UserRow DummyUser = new() { Email = "nobody@invalid", DisplayName = "nobody" };
    private static readonly JsonWebTokenHandler Jwt = new() { SetDefaultTimesOnTokenCreation = false };
    private readonly AuthOptions _o = options.Value;

    // Verified for unknown addresses so that an unknown account takes as long to refuse as a wrong password.
    private static readonly Lazy<string> DummyHash =
        new(() => new PasswordHasher<UserRow>().HashPassword(DummyUser, AuthKeys.NewToken()));

    public async Task<Outcome<TokenResponse>> LoginAsync(LoginRequest request, string? address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = clock.GetUtcNow();
        var email = AuthData.NormalizeEmail(request.Email);
        var actor = Actor.Web(address);
        if (email is null || string.IsNullOrEmpty(request.Password) || request.Password.Length > 1024
            || request.InstallationId == Guid.Empty)
        {
            return AuthErrors.InvalidCredentials;
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await db.LockUserAsync(email, cancellationToken);
        if (user is not { IsActive: true, PasswordHash: not null, TotpEnrolledAt: not null })
        {
            hasher.VerifyHashedPassword(DummyUser, DummyHash.Value, request.Password);
            await audit.WriteAsync(actor, "auth.login.refused", user?.Id, new { email, reason = "unknown_disabled_or_not_enrolled" }, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return AuthErrors.InvalidCredentials;
        }

        if (user.LockedUntil > now)
        {
            await audit.WriteAsync(actor, "auth.login.refused", user.Id, new { reason = "locked", until = user.LockedUntil }, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return AuthErrors.InvalidCredentials;
        }

        // The second factor is examined only after the password is right, so it cannot be probed on its own.
        var password = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        var usedRecoveryCode = false;
        string? failure = null;
        if (password == PasswordVerificationResult.Failed)
        {
            failure = "password";
        }
        else if (!string.IsNullOrWhiteSpace(request.Code))
        {
            failure = VerifyTotp(user, request.Code, now) ? null : "totp";
        }
        else if (!string.IsNullOrWhiteSpace(request.RecoveryCode))
        {
            usedRecoveryCode = await ConsumeRecoveryCodeAsync(user.Id, request.RecoveryCode, now, cancellationToken);
            failure = usedRecoveryCode ? null : "recovery_code";
        }
        else
        {
            failure = "second_factor_missing";
        }

        if (failure is not null)
        {
            await AccountService.RegisterFailureAsync(db, audit, _o, user, actor, "login:" + failure, now, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return AuthErrors.InvalidCredentials;
        }

        user.FailedAttempts = 0;
        user.LockedUntil = null;
        if (password == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = hasher.HashPassword(user, request.Password);
        }

        // One live session per laptop: a new sign-in replaces the previous one on the same installation.
        await db.RevokeSessionsAsync(user.Id, request.InstallationId, "superseded", now, cancellationToken);
        var session = new SessionRow
        {
            Id = Guid.NewGuid(), UserId = user.Id, InstallationId = request.InstallationId,
            DeviceName = Clean(request.DeviceName, 100) ?? "unknown", CreatedAt = now, LastRefreshedAt = now,
            AbsoluteExpiresAt = now.Add(_o.SessionAbsoluteLifetime),
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        var (refresh, row) = NewRefreshToken(session, now);
        db.RefreshTokens.Add(row);

        int? remaining = usedRecoveryCode
            ? await db.RecoveryCodes.CountAsync(c => c.UserId == user.Id && c.UsedAt == null, cancellationToken)
            : null;
        await audit.WriteAsync(actor with { UserId = user.Id }, "auth.login.succeeded", user.Id, new
        {
            sessionId = session.Id, installationId = session.InstallationId, device = session.DeviceName,
            secondFactor = usedRecoveryCode ? "recovery_code" : "totp", recoveryCodesRemaining = remaining,
        }, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return Tokens(user, session, refresh, row, now, remaining);
    }

    public async Task<Outcome<TokenResponse>> RefreshAsync(RefreshRequest request, string? address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RefreshToken) || request.RefreshToken.Length > 200)
        {
            return AuthErrors.InvalidSession;
        }

        var now = clock.GetUtcNow();
        var hash = keys.Hash("refresh", request.RefreshToken);
        var sessionId = await db.RefreshTokens.AsNoTracking().Where(t => t.TokenHash == hash)
            .Select(t => (Guid?)t.SessionId).FirstOrDefaultAsync(cancellationToken);
        if (sessionId is null)
        {
            return AuthErrors.InvalidSession;
        }

        // The session row is locked, so two refreshes of one session are handled one after the other.
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var session = await db.LockSessionAsync(sessionId.Value, cancellationToken);
        var token = await db.RefreshTokens.FirstAsync(t => t.TokenHash == hash, cancellationToken);
        var user = session is null ? null : await db.Users.FirstOrDefaultAsync(u => u.Id == session.UserId, cancellationToken);
        if (session is not { RevokedAt: null } || user is not { IsActive: true } || now >= session.AbsoluteExpiresAt)
        {
            return AuthErrors.InvalidSession;
        }

        var actor = new Actor(user.Id, "api", address);
        if (session.InstallationId != request.InstallationId)
        {
            return await RevokeAsync("installation_mismatch");
        }

        if (token.UsedAt is { } usedAt)
        {
            var replacement = token.ReplacedBy is { } id
                ? await db.RefreshTokens.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
                : null;
            if (replacement is not { UsedAt: null } || now - usedAt > _o.RefreshRetryGrace)
            {
                // A rotated token came back: either it was copied, or the laptop was restored from an old image.
                return await RevokeAsync("refresh_token_reuse");
            }

            // The reply to the first rotation was lost on the way; retire the unseen replacement and issue another.
            replacement.UsedAt = now;
        }
        else if (now >= token.ExpiresAt)
        {
            return AuthErrors.InvalidSession;
        }
        else
        {
            token.UsedAt = now;
        }

        var (refresh, row) = NewRefreshToken(session, now);
        db.RefreshTokens.Add(row);
        token.ReplacedBy = row.Id;
        session.LastRefreshedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return Tokens(user, session, refresh, row, now, null);

        async Task<Outcome<TokenResponse>> RevokeAsync(string reason)
        {
            session.RevokedAt = now;
            session.RevokedReason = reason;
            await audit.WriteAsync(actor, "auth.session.revoked", user.Id,
                new { sessionId = session.Id, installationId = session.InstallationId, reason }, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return AuthErrors.InvalidSession;
        }
    }

    public async Task LogoutAsync(LogoutRequest request, string? address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RefreshToken) || request.RefreshToken.Length > 200)
        {
            return;
        }

        var hash = keys.Hash("refresh", request.RefreshToken);
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var session = await db.Sessions.FirstOrDefaultAsync(
            s => s.RevokedAt == null && db.RefreshTokens.Any(t => t.SessionId == s.Id && t.TokenHash == hash), cancellationToken);
        if (session is null)
        {
            return;
        }

        session.RevokedAt = now;
        session.RevokedReason = "signed_out";
        await audit.WriteAsync(new Actor(session.UserId, "api", address), "auth.logout", session.UserId,
            new { sessionId = session.Id }, cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Checked on every authenticated request: a disabled user or a revoked session is refused at once, without
    /// waiting for the access token to expire.
    /// </summary>
    public Task<bool> IsSessionLiveAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        return db.Sessions.AsNoTracking().AnyAsync(s => s.Id == sessionId && s.UserId == userId && s.RevokedAt == null
            && s.AbsoluteExpiresAt > now && db.Users.Any(u => u.Id == userId && u.IsActive), cancellationToken);
    }

    public async Task<UserInfo?> GetUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, cancellationToken);
        return user is null ? null : ToInfo(user);
    }

    /// <summary>
    /// Step-up for sign-off: a TOTP code that has not been used before (not the one used to sign in) buys one
    /// single-use grant for one working paper at one content hash. Recovery codes are not accepted.
    /// </summary>
    public async Task<Outcome<StepUpResponse>> IssueStepUpAsync(
        Guid userId, Guid sessionId, StepUpRequest request, string? address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var contentHash = request.ContentHash?.Trim();
        if (request.WorkingPaperId == Guid.Empty || contentHash is not { Length: > 0 and <= 128 })
        {
            return new AuthFailure("request_invalid", "Thiếu working paper hoặc content hash. / Working paper or content hash is missing.");
        }

        var now = clock.GetUtcNow();
        var actor = Actor.User(userId, address);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await db.LockUserAsync(userId, cancellationToken);
        if (user is not { IsActive: true, TotpEnrolledAt: not null } || user.LockedUntil > now)
        {
            return AuthErrors.CodeInvalid;
        }

        if (!VerifyTotp(user, request.Code, now))
        {
            await AccountService.RegisterFailureAsync(db, audit, _o, user, actor, "step_up:totp", now, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return AuthErrors.CodeInvalid;
        }

        user.FailedAttempts = 0;
        var token = AuthKeys.NewToken();
        var grant = new StepUpGrantRow
        {
            Id = Guid.NewGuid(), UserId = userId, SessionId = sessionId, TokenHash = keys.Hash("step-up", token),
            Purpose = SignOffPurpose, WorkingPaperId = request.WorkingPaperId, ContentHash = contentHash,
            IssuedAt = now, ExpiresAt = now.Add(_o.StepUpLifetime),
        };
        db.StepUpGrants.Add(grant);
        await audit.WriteAsync(actor, "auth.step_up.issued", userId,
            new { grantId = grant.Id, workingPaperId = grant.WorkingPaperId, contentHash, sessionId }, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new StepUpResponse(token, grant.ExpiresAt);
    }

    public async Task<bool> ConsumeAsync(
        Guid userId, string? stepUpToken, Guid workingPaperId, string contentHash, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(stepUpToken) || stepUpToken.Length > 200)
        {
            return false;
        }

        var now = clock.GetUtcNow();
        var hash = keys.Hash("step-up", stepUpToken);
        await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;

        // One statement claims the grant, so two concurrent sign-offs cannot both spend it.
        var claimed = await db.StepUpGrants
            .Where(g => g.TokenHash == hash && g.UserId == userId && g.Purpose == SignOffPurpose
                && g.WorkingPaperId == workingPaperId && g.ContentHash == contentHash && g.ConsumedAt == null && g.ExpiresAt > now
                && db.Sessions.Any(s => s.Id == g.SessionId && s.RevokedAt == null))
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.ConsumedAt, now), cancellationToken);
        if (claimed != 1)
        {
            return false;
        }

        await audit.WriteAsync(Actor.User(userId, null), "auth.step_up.consumed", userId, new { workingPaperId, contentHash }, cancellationToken);
        if (tx is not null)
        {
            await tx.CommitAsync(cancellationToken);
        }

        return true;
    }

    internal static UserInfo ToInfo(UserRow user) => new(user.Id, user.Email, user.DisplayName, user.Role.ToString());

    /// <summary>Accepts a code only from a time step later than the last accepted one: every code works once.</summary>
    private bool VerifyTotp(UserRow user, string? code, DateTimeOffset now)
    {
        if (keys.Unprotect(user.TotpSecret, AuthData.UserContext(user.Id)) is not { } secret
            || Totp.Verify(secret, code, now) is not { } step || step <= user.TotpLastStep)
        {
            return false;
        }

        user.TotpLastStep = step;
        return true;
    }

    private async Task<bool> ConsumeRecoveryCodeAsync(Guid userId, string code, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var hash = keys.Hash("recovery", AccountService.NormalizeRecoveryCode(code));
        return await db.RecoveryCodes.Where(c => c.UserId == userId && c.CodeHash == hash && c.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, now), cancellationToken) == 1;
    }

    private (string Token, RefreshTokenRow Row) NewRefreshToken(SessionRow session, DateTimeOffset now)
    {
        var token = AuthKeys.NewToken();
        var expires = now.Add(_o.RefreshTokenLifetime);
        return (token, new RefreshTokenRow
        {
            Id = Guid.NewGuid(), SessionId = session.Id, TokenHash = keys.Hash("refresh", token), IssuedAt = now,
            ExpiresAt = expires < session.AbsoluteExpiresAt ? expires : session.AbsoluteExpiresAt,
        });
    }

    private TokenResponse Tokens(UserRow user, SessionRow session, string refresh, RefreshTokenRow row, DateTimeOffset now, int? recoveryRemaining)
    {
        var expires = now.Add(_o.AccessTokenLifetime);
        var access = Jwt.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _o.Issuer,
            Audience = _o.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString("D")),
                new Claim(SessionClaim, session.Id.ToString("D")),
                new Claim("role", user.Role.ToString()),
            ]),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(keys.Signing), SecurityAlgorithms.HmacSha256),
        });
        return new TokenResponse(access, expires, refresh, row.ExpiresAt, session.AbsoluteExpiresAt, ToInfo(user), recoveryRemaining);
    }

    private static string? Clean(string? value, int max)
    {
        var text = new string([.. (value ?? string.Empty).Where(c => !char.IsControl(c))]).Trim();
        return text.Length == 0 ? null : text[..Math.Min(text.Length, max)];
    }
}
