namespace AuditPlatform.Master.Auth;

/// <summary>Authentication parameters (ADR-0005). Defaults are the values signed off by the sponsor on 2026-11-13.</summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>
    /// Base64 of at least 32 random bytes. Root of the token-signing, secret-encryption and token-hashing keys.
    /// Exists only in /etc/audit-master/master.env on the VM. Without it every authentication endpoint answers 503.
    /// </summary>
    public string? MasterKey { get; set; }

    /// <summary>Public origin used in e-mailed links.</summary>
    public Uri PublicBaseUrl { get; set; } = new("https://app.audit.io.vn");

    public string Issuer { get; set; } = "audit-master";
    public string Audience { get; set; } = "audit-client";

    /// <summary>Name shown in the authenticator app.</summary>
    public string TotpIssuer { get; set; } = "Audit Platform";

    public int PasswordMinLength { get; set; } = 12;
    public int PasswordMaxLength { get; set; } = 128;

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Sliding: a session unused for this long needs a full sign-in.</summary>
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(14);

    /// <summary>Absolute cap on a session, however regularly it is used.</summary>
    public TimeSpan SessionAbsoluteLifetime { get; set; } = TimeSpan.FromDays(90);

    /// <summary>
    /// A rotated refresh token is accepted once more within this window when its replacement was never used:
    /// covers a response lost on a bad connection. Outside it, reuse revokes the session.
    /// </summary>
    public TimeSpan RefreshRetryGrace { get; set; } = TimeSpan.FromSeconds(30);

    public int LockoutThreshold { get; set; } = 5;
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan InviteLifetime { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan PasswordResetLifetime { get; set; } = TimeSpan.FromMinutes(30);
    public int PasswordResetsPerHour { get; set; } = 3;

    /// <summary>Time allowed between the password step and the code step of the enrolment page.</summary>
    public TimeSpan EnrolmentTicketLifetime { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan StepUpLifetime { get; set; } = TimeSpan.FromMinutes(5);
    public int RecoveryCodeCount { get; set; } = 10;

    /// <summary>Requests per minute per client address on the authentication API and account pages.</summary>
    public int RateLimitPerMinute { get; set; } = 30;

    /// <summary>Query haveibeenpwned.com (k-anonymity range API). The bundled list is always checked as well.</summary>
    public bool UseHibp { get; set; } = true;
}

public sealed class MailOptions
{
    public const string Section = "Mail";

    /// <summary>Brevo transactional e-mail API key (HTTP API, not SMTP). VM only.</summary>
    public string? BrevoApiKey { get; set; }
    public string From { get; set; } = "no-reply@audit.io.vn";
    public string FromName { get; set; } = "Audit Platform";
}
