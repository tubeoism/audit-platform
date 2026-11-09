using System.Net;
using AuditPlatform.Contracts.Sync;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuditPlatform.Master.Tests;

public sealed class MasterHostTests(MasterHostTests.Factory factory) : IClassFixture<MasterHostTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            // Never contacted by these tests: liveness excludes the database check.
            builder.UseSetting("ConnectionStrings:Master", "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none");
        }
    }

    [Fact]
    public async Task Liveness_endpoint_answers_without_database()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync(new Uri("/healthz/live", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Sync_hub_refuses_unauthenticated_callers()
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsync(new Uri($"{SyncHubRoutes.Path}/negotiate?negotiateVersion=1", UriKind.Relative), null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
