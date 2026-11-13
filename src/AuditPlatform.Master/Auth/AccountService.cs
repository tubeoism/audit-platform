using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AuditPlatform.Contracts.Auth;
using AuditPlatform.Master.Data;
using AuditPlatform.Master.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuditPlatform.Master.Auth;

public sealed record InviteResult(Guid UserId, Uri Link, bool MailSent);

public sealed record LinkInfo(UserTokenPurpose Purpose, string Email, string DisplayName);

/// <summary>What the enrolment page shows: the secret for manual entry, the QR payload, and the ticket for the code step.</summary>
public sealed record Enrolment(string Ticket, string Email, string SecretBase32, string OtpAuthUri);

/// <summary>
/// Account lifecycle (ADR-0005): invitation, password, TOTP enrolment, recovery codes, password reset and the
/// administrator actions. There is no self-registration: an account exists only because an administrator invited it.
/// </summary>
public sealed class AccountService(
    MasterDbContext db, AuthKeys keys, PasswordPolicy policy, IPasswordHasher<UserRow> hasher, IEmailSender mail,
    AuthAudit audit, IOptions<AuthOptions> options, TimeProvider clock)
{
    private const string RecoveryAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no I, O, 0, 1
    private readonly AuthOptions _o = options.Value;

    // ----- Invitation and enrolment -------------------------------------------------------------------------------

    public async Task<Outcome<InviteResult>> InviteAsync(
        Actor actor, string? email, string? displayName, SystemRole role, CancellationToken cancellationToken)
    {
        var address = AuthData.NormalizeEmail(email);
        var name = displayName?.Trim();
        if (address is null)
        {
            return new AuthFailure("email_invalid", "Địa chỉ e-mail không hợp lệ. / The e-mail address is not valid.");
        }

        if (name is not { Length: > 0 and <= 200 })
        {
            return new AuthFailure("name_invalid", "Cần tên hiển thị (tối đa 200 ký tự). / A display name is required (200 characters at most).");
        }

        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await db.LockUserAsync(address, cancellationToken);
        if (user is { TotpEnrolledAt: not null })
        {
            return new AuthFailure("already_enrolled", "Tài khoản này đã kích hoạt. / This account is already enrolled.");
        }

        if (user is { IsActive: false })
        {
            return new AuthFailure("user_disabled", "Tài khoản đang bị vô hiệu hoá. / The account is disabled.");
        }

        if (user is null)
        {
            user = new UserRow { Id = Guid.NewGuid(), Email = address, DisplayName = name, Role = role, CreatedAt = now };
            db.Users.Add(user);
            await db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            user.DisplayName = name;
            user.Role = role;
        }

        var link = await IssueLinkAsync(user.Id, UserTokenPurpose.Invite, _o.InviteLifetime, actor.UserId, now, cancellationToken);
        await audit.WriteAsync(actor, "auth.invite.sent", user.Id, new { email = address, role = role.ToString() }, cancellationToken);
        await tx.CommitAsync(cancellationToken);

        var sent = await SendAsync(actor, user.Id, EmailTemplates.Invitation(address, name, link, _o.InviteLifetime), cancellationToken);
        return new InviteResult(user.Id, link, sent);
    }

    public async Task<LinkInfo?> DescribeLinkAsync(string? token, CancellationToken cancellationToken)
    {
        var found = await FindLinkAsync(token, cancellationToken);
        return found is var (link, user) ? new LinkInfo(link.Purpose, user.Email, user.DisplayName) : null;
    }

    /// <summary>
    /// Password step of the enrolment page. An invitation sets the password; a re-enrolment after an MFA reset
    /// verifies the existing one. Either way a new TOTP secret is generated and stays pending until confirmed.
    /// </summary>
    public async Task<Outcome<Enrolment>> BeginEnrolmentAsync(
        string? token, string? password, Actor actor, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await FindLinkAsync(token, cancellationToken, UserTokenPurpose.Invite, UserTokenPurpose.MfaEnrol) is not var (link, found)
            || await db.LockUserAsync(found.Id, cancellationToken) is not { TotpEnrolledAt: null } user)
        {
            return AuthErrors.LinkInvalid;
        }

        if (link.Purpose == UserTokenPurpose.Invite)
        {
            if (await policy.CheckAsync(password, user.Email, cancellationToken) is { } problem)
            {
                return AuthErrors.Password(problem);
            }

            user.PasswordHash = hasher.HashPassword(user, password!);
            user.PasswordChangedAt = now;
        }
        else
        {
            var locked = user.LockedUntil > now;
            if (locked || user.PasswordHash is null || string.IsNullOrEmpty(password)
                || hasher.VerifyHashedPassword(user, user.PasswordHash, password) == PasswordVerificationResult.Failed)
            {
                if (!locked)
                {
                    await RegisterFailureAsync(db, audit, _o, user, actor, "mfa_enrol_password", now, cancellationToken);
                    await tx.CommitAsync(cancellationToken);
                }

                return AuthErrors.InvalidCredentials;
            }
        }

        var secret = Totp.NewSecret();
        user.TotpSecret = keys.Protect(secret, AuthData.UserContext(user.Id));
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        var expires = now.Add(_o.EnrolmentTicketLifetime).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var ticket = keys.Protect(Encoding.UTF8.GetBytes($"{link.Id:N}|{expires}"), "enrol-ticket");
        return ToEnrolment(ticket, user, secret);
    }

    /// <summary>Shows the pending QR code again (after a wrong code). Needs the ticket from the password step.</summary>
    public async Task<Outcome<Enrolment>> ResumeEnrolmentAsync(string? token, string? ticket, CancellationToken cancellationToken)
    {
        if (await FindLinkAsync(token, cancellationToken, UserTokenPurpose.Invite, UserTokenPurpose.MfaEnrol) is not var (link, user)
            || !TicketMatches(ticket, link.Id) || user.TotpEnrolledAt is not null
            || keys.Unprotect(user.TotpSecret, AuthData.UserContext(user.Id)) is not { } secret)
        {
            return AuthErrors.LinkInvalid;
        }

        return ToEnrolment(ticket!, user, secret);
    }

    /// <summary>Code step: proves the authenticator app holds the secret, then issues the recovery codes (shown once).</summary>
    public async Task<Outcome<IReadOnlyList<string>>> ConfirmEnrolmentAsync(
        string? token, string? ticket, string? code, Actor actor, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await FindLinkAsync(token, cancellationToken, UserTokenPurpose.Invite, UserTokenPurpose.MfaEnrol) is not var (link, found)
            || !TicketMatches(ticket, link.Id)
            || await db.LockUserAsync(found.Id, cancellationToken) is not { TotpEnrolledAt: null, PasswordHash: not null } user
            || keys.Unprotect(user.TotpSecret, AuthData.UserContext(user.Id)) is not { } secret)
        {
            return AuthErrors.LinkInvalid;
        }

        if (Totp.Verify(secret, code, now) is not { } step)
        {
            return AuthErrors.CodeInvalid;
        }

        user.TotpEnrolledAt = now;
        user.TotpLastStep = step;
        user.FailedAttempts = 0;
        user.LockedUntil = null;
        link.UsedAt = now;

        await db.RecoveryCodes.Where(c => c.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
        var codes = Enumerable.Range(0, _o.RecoveryCodeCount).Select(_ => NewRecoveryCode()).ToList();
        db.RecoveryCodes.AddRange(codes.Select(c => new RecoveryCodeRow
        {
            Id = Guid.NewGuid(), UserId = user.Id, CodeHash = keys.Hash("recovery", NormalizeRecoveryCode(c)),
        }));

        await audit.WriteAsync(actor with { UserId = user.Id }, "auth.totp.enrolled", user.Id,
            new { purpose = link.Purpose.ToString(), recoveryCodes = codes.Count }, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return codes;
    }

    // ----- Password reset -----------------------------------------------------------------------------------------

    /// <summary>Always completes without saying whether the address has an account.</summary>
    public async Task RequestPasswordResetAsync(string? email, Actor actor, CancellationToken cancellationToken)
    {
        var address = AuthData.NormalizeEmail(email);
        if (address is null)
        {
            return;
        }

        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await db.LockUserAsync(address, cancellationToken);
        if (user is not { IsActive: true, PasswordHash: not null })
        {
            return;
        }

        var since = now.AddHours(-1);
        var recent = await db.UserTokens.CountAsync(
            t => t.UserId == user.Id && t.Purpose == UserTokenPurpose.PasswordReset && t.CreatedAt > since, cancellationToken);
        if (recent >= _o.PasswordResetsPerHour)
        {
            return;
        }

        var link = await IssueLinkAsync(user.Id, UserTokenPurpose.PasswordReset, _o.PasswordResetLifetime, Guid.Empty, now, cancellationToken);
        await audit.WriteAsync(actor, "auth.password.reset_requested", user.Id, new { }, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        await SendAsync(actor, user.Id,
            EmailTemplates.PasswordReset(user.Email, user.DisplayName, link, _o.PasswordResetLifetime), cancellationToken);
    }

    /// <summary>Sets a new password and signs every device out. Sign-in still needs the TOTP code.</summary>
    public async Task<Outcome<bool>> ResetPasswordAsync(string? token, string? password, Actor actor, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await FindLinkAsync(token, cancellationToken, UserTokenPurpose.PasswordReset) is not var (link, found)
            || await db.LockUserAsync(found.Id, cancellationToken) is not { } user)
        {
            return AuthErrors.LinkInvalid;
        }

        if (await policy.CheckAsync(password, user.Email, cancellationToken) is { } problem)
        {
            return AuthErrors.Password(problem);
        }

        user.PasswordHash = hasher.HashPassword(user, password!);
        user.PasswordChangedAt = now;
        user.FailedAttempts = 0;
        user.LockedUntil = null;
        link.UsedAt = now;
        var revoked = await db.RevokeSessionsAsync(user.Id, null, "password_reset", now, cancellationToken);
        await audit.WriteAsync(actor with { UserId = user.Id }, "auth.password.reset", user.Id, new { sessionsRevoked = revoked }, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return true;
    }

    // ----- Administration -----------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<AdminUserInfo>> ListUsersAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var rows = await db.Users.AsNoTracking().OrderBy(u => u.Email).Select(u => new
        {
            User = u,
            Sessions = db.Sessions.Count(s => s.UserId == u.Id && s.RevokedAt == null && s.AbsoluteExpiresAt > now),
        }).ToListAsync(cancellationToken);
        return [.. rows.Select(r => new AdminUserInfo(r.User.Id, r.User.Email, r.User.DisplayName, r.User.Role.ToString(),
            r.User.IsActive, r.User.TotpEnrolledAt is not null, r.User.LockedUntil > now, r.User.CreatedAt, r.Sessions))];
    }

    public async Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var sessions = await db.Sessions.AsNoTracking().Where(s => s.UserId == userId).ToListAsync(cancellationToken);
        return [.. sessions.GroupBy(s => s.InstallationId).Select(g =>
        {
            var latest = g.MaxBy(s => s.CreatedAt)!;
            return new DeviceInfo(g.Key, latest.DeviceName, latest.CreatedAt, g.Max(s => s.LastRefreshedAt),
                g.Any(s => s.RevokedAt is null && s.AbsoluteExpiresAt > now));
        }).OrderByDescending(d => d.LastSeenAt)];
    }

    public Task<Outcome<bool>> SetActiveAsync(Actor actor, Guid userId, bool active, CancellationToken cancellationToken) =>
        ChangeUserAsync(actor, userId, async (user, now) =>
        {
            if (!active && user.Role == SystemRole.Admin && !await AnotherAdminExistsAsync(user.Id, cancellationToken))
            {
                return LastAdmin;
            }

            user.IsActive = active;
            var revoked = active ? 0 : await db.RevokeSessionsAsync(user.Id, null, "user_disabled", now, cancellationToken);
            await audit.WriteAsync(actor, active ? "auth.user.enabled" : "auth.user.disabled", user.Id,
                new { sessionsRevoked = revoked }, cancellationToken);
            return null;
        }, cancellationToken);

    public Task<Outcome<bool>> SetRoleAsync(Actor actor, Guid userId, SystemRole role, CancellationToken cancellationToken) =>
        ChangeUserAsync(actor, userId, async (user, now) =>
        {
            if (user.Role == role)
            {
                return null;
            }

            if (role != SystemRole.Admin && !await AnotherAdminExistsAsync(user.Id, cancellationToken))
            {
                return LastAdmin;
            }

            var previous = user.Role;
            user.Role = role;

            // Access tokens carry the role; ending the sessions makes the change effective at once.
            var revoked = await db.RevokeSessionsAsync(user.Id, null, "role_changed", now, cancellationToken);
            await audit.WriteAsync(actor, "auth.user.role_changed", user.Id,
                new { from = previous.ToString(), to = role.ToString(), sessionsRevoked = revoked }, cancellationToken);
            return null;
        }, cancellationToken);

    /// <summary>For a lost authenticator with no recovery codes left: clears TOTP and e-mails a re-enrolment link.</summary>
    public async Task<Outcome<InviteResult>> ResetMfaAsync(Actor actor, Guid userId, CancellationToken cancellationToken)
    {
        Uri? link = null;
        UserRow? target = null;
        var result = await ChangeUserAsync(actor, userId, async (user, now) =>
        {
            if (user is not { IsActive: true, PasswordHash: not null })
            {
                return new AuthFailure("not_enrolled", "Tài khoản chưa kích hoạt; hãy gửi lại lời mời. / The account is not enrolled; send the invitation again.");
            }

            user.TotpSecret = null;
            user.TotpEnrolledAt = null;
            user.TotpLastStep = 0;
            await db.RecoveryCodes.Where(c => c.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
            var revoked = await db.RevokeSessionsAsync(user.Id, null, "mfa_reset", now, cancellationToken);
            link = await IssueLinkAsync(user.Id, UserTokenPurpose.MfaEnrol, _o.InviteLifetime, actor.UserId, now, cancellationToken);
            await audit.WriteAsync(actor, "auth.mfa.reset", user.Id, new { sessionsRevoked = revoked }, cancellationToken);
            target = user;
            return null;
        }, cancellationToken);

        if (result.Error is { } error)
        {
            return error;
        }

        var sent = await SendAsync(actor, userId,
            EmailTemplates.MfaEnrolment(target!.Email, target.DisplayName, link!, _o.InviteLifetime), cancellationToken);
        return new InviteResult(userId, link!, sent);
    }

    /// <summary>Ends the sessions of one laptop (<paramref name="installationId"/>) or of every device.</summary>
    public Task<Outcome<bool>> RevokeSessionsAsync(Actor actor, Guid userId, Guid? installationId, CancellationToken cancellationToken) =>
        ChangeUserAsync(actor, userId, async (user, now) =>
        {
            var revoked = await db.RevokeSessionsAsync(user.Id, installationId, "revoked_by_admin", now, cancellationToken);
            await audit.WriteAsync(actor, "auth.sessions.revoked", user.Id, new { installationId, sessionsRevoked = revoked }, cancellationToken);
            return null;
        }, cancellationToken);

    public async Task<UserRow?> FindUserAsync(string emailOrId, CancellationToken cancellationToken) =>
        Guid.TryParse(emailOrId, out var id)
            ? await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancellationToken)
            : AuthData.NormalizeEmail(emailOrId) is { } email
                ? await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == email, cancellationToken)
                : null;

    // ----- Shared -------------------------------------------------------------------------------------------------

    internal static string NormalizeRecoveryCode(string code) =>
        new([.. code.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit)]);

    /// <summary>Counts a failed attempt; the fifth locks the account. The caller commits the transaction.</summary>
    internal static async Task RegisterFailureAsync(MasterDbContext db, AuthAudit audit, AuthOptions o, UserRow user, Actor actor,
        string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        user.FailedAttempts++;
        var locked = user.FailedAttempts >= o.LockoutThreshold;
        if (locked)
        {
            user.FailedAttempts = 0;
            user.LockedUntil = now.Add(o.LockoutDuration);
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(actor, "auth.attempt.failed", user.Id, new { reason, attempts = locked ? o.LockoutThreshold : user.FailedAttempts },
            cancellationToken);
        if (locked)
        {
            await audit.WriteAsync(actor, "auth.account.locked", user.Id, new { until = user.LockedUntil }, cancellationToken);
        }
    }

    private static readonly AuthFailure LastAdmin = new("last_admin",
        "Phải còn ít nhất một quản trị viên đang hoạt động. / At least one active administrator must remain.");

    private static readonly AuthFailure UserNotFound = new("user_not_found", "Không tìm thấy tài khoản. / User not found.");

    private Task<bool> AnotherAdminExistsAsync(Guid exceptUserId, CancellationToken cancellationToken) =>
        db.Users.AnyAsync(u => u.Id != exceptUserId && u.IsActive && u.Role == SystemRole.Admin, cancellationToken);

    private async Task<Outcome<bool>> ChangeUserAsync(Actor actor, Guid userId,
        Func<UserRow, DateTimeOffset, Task<AuthFailure?>> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await db.LockUserAsync(userId, cancellationToken) is not { } user)
        {
            return UserNotFound;
        }

        if (await change(user, clock.GetUtcNow()) is { } failure)
        {
            return failure;
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<(UserTokenRow Link, UserRow User)?> FindLinkAsync(
        string? token, CancellationToken cancellationToken, params UserTokenPurpose[] purposes)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
        {
            return null;
        }

        var hash = keys.Hash("link", token);
        var now = clock.GetUtcNow();
        var link = await db.UserTokens.FirstOrDefaultAsync(t => t.TokenHash == hash && t.UsedAt == null && t.ExpiresAt > now, cancellationToken);
        if (link is null || (purposes.Length > 0 && !purposes.Contains(link.Purpose)))
        {
            return null;
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == link.UserId && u.IsActive, cancellationToken);
        return user is null ? null : (link, user);
    }

    /// <summary>Creates a one-time link and retires earlier unused links of the same purpose.</summary>
    private async Task<Uri> IssueLinkAsync(Guid userId, UserTokenPurpose purpose, TimeSpan lifetime, Guid createdBy,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await db.UserTokens.Where(t => t.UserId == userId && t.Purpose == purpose && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), cancellationToken);
        var token = AuthKeys.NewToken();
        db.UserTokens.Add(new UserTokenRow
        {
            Id = Guid.NewGuid(), UserId = userId, Purpose = purpose, TokenHash = keys.Hash("link", token),
            CreatedAt = now, ExpiresAt = now.Add(lifetime), CreatedBy = createdBy,
        });
        var path = purpose == UserTokenPurpose.PasswordReset ? "account/reset" : "account/enrol";
        return new Uri(_o.PublicBaseUrl, $"{path}?token={token}");
    }

    private async Task<bool> SendAsync(Actor actor, Guid userId, EmailMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await mail.SendAsync(message, cancellationToken);
            return true;
        }
        catch (EmailDeliveryException ex)
        {
            await audit.WriteAsync(actor, "auth.mail.failed", userId, new { subject = message.Subject, reason = ex.Message }, cancellationToken);
            return false;
        }
    }

    private bool TicketMatches(string? ticket, Guid linkId)
    {
        if (keys.Unprotect(ticket, "enrol-ticket") is not { } raw)
        {
            return false;
        }

        var parts = Encoding.UTF8.GetString(raw).Split('|');
        return parts.Length == 2 && parts[0] == linkId.ToString("N")
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var expires)
            && clock.GetUtcNow().ToUnixTimeSeconds() < expires;
    }

    private Enrolment ToEnrolment(string ticket, UserRow user, byte[] secret) =>
        new(ticket, user.Email, Base32.Encode(secret), Totp.OtpAuthUri(_o.TotpIssuer, user.Email, secret));

    private static string NewRecoveryCode()
    {
        var chars = RandomNumberGenerator.GetItems<char>(RecoveryAlphabet, 12);
        return $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}-{new string(chars, 8, 4)}";
    }
}
