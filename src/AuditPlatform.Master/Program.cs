using AuditPlatform.Contracts.Sync;
using AuditPlatform.Evidence;
using AuditPlatform.Master;
using AuditPlatform.Master.Data;
using AuditPlatform.Master.Engagements;
using AuditPlatform.Master.Evidence;
using AuditPlatform.Master.Notifications;
using AuditPlatform.Master.Security;
using AuditPlatform.Master.Sync;
using AuditPlatform.Sync;
using AuditPlatform.Master.Auth;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

// `AuditPlatform.Master.dll admin <command>` runs an account-administration command and exits (see AdminCli).
var adminCommand = args is ["admin", ..] ? args[1..] : null;
var builder = WebApplication.CreateBuilder(adminCommand is null ? args : []);
if (adminCommand is not null)
{
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
}

builder.Services.Configure<MasterOptions>(builder.Configuration.GetSection(MasterOptions.Section));

// Connection string comes from configuration (environment variable ConnectionStrings__Master on the VM);
// it is never committed. See deploy/master/README.md.
var connectionString = builder.Configuration.GetConnectionString("Master")
    ?? throw new InvalidOperationException("ConnectionStrings:Master is not configured.");
builder.Services.AddMasterData(connectionString);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IEvidenceStore>(sp =>
    new FileSystemEvidenceStore(sp.GetRequiredService<IOptions<MasterOptions>>().Value.EvidenceRoot));
builder.Services.AddScoped<IEngagementAccess, EfEngagementAccess>();
// ADR-0004: field-level versioning with master arbitration, on PostgreSQL.
builder.Services.AddScoped<FieldSyncEngine>();
builder.Services.AddScoped<IDocumentSyncEngine>(sp => sp.GetRequiredService<FieldSyncEngine>());
builder.Services.AddScoped<SyncCoordinator>();
builder.Services.AddScoped<EngagementService>();
builder.Services.AddScoped<EngagementLifecycle>();
builder.Services.AddScoped<TemplateService>();
builder.Services.AddScoped<EvidenceService>();
builder.Services.AddSingleton<NotificationSignal>();
builder.Services.AddScoped<NotificationSender>();
builder.Services.AddHostedService<NotificationDispatcher>();

builder.AddAuditAuthentication();

builder.Services.AddSignalR(o =>
{
    o.MaximumReceiveMessageSize = 1024 * 1024;
    o.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

builder.Services.AddHealthChecks()
    .AddDbContextCheck<MasterDbContext>("postgres", tags: ["ready"]);

var app = builder.Build();

if (adminCommand is not null)
{
    return await AdminCli.RunAsync(app.Services, adminCommand, Console.Out, Console.In);
}

app.UseForwardedHeaders();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Liveness: process is up. Readiness: database reachable. Reverse proxy and monitoring use these.
app.MapHealthChecks("/healthz/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/healthz/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

app.MapAuthApi();
app.MapAdminApi();
app.MapAccountPages();
app.MapSyncApi();
app.MapDirectoryApi();
app.MapLifecycleApi();
app.MapEvidenceApi();

// A connection ends when its access token expires; the client reconnects with a fresh one, so a revoked session
// or disabled user cannot keep syncing over a socket opened earlier.
app.MapHub<SyncHub>(SyncHubRoutes.Path, o => o.CloseOnAuthenticationExpiration = true);

await app.RunAsync();
return 0;

/// <summary>Entry point marker for WebApplicationFactory in integration tests.</summary>
public partial class Program;
