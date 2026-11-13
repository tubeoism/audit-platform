using System.Net.Mail;
using System.Text.Json;
using AuditPlatform.AuditLog;
using AuditPlatform.Master.Data;
using AuditPlatform.Master.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Master.Auth;

/// <summary>Who performed an account action. The VM console and unauthenticated web requests have no user id.</summary>
public sealed record Actor(Guid UserId, string Via, string? Address)
{
    public static Actor Console(string osUser) => new(Guid.Empty, "vm-console:" + osUser, null);

    public static Actor Web(string? address) => new(Guid.Empty, "web", address);

    public static Actor User(Guid userId, string? address) => new(userId, "api", address);
}

public sealed record AuthFailure(string Code, string Message);

/// <summary>Result of an authentication operation: a value, or a failure with a stable code.</summary>
public readonly record struct Outcome<T>(T? Value, AuthFailure? Error)
{
    public bool Succeeded => Error is null;

    public static implicit operator Outcome<T>(T value) => new(value, null);

    public static implicit operator Outcome<T>(AuthFailure error) => new(default, error);
}

public static class AuthErrors
{
    /// <summary>
    /// One answer for every failed sign-in (unknown address, wrong password, wrong code, locked, disabled), so the
    /// response does not reveal which accounts exist or which factor was wrong. The audit log records the reason.
    /// </summary>
    public static readonly AuthFailure InvalidCredentials = new("invalid_credentials",
        "Thông tin đăng nhập không đúng, hoặc tài khoản đang tạm khoá. / Sign-in details are incorrect, or the account is temporarily locked.");

    public static readonly AuthFailure InvalidSession = new("invalid_session",
        "Phiên đã hết hạn hoặc bị thu hồi; hãy đăng nhập lại. / The session has expired or was revoked; sign in again.");

    public static readonly AuthFailure LinkInvalid = new("link_invalid",
        "Liên kết không hợp lệ, đã dùng hoặc đã hết hạn. / The link is invalid, already used or expired.");

    public static readonly AuthFailure CodeInvalid = new("code_invalid",
        "Mã xác thực không đúng hoặc đã được dùng; hãy chờ mã mới. / The code is wrong or was already used; wait for a new one.");

    public static AuthFailure Password(PasswordProblem problem) => problem switch
    {
        PasswordProblem.TooShort => new("password_too_short", "Mật khẩu cần tối thiểu 12 ký tự. / The password needs at least 12 characters."),
        PasswordProblem.TooLong => new("password_too_long", "Mật khẩu quá dài. / The password is too long."),
        PasswordProblem.TooPredictable => new("password_too_predictable",
            "Mật khẩu quá dễ đoán (lặp ký tự hoặc chứa địa chỉ e-mail). / The password is too predictable (repeated characters or contains your e-mail name)."),
        _ => new("password_breached",
            "Mật khẩu này nằm trong danh sách mật khẩu đã lộ; hãy chọn mật khẩu khác. / This password appears in known breaches; choose another."),
    };
}

/// <summary>Writes authentication events to the hash-chained audit log, in the caller's transaction.</summary>
public sealed class AuthAudit(IAuditLog log, MasterDbContext db, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task WriteAsync(Actor actor, string action, Guid? subjectUserId, object payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var body = JsonSerializer.Serialize(new { via = actor.Via, address = actor.Address, detail = payload }, Json);
        await log.AppendAsync(new AuditEvent(clock.GetUtcNow(), actor.UserId, action, "user",
            subjectUserId?.ToString("D") ?? "-", null, body), cancellationToken);

        // PostgresAuditLog has already saved pending changes with the entry; other implementations have not.
        await db.SaveChangesAsync(cancellationToken);
    }
}

internal static class AuthData
{
    public static string? NormalizeEmail(string? email)
    {
        var value = email?.Trim().ToLowerInvariant();
        return value is { Length: > 0 and <= 320 } && MailAddress.TryCreate(value, out var parsed)
            && parsed.Address == value && parsed.Host.Contains('.', StringComparison.Ordinal) ? value : null;
    }

    /// <summary>
    /// Locks the user row until the transaction ends. Sign-in attempts for one account run one at a time, so
    /// parallel guesses cannot slip past the failure counter or reuse one TOTP code.
    /// </summary>
    public static async Task<UserRow?> LockUserAsync(this MasterDbContext db, Guid id, CancellationToken cancellationToken) =>
        (await db.Users.FromSql($"SELECT * FROM users WHERE id = {id} FOR UPDATE").ToListAsync(cancellationToken)).FirstOrDefault();

    public static async Task<UserRow?> LockUserAsync(this MasterDbContext db, string email, CancellationToken cancellationToken) =>
        (await db.Users.FromSql($"SELECT * FROM users WHERE email = {email} FOR UPDATE").ToListAsync(cancellationToken)).FirstOrDefault();

    public static async Task<SessionRow?> LockSessionAsync(this MasterDbContext db, Guid id, CancellationToken cancellationToken) =>
        (await db.Sessions.FromSql($"SELECT * FROM sessions WHERE id = {id} FOR UPDATE").ToListAsync(cancellationToken)).FirstOrDefault();

    public static Task<int> RevokeSessionsAsync(this MasterDbContext db, Guid userId, Guid? installationId, string reason,
        DateTimeOffset now, CancellationToken cancellationToken) =>
        db.Sessions
            .Where(s => s.UserId == userId && s.RevokedAt == null && (installationId == null || s.InstallationId == installationId))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now).SetProperty(x => x.RevokedReason, reason), cancellationToken);

    public static string UserContext(Guid userId) => "totp:" + userId.ToString("N");
}
