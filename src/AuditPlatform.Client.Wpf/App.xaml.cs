using System.IO;
using System.Windows;
using System.Net.Http;
using AuditPlatform.Client.Core;
using AuditPlatform.Client.Core.Auth;
using AuditPlatform.Client.Core.LocalStore;
using AuditPlatform.Client.Core.Sync;
using AuditPlatform.Client.Core.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AuditPlatform.Client.Wpf;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var builder = Host.CreateApplicationBuilder(e.Args);
        builder.Configuration.AddJsonFile("appsettings.json", optional: true);

        var options = new ClientOptions();
        builder.Configuration.GetSection(ClientOptions.Section).Bind(options);
        Directory.CreateDirectory(options.DataDirectory);
        options.ClientId = ClientIdentity.LoadOrCreate(options.DataDirectory);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddDbContext<LocalDbContext>(o => o.UseSqlite($"Data Source={options.DatabasePath}"));
        // ADR-0005: the session supplies and renews the access token. The sign-in window comes with the client UI;
        // until a user has signed in, the session has no token and the master answers 401.
        builder.Services.AddSingleton<IRefreshTokenStore>(_ => new DpapiRefreshTokenStore(options.DataDirectory));
        builder.Services.AddSingleton(sp => new AuthSession(new HttpClient { Timeout = TimeSpan.FromSeconds(20) }, options,
            sp.GetRequiredService<IRefreshTokenStore>(), sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<ISyncTransport>(sp =>
        {
            var session = sp.GetRequiredService<AuthSession>();
            return new SignalRSyncTransport(options, () => session.GetAccessTokenAsync());
        });
        // ADR-0004: the replica and the agent share one LocalDbContext per unit of work (a scope).
        builder.Services.AddScoped<LocalReplica>();
        builder.Services.AddScoped<ILocalChangeSink>(sp => sp.GetRequiredService<LocalReplica>());
        builder.Services.AddScoped<SyncAgent>();
        builder.Services.AddSingleton<ShellViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        _host = builder.Build();
        await _host.StartAsync();

        using (var scope = _host.Services.CreateScope())
        {
            // Local schema (outbox, replica log, documents). No client is deployed yet; SQLite migrations come with
            // the first client release, after which the schema can no longer be recreated.
            await scope.ServiceProvider.GetRequiredService<LocalDbContext>().Database.EnsureCreatedAsync();
        }

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }

        base.OnExit(e);
    }
}
