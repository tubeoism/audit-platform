using System.Security.Claims;
using AuditPlatform.Contracts.Sync;
using AuditPlatform.Domain.Engagements;
using AuditPlatform.Master.Auth;
using AuditPlatform.Master.Security;
using Microsoft.AspNetCore.SignalR;

namespace AuditPlatform.Master.Sync;

internal static class SyncEndpoints
{
    /// <summary>
    /// Sign-off is the one change a client cannot queue offline: it is an HTTP call that needs the master, a live
    /// session, the reviewer's role in the engagement and a step-up grant for the content being signed.
    /// </summary>
    public static void MapSyncApi(this WebApplication app)
    {
        app.MapPost(SyncRoutes.SignOff, async (
                SignOffRequest request, ClaimsPrincipal user, IEngagementAccess access, FieldSyncEngine engine, ISignOffStepUp stepUp,
                IHubContext<SyncHub, ISyncClient> hub, CancellationToken ct) =>
            {
                var userId = user.GetUserId();
                var grant = await access.GetAsync(request.EngagementId, userId, ct);
                if (!grant.Exists || !grant.IsMember)
                {
                    // Same answer for "does not exist" and "not a member", as on the hub.
                    return Fail(StatusCodes.Status403Forbidden, "not_member", "Not a member of this engagement.");
                }

                if (!Enum.TryParse<EngagementRole>(request.Level, out var level) || level is not (EngagementRole.ManagerQa or EngagementRole.JapanDesk))
                {
                    return Fail(StatusCodes.Status400BadRequest, "level_invalid", "Level must be ManagerQa or JapanDesk.");
                }

                if (!grant.Roles.Contains(level))
                {
                    return Fail(StatusCodes.Status403Forbidden, "not_reviewer", $"You do not hold the {level} role in this engagement.");
                }

                var attempt = await engine.SignOffAsync(request.EngagementId, request.WorkingPaperId, level, userId, request.StepUpToken, stepUp, ct);
                switch (attempt.Status)
                {
                    case SignOffStatus.SignedOff:
                        await hub.Clients.Group(SyncHub.GroupName(request.EngagementId)).ChangesAvailable(request.EngagementId, attempt.Cursor);
                        return Results.Ok(new SignOffResponse(attempt.ChangeId, attempt.ContentHash!, attempt.Cursor));
                    case SignOffStatus.StepUpRefused:
                        return Fail(StatusCodes.Status403Forbidden, "step_up_required", attempt.Reason!);
                    case SignOffStatus.Locked:
                        return Fail(StatusCodes.Status409Conflict, "engagement_locked", attempt.Reason!);
                    default:
                        return Fail(StatusCodes.Status409Conflict, "sign_off_refused", attempt.Reason!);
                }
            })
            .RequireAuthorization()
            .RequireRateLimiting(AuthEndpoints.RateLimitPolicy)
            .AddEndpointFilter<RequireAuthKeysFilter>();
    }

    private static IResult Fail(int status, string code, string message) => Results.Json(new SyncError(code, message), statusCode: status);
}
