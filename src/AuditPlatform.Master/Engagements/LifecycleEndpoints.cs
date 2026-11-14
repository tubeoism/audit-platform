using System.Security.Claims;
using AuditPlatform.Contracts.Engagements;
using AuditPlatform.Contracts.Sync;
using AuditPlatform.Domain.Engagements;
using AuditPlatform.Master.Auth;
using AuditPlatform.Master.Security;
using AuditPlatform.Master.Sync;
using Microsoft.AspNetCore.SignalR;

namespace AuditPlatform.Master.Engagements;

internal static class LifecycleEndpoints
{
    /// <summary>
    /// Moving an engagement through its phases and locking it. The lock needs a live session, the manager role and a
    /// step-up grant for the exact state being locked; the master then tells every connected member.
    /// </summary>
    public static void MapLifecycleApi(this WebApplication app)
    {
        app.MapPost(LifecycleRoutes.StatusTemplate, async (
                Guid id, AdvanceRequest request, ClaimsPrincipal user, EngagementLifecycle lifecycle, HttpContext http, CancellationToken ct) =>
            {
                if (!Enum.TryParse<EngagementStatus>(request.Target, ignoreCase: false, out var target) || !Enum.IsDefined(target))
                {
                    return Fail(StatusCodes.Status400BadRequest, "status_invalid", "Unknown status.");
                }

                var result = await lifecycle.AdvanceAsync(Actor.User(user.GetUserId(), AccountPages.Address(http)), id, target, ct);
                if (result.Error is { } error)
                {
                    return Fail(error);
                }

                // Members see the new phase on their next directory refresh; the data does not change.
                return Results.Ok(new AdvanceResponse(result.Value.ToString()));
            })
            .RequireAuthorization()
            .RequireRateLimiting(AuthEndpoints.RateLimitPolicy)
            .AddEndpointFilter<RequireAuthKeysFilter>();

        app.MapGet(LifecycleRoutes.LockReadinessTemplate, async (
                Guid id, ClaimsPrincipal user, IEngagementAccess access, EngagementLifecycle lifecycle, CancellationToken ct) =>
            {
                var grant = await access.GetAsync(id, user.GetUserId(), ct);
                if (!grant.Exists || !grant.IsMember)
                {
                    return Fail(StatusCodes.Status403Forbidden, "not_member", "Not a member of this engagement.");
                }

                return Results.Ok(await lifecycle.ReadinessAsync(id, ct));
            })
            .RequireAuthorization()
            .AddEndpointFilter<RequireAuthKeysFilter>();

        app.MapPost(LifecycleRoutes.LockTemplate, async (
                Guid id, LockRequest request, ClaimsPrincipal user, EngagementLifecycle lifecycle, HttpContext http,
                IHubContext<SyncHub, ISyncClient> hub, CancellationToken ct) =>
            {
                var result = await lifecycle.LockAsync(Actor.User(user.GetUserId(), AccountPages.Address(http)), id, request.StepUpToken, ct);
                if (result.Error is { } error)
                {
                    return Fail(error);
                }

                var locked = result.Value!;
                await hub.Clients.Group(SyncHub.GroupName(id)).EngagementLocked(id, locked.LockedAt);
                return Results.Ok(locked);
            })
            .RequireAuthorization()
            .RequireRateLimiting(AuthEndpoints.RateLimitPolicy)
            .AddEndpointFilter<RequireAuthKeysFilter>();
    }

    private static IResult Fail(AuthFailure error) => Fail(error.Code switch
    {
        "not_manager" or "step_up_required" => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status409Conflict,
    }, error.Code, error.Message);

    private static IResult Fail(int status, string code, string message) => Results.Json(new SyncError(code, message), statusCode: status);
}
