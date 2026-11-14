using System.IO;
using System.Net.Http;
using System.Windows;
using AuditPlatform.Client.Core;
using AuditPlatform.Client.Core.Auth;
using AuditPlatform.Client.Core.Directory;
using AuditPlatform.Client.Core.Evidence;
using AuditPlatform.Client.Core.LocalStore;
using AuditPlatform.Client.Core.Sync;
using AuditPlatform.Client.Core.ViewModels;
using AuditPlatform.Contracts.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AuditPlatform.Client.Wpf;

public partial class App : Application
{
    private IHost? _host;
    private MainWindow? _main;
    private SignInWindow? _signIn;

    private IServiceProvider Services => _host!.Services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var builder = Host.CreateApplicationBuilder(e.Args);
        builder.Configuration.AddJsonFile("appsettings.json", optional: true);

        var options = new ClientOptions();
        builder.Configuration.GetSection(ClientOptions.Section).Bind(options);
        System.IO.Directory.CreateDirectory(options.DataDirectory);
        options.ClientId = ClientIdentity.LoadOrCreate(options.DataDirectory);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IUiDispatcher>(new WpfDispatcher(this));

        // Each unit of work (a save on the form, a sync round) opens its own context; SQLite orders the writers.
        builder.Services.AddDbContextFactory<LocalDbContext>(o => o.UseSqlite($"Data Source={options.DatabasePath}"));
        builder.Services.AddSingleton<LocalUnitFactory>();

        // ADR-0005: the session supplies and renews the access token; the refresh token is kept under DPAPI.
        builder.Services.AddSingleton<IRefreshTokenStore>(_ => new DpapiRefreshTokenStore(options.DataDirectory));
        builder.Services.AddSingleton(sp => new AuthSession(new HttpClient { Timeout = TimeSpan.FromSeconds(20) }, options,
            sp.GetRequiredService<IRefreshTokenStore>(), sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<ISyncTransport>(sp =>
        {
            var session = sp.GetRequiredService<AuthSession>();
            return new SignalRSyncTransport(options, () => session.GetAccessTokenAsync());
        });
        builder.Services.AddSingleton<IDirectorySource, MasterDirectorySource>();

        // Evidence files take minutes on a slow line, so they do not share the short timeout of the sign-in client.
        builder.Services.AddSingleton<IEvidenceTransport>(sp =>
        {
            var session = sp.GetRequiredService<AuthSession>();
            return new MasterEvidenceTransport(new HttpClient { Timeout = TimeSpan.FromMinutes(30) }, options, () => session.GetAccessTokenAsync());
        });
        builder.Services.AddSingleton<IFileLauncher, ShellFileLauncher>();
        builder.Services.AddSingleton<LocalEvidence>();
        builder.Services.AddSingleton<SyncLoop>();
        builder.Services.AddSingleton<ShellViewModel>();
        builder.Services.AddTransient<SignInViewModel>();

        _host = builder.Build();
        await _host.StartAsync();
        Services.GetRequiredService<LocalEvidence>().ClearReadingCopies();

        LocalProfileRow? profile;
        await using (var db = await Services.GetRequiredService<IDbContextFactory<LocalDbContext>>().CreateDbContextAsync())
        {
            // ADR-0004 open item 2: the local schema is versioned from the first client release on.
            await db.Database.MigrateAsync();

            // Write-ahead log: the form keeps reading and saving while a sync round writes.
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
            profile = await DirectoryStore.LoadProfileAsync(db, CancellationToken.None);
        }

        var session = Services.GetRequiredService<AuthSession>();
        session.SignedOut += (_, _) => Dispatcher.BeginInvoke(ShowSignIn);
        Services.GetRequiredService<ShellViewModel>().SignedOut += (_, _) => ShowSignIn();

        // ADR-0005: with a stored session the client opens without the network; the master is asked when it is reachable.
        if (session.HasStoredSession && profile is not null)
        {
            await OpenShellAsync(new CurrentUser(profile.UserId, profile.Email, profile.DisplayName));
        }
        else
        {
            ShowSignIn();
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await Services.GetRequiredService<SyncLoop>().DisposeAsync();
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }

        base.OnExit(e);
    }

    private void ShowSignIn()
    {
        if (_signIn is not null)
        {
            _signIn.Activate();
            return;
        }

        var viewModel = Services.GetRequiredService<SignInViewModel>();
        viewModel.SignedIn += async (_, user) => await OnSignedInAsync(viewModel, user);
        _signIn = new SignInWindow(viewModel);
        _signIn.Closed += (_, _) => _signIn = null;
        _signIn.Show();

        // The main window goes after the sign-in window is up, so the application does not shut down in between.
        _main?.Close();
        _main = null;
    }

    private async Task OnSignedInAsync(SignInViewModel viewModel, UserInfo user)
    {
        await using (var db = await Services.GetRequiredService<IDbContextFactory<LocalDbContext>>().CreateDbContextAsync())
        {
            if (await SessionGate.WhyRefusedAsync(db, user.Id, CancellationToken.None) is { } refusal)
            {
                await Services.GetRequiredService<AuthSession>().SignOutAsync(CancellationToken.None);
                viewModel.Error = refusal;
                return;
            }

            await DirectoryStore.SaveProfileAsync(db, user, CancellationToken.None);
        }

        await OpenShellAsync(new CurrentUser(user.Id, user.Email, user.DisplayName));
    }

    private async Task OpenShellAsync(CurrentUser user)
    {
        var shell = Services.GetRequiredService<ShellViewModel>();
        _main = new MainWindow(shell);
        MainWindow = _main;
        _main.Show();
        _signIn?.Close();
        await shell.OpenAsync(user, CancellationToken.None);
    }
}
