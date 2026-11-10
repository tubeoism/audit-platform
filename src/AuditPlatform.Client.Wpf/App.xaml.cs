using System.IO;
using System.Windows;
using AuditPlatform.Client.Core;
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
        // Sign-in is pending ADR-0005; until then the transport connects without a token and the master answers 401.
        builder.Services.AddSingleton<ISyncTransport>(_ => new SignalRSyncTransport(options, () => Task.FromResult<string?>(null)));
        builder.Services.AddSingleton<ShellViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        _host = builder.Build();
        await _host.StartAsync();

        using (var scope = _host.Services.CreateScope())
        {
            // Local schema; replaced by SQLite migrations once the replica model is fixed by ADR-0004.
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
