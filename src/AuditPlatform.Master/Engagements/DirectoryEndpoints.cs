using System.Security.Claims;
using AuditPlatform.Contracts.Engagements;
using AuditPlatform.Master.Auth;
using AuditPlatform.Master.Security;

namespace AuditPlatform.Master.Engagements;

internal static class DirectoryEndpoints
{
    /// <summary>
    /// What a client needs before its first sync: the engagements the caller belongs to (never anyone else's) and
    /// the methodology library. Both are read-only; engagements are set up from the console.
    /// </summary>
    public static void MapDirectoryApi(this WebApplication app)
    {
        app.MapGet(DirectoryRoutes.Engagements, async (ClaimsPrincipal user, EngagementService engagements, CancellationToken ct) =>
                Results.Ok(await engagements.ListForAsync(user.GetUserId(), ct)))
            .RequireAuthorization()
            .AddEndpointFilter<RequireAuthKeysFilter>();

        app.MapGet(DirectoryRoutes.Templates, async (TemplateService templates, CancellationToken ct) =>
                Results.Ok(await templates.ListAsync(ct)))
            .RequireAuthorization()
            .AddEndpointFilter<RequireAuthKeysFilter>();
    }
}
