namespace AuditPlatform.Contracts.Auth;

/// <summary>HTTP routes of the master's authentication API (ADR-0005).</summary>
public static class AuthRoutes
{
    public const string Login = "/api/auth/login";
    public const string Refresh = "/api/auth/refresh";
    public const string Logout = "/api/auth/logout";
    public const string Me = "/api/auth/me";
    public const string StepUp = "/api/auth/step-up";
    public const string ForgotPassword = "/api/auth/password/forgot";
    public const string AdminUsers = "/api/admin/users";
}

/// <summary>
/// Password plus exactly one second factor: a TOTP code, or a recovery code when the authenticator is unavailable.
/// </summary>
public sealed record LoginRequest(
    string Email, string Password, string? Code, string? RecoveryCode, Guid InstallationId, string? DeviceName);

public sealed record RefreshRequest(string RefreshToken, Guid InstallationId);

public sealed record LogoutRequest(string RefreshToken);

public sealed record UserInfo(Guid Id, string Email, string DisplayName, string Role);

public sealed record TokenResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    DateTimeOffset SessionExpiresAt,
    UserInfo User,
    int? RecoveryCodesRemaining);

/// <summary>Asks for a sign-off grant. The code must be a fresh TOTP code; recovery codes are not accepted.</summary>
public sealed record StepUpRequest(string Code, Guid WorkingPaperId, string ContentHash);

public sealed record StepUpResponse(string StepUpToken, DateTimeOffset ExpiresAt);

public sealed record ForgotPasswordRequest(string Email);

/// <summary>Error body of the authentication API. <see cref="Error"/> is a stable machine-readable code.</summary>
public sealed record AuthError(string Error, string Message);

public sealed record InviteUserRequest(string Email, string DisplayName, string Role);

public sealed record SetRoleRequest(string Role);

public sealed record AdminUserInfo(
    Guid Id, string Email, string DisplayName, string Role, bool IsActive, bool IsEnrolled, bool IsLocked,
    DateTimeOffset CreatedAt, int ActiveSessions);

public sealed record DeviceInfo(
    Guid InstallationId, string DeviceName, DateTimeOffset SignedInAt, DateTimeOffset LastSeenAt, bool IsActive);
