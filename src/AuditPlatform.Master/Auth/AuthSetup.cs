using System.Threading.RateLimiting;
using AuditPlatform.Contracts.Sync;
using AuditPlatform.Master.Data.Entities;
using AuditPlatform.Master.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AuditPlatform.Master.Auth;

internal static class AuthSetup
{
    public static void AddAuditAuthentication(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.Section));
        services.Configure<MailOptions>(builder.Configuration.GetSection(MailOptions.Section));

        services.AddSingleton<AuthKeys>();
        services.AddSingleton<LocalPasswordList>();
        services.AddSingleton<IPasswordHasher<UserRow>, PasswordHasher<UserRow>>();
        services.AddHttpClient<IBreachedPasswordChecker, HibpPasswordChecker>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(3);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("AuditPlatform-Master");
        });
        services.AddHttpClient<IEmailSender, BrevoEmailSender>(c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddScoped<PasswordPolicy>();
        services.AddScoped<AuthAudit>();
        services.AddScoped<AccountService>();
        services.AddScoped<SessionService>();
        services.AddScoped<ISignOffStepUp>(sp => sp.GetRequiredService<SessionService>());

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<AuthKeys, IOptions<AuthOptions>, TimeProvider>(ConfigureJwt);
        services.AddAuthorizationBuilder()
            .AddPolicy(AuthEndpoints.AdminPolicy, p => p.RequireAuthenticatedUser().RequireRole(nameof(SystemRole.Admin)));

        // Caddy on the same host is the only proxy trusted to state the client address (default: loopback only).
        services.Configure<ForwardedHeadersOptions>(o =>
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

        var perMinute = builder.Configuration.GetValue<int?>($"{AuthOptions.Section}:{nameof(AuthOptions.RateLimitPerMinute)}")
            ?? new AuthOptions().RateLimitPerMinute;
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(AuthEndpoints.RateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1) }));
        });
    }

    private static void ConfigureJwt(JwtBearerOptions jwt, AuthKeys keys, IOptions<AuthOptions> options, TimeProvider clock)
    {
        var o = options.Value;
        jwt.MapInboundClaims = false;
        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = o.Issuer,
            ValidAudience = o.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(keys.Signing),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "sub",
            RoleClaimType = "role",
            LifetimeValidator = (notBefore, expires, _, parameters) =>
            {
                var now = clock.GetUtcNow().UtcDateTime;
                return expires is { } end && now < end.Add(parameters.ClockSkew)
                    && (notBefore is not { } start || now >= start.Subtract(parameters.ClockSkew));
            },
        };
        jwt.Events = new JwtBearerEvents
        {
            // Browsers and the SignalR client cannot set headers on a WebSocket; the hub takes the token from the query string.
            OnMessageReceived = context =>
            {
                if (context.HttpContext.Request.Path.StartsWithSegments(SyncHubRoutes.Path, StringComparison.Ordinal)
                    && context.Request.Query.TryGetValue("access_token", out var token))
                {
                    context.Token = token;
                }

                return Task.CompletedTask;
            },

            // A valid signature is not enough: the session must still be live and the user still active.
            OnTokenValidated = async context =>
            {
                try
                {
                    var sessions = context.HttpContext.RequestServices.GetRequiredService<SessionService>();
                    if (!await sessions.IsSessionLiveAsync(context.Principal.GetUserId(), context.Principal.GetSessionId(),
                            context.HttpContext.RequestAborted))
                    {
                        context.Fail("Session revoked or user disabled.");
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    context.Fail("Token has no user or session.");
                }
            },
        };
    }
}
