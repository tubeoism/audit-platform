using System.Security.Claims;
using AuditPlatform.Contracts.Auth;
using AuditPlatform.Master.Data.Entities;
using AuditPlatform.Master.Security;

namespace AuditPlatform.Master.Auth;

/// <summary>Refuses authentication work with 503 while the master key is missing, instead of issuing tokens that a restart would void.</summary>
internal sealed class RequireAuthKeysFilter(AuthKeys keys) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        keys.IsConfigured
            ? await next(context)
            : Results.Json(new AuthError("auth_not_configured", "Authentication is not configured on the master (Auth:MasterKey)."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
}

internal static class AuthEndpoints
{
    public const string RateLimitPolicy = "auth";
    public const string AdminPolicy = "Admin";

    public static void MapAuthApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/auth").RequireRateLimiting(RateLimitPolicy).AddEndpointFilter<RequireAuthKeysFilter>();

        api.MapPost("/login", async (LoginRequest request, SessionService sessions, HttpContext http, CancellationToken ct) =>
            ToResult(await sessions.LoginAsync(request, AccountPages.Address(http), ct), StatusCodes.Status401Unauthorized));

        api.MapPost("/refresh", async (RefreshRequest request, SessionService sessions, HttpContext http, CancellationToken ct) =>
            ToResult(await sessions.RefreshAsync(request, AccountPages.Address(http), ct), StatusCodes.Status401Unauthorized));

        api.MapPost("/logout", async (LogoutRequest request, SessionService sessions, HttpContext http, CancellationToken ct) =>
        {
            await sessions.LogoutAsync(request, AccountPages.Address(http), ct);
            return Results.NoContent();
        });

        api.MapPost("/password/forgot", async (ForgotPasswordRequest request, AccountService accounts, HttpContext http, CancellationToken ct) =>
        {
            await accounts.RequestPasswordResetAsync(request.Email, Actor.Web(AccountPages.Address(http)), ct);
            return Results.Accepted();
        });

        api.MapGet("/me", async (ClaimsPrincipal user, SessionService sessions, CancellationToken ct) =>
            await sessions.GetUserAsync(user.GetUserId(), ct) is { } info ? Results.Ok(info) : Results.Unauthorized())
            .RequireAuthorization();

        api.MapPost("/step-up", async (StepUpRequest request, ClaimsPrincipal user, SessionService sessions, HttpContext http, CancellationToken ct) =>
            ToResult(await sessions.IssueStepUpAsync(user.GetUserId(), user.GetSessionId(), request, AccountPages.Address(http), ct),
                StatusCodes.Status403Forbidden))
            .RequireAuthorization();
    }

    /// <summary>
    /// Account administration. No endpoint returns an invitation or re-enrolment link: the link goes only to the
    /// user's mailbox, so an administrator cannot activate an account in someone else's name.
    /// </summary>
    public static void MapAdminApi(this WebApplication app)
    {
        var admin = app.MapGroup(AuthRoutes.AdminUsers).RequireAuthorization(AdminPolicy).AddEndpointFilter<RequireAuthKeysFilter>();

        admin.MapGet("", async (AccountService accounts, CancellationToken ct) => Results.Ok(await accounts.ListUsersAsync(ct)));

        admin.MapPost("", async (InviteUserRequest request, AccountService accounts, ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
        {
            if (!Enum.TryParse<SystemRole>(request.Role, ignoreCase: true, out var role) || !Enum.IsDefined(role))
            {
                return BadRole();
            }

            var result = await accounts.InviteAsync(Who(user, http), request.Email, request.DisplayName, role, ct);
            return result.Error is { } error ? Fail(error) : Results.Ok(new { result.Value!.UserId, result.Value.MailSent });
        });

        admin.MapPost("/{id:guid}/disable", async (Guid id, AccountService accounts, ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
            Done(await accounts.SetActiveAsync(Who(user, http), id, false, ct)));

        admin.MapPost("/{id:guid}/enable", async (Guid id, AccountService accounts, ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
            Done(await accounts.SetActiveAsync(Who(user, http), id, true, ct)));

        admin.MapPut("/{id:guid}/role", async (Guid id, SetRoleRequest request, AccountService accounts, ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
            Enum.TryParse<SystemRole>(request.Role, ignoreCase: true, out var role) && Enum.IsDefined(role)
                ? Done(await accounts.SetRoleAsync(Who(user, http), id, role, ct))
                : BadRole());

        admin.MapPost("/{id:guid}/reset-mfa", async (Guid id, AccountService accounts, ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
        {
            var result = await accounts.ResetMfaAsync(Who(user, http), id, ct);
            return result.Error is { } error ? Fail(error) : Results.Ok(new { result.Value!.UserId, result.Value.MailSent });
        });

        admin.MapGet("/{id:guid}/devices", async (Guid id, AccountService accounts, CancellationToken ct) =>
            Results.Ok(await accounts.ListDevicesAsync(id, ct)));

        admin.MapDelete("/{id:guid}/devices/{installationId:guid}", async (Guid id, Guid installationId, AccountService accounts, ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
            Done(await accounts.RevokeSessionsAsync(Who(user, http), id, installationId, ct)));

        admin.MapDelete("/{id:guid}/sessions", async (Guid id, AccountService accounts, ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
            Done(await accounts.RevokeSessionsAsync(Who(user, http), id, null, ct)));
    }

    private static Actor Who(ClaimsPrincipal user, HttpContext http) => Actor.User(user.GetUserId(), AccountPages.Address(http));

    private static IResult ToResult<T>(Outcome<T> outcome, int failureStatus) =>
        outcome.Error is { } error
            ? Results.Json(new AuthError(error.Code, error.Message), statusCode: failureStatus)
            : Results.Ok(outcome.Value);

    private static IResult Done(Outcome<bool> outcome) => outcome.Error is { } error ? Fail(error) : Results.NoContent();

    private static IResult Fail(AuthFailure error) => Results.Json(new AuthError(error.Code, error.Message),
        statusCode: error.Code == "user_not_found" ? StatusCodes.Status404NotFound : StatusCodes.Status409Conflict);

    private static IResult BadRole() => Results.Json(new AuthError("role_invalid", "Role must be User or Admin."),
        statusCode: StatusCodes.Status400BadRequest);
}
