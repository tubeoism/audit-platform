using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AuditPlatform.Master.Security;

/// <summary>
/// Fail-closed placeholder until the identity design is decided (ADR-0005, open). It never authenticates anyone,
/// so every endpoint marked [Authorize], including the sync hub, answers 401.
/// </summary>
internal sealed class PendingAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Pending";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
}
