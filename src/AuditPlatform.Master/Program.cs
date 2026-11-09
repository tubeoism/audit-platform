using AuditPlatform.Contracts.Sync;
using AuditPlatform.Evidence;
using AuditPlatform.Master;
using AuditPlatform.Master.Data;
using AuditPlatform.Master.Security;
using AuditPlatform.Master.Sync;
using AuditPlatform.Sync;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddSingleton<IDocumentSyncEngine, SyncEngineNotSelected>();
builder.Services.AddScoped<SyncCoordinator>();

builder.Services
    .AddAuthentication(PendingAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, PendingAuthenticationHandler>(PendingAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization();

builder.Services.AddSignalR(o =>
{
    o.MaximumReceiveMessageSize = 1024 * 1024;
    o.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

builder.Services.AddHealthChecks()
    .AddDbContextCheck<MasterDbContext>("postgres", tags: ["ready"]);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Liveness: process is up. Readiness: database reachable. Reverse proxy and monitoring use these.
app.MapHealthChecks("/healthz/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/healthz/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

app.MapHub<SyncHub>(SyncHubRoutes.Path);

app.Run();

/// <summary>Entry point marker for WebApplicationFactory in integration tests.</summary>
public partial class Program;
