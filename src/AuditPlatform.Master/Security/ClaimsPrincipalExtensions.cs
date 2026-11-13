using System.Security.Claims;
using AuditPlatform.Master.Auth;

namespace AuditPlatform.Master.Security;

internal static class ClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal? principal)
    {
        var value = principal?.FindFirstValue("sub") ?? principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var id) ? id : throw new UnauthorizedAccessException("Caller has no user id claim.");
    }

    public static Guid GetSessionId(this ClaimsPrincipal? principal) =>
        Guid.TryParse(principal?.FindFirstValue(SessionService.SessionClaim), out var id)
            ? id
            : throw new UnauthorizedAccessException("Caller has no session claim.");
}
