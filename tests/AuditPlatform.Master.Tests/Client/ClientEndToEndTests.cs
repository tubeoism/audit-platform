using AuditPlatform.Client.Core;
using AuditPlatform.Client.Core.Auth;
using AuditPlatform.Client.Core.Directory;
using AuditPlatform.Client.Core.LocalStore;
using AuditPlatform.Client.Core.Sync;
using AuditPlatform.Client.Core.ViewModels;
using AuditPlatform.Client.Core.WorkingPapers;
using AuditPlatform.Contracts.Auth;
using AuditPlatform.Domain.Engagements;
using AuditPlatform.Master.Auth;
using AuditPlatform.Master.Tests.Auth;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Master.Tests.Client;

/// <summary>
/// The client as the WPF shell runs it, without the windows: sign-in view model, sync loop, engagement and
/// working-paper view models, migrated SQLite file. Against the real master on PostgreSQL, set up with the console
/// commands an administrator would type.
/// </summary>
public sealed class ClientEndToEndTests(AuthFactory factory) : IClassFixture<AuthFactory>
{
    private const string CashArea = """
        # Audit Area: Cash Management

        ## I. Audit Objectives

        *   Ensure that cash is real and recorded accurately.

        ## II. Detailed Audit Procedures

        ### 1. AUP-CSH-01: Reconciliation of Aggregated Data

        - **Procedure:** Reconcile cash balances on the Financial Statements with the General Ledger.
        - **Audit Content:**
            - Compare the balances of cash on hand with the General Ledger.
            - Check the arithmetic balance of the books.
        - **Evidence to Check:**
            - Balance Sheet.
            - Trial Balance.

        ### 2. AUP-CSH-03: Cash Count

        - **Procedure:** Perform a surprise cash count at the time of the audit.
        - **Audit Content:**
            - Physically count all cash kept in the safe.
        - **Evidence to Check:**
            - Cash Count Report.
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class MemoryStore : IRefreshTokenStore
    {
        private string? _token;
        public string? Load() => _token;
        public void Save(string refreshToken) => _token = refreshToken;
        public void Clear() => _token = null;
    }

    /// <summary>The tests reload the forms themselves, at the moments they choose.</summary>
    private sealed class NoDispatcher : IUiDispatcher
    {
        public void Post(Action action)
        {
        }
    }

    private sealed class FileDbFactory(string path) : IDbContextFactory<LocalDbContext>
    {
        public LocalDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite($"Data Source={path}").Options);
    }

    /// <summary>One laptop: its own database file, session, connection, sync loop and main window state.</summary>
    private sealed class Desk : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "audit-desk-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _http;
        private readonly AuthFactory _factory;
        private readonly FileDbFactory _contexts;

        public Desk(AuthFactory factory, TestUser user)
        {
            _factory = factory;
            User = user;
            System.IO.Directory.CreateDirectory(_directory);
            var options = new ClientOptions
            {
                MasterUrl = new Uri("http://localhost/"), ClientId = Guid.NewGuid(), DataDirectory = _directory, TemplateLanguage = "en",
                SyncInterval = TimeSpan.FromHours(1),
            };
            _http = factory.CreateClient();
            _contexts = new FileDbFactory(options.DatabasePath);
            Session = new AuthSession(_http, options, new MemoryStore(), factory.Clock);
            Transport = new SignalRSyncTransport(options, () => Session.GetAccessTokenAsync(), o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            });
            var units = new LocalUnitFactory(_contexts, Transport, options, factory.Clock);
            Loop = new SyncLoop(units, Transport, new MasterDirectorySource(Session), options);
            Shell = new ShellViewModel(units, Loop, Session, options, new NoDispatcher());
        }

        public TestUser User { get; }
        public AuthSession Session { get; }
        public SignalRSyncTransport Transport { get; }
        public SyncLoop Loop { get; }
        public ShellViewModel Shell { get; }
        public EngagementViewModel Engagement => Shell.Engagement!;
        public WorkingPaperViewModel Paper => Shell.Engagement!.Paper!;

        public LocalDbContext OpenDb() => _contexts.CreateDbContext();

        /// <summary>What the application does between start-up and the main window.</summary>
        public async Task<string?> StartAsync()
        {
            await using var db = OpenDb();
            await db.Database.MigrateAsync(Ct);
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", Ct);

            var signIn = new SignInViewModel(Session) { Email = User.Email, Password = User.Password, Code = _factory.NextCode(User.Secret) };
            UserInfo? signedIn = null;
            signIn.SignedIn += (_, user) => signedIn = user;
            await signIn.SignInCommand.ExecuteAsync(null);
            if (signedIn is null)
            {
                return signIn.Error;
            }

            if (await SessionGate.WhyRefusedAsync(db, signedIn.Id, Ct) is { } refusal)
            {
                return refusal;
            }

            await DirectoryStore.SaveProfileAsync(db, signedIn, Ct);
            await Shell.OpenAsync(new CurrentUser(signedIn.Id, signedIn.Email, signedIn.DisplayName), Ct);
            await SyncAsync();
            return null;
        }

        /// <summary>One sync round, then the forms reload, as they do when the loop reports a change.</summary>
        public async Task SyncAsync()
        {
            await Loop.RunOnceAsync(Ct);
            await Shell.ReloadAsync(Ct);
        }

        public FieldViewModel Field(string key) => Paper.Fields.Single(f => f.Key == key);

        public async Task TypeAndLeaveAsync(string key, string text)
        {
            Field(key).Text = text;
            await Paper.SaveFieldCommand.ExecuteAsync(Field(key));
        }

        public async ValueTask DisposeAsync()
        {
            await Loop.DisposeAsync();
            await Transport.DisposeAsync();
            Session.Dispose();
            _http.Dispose();
            SqliteConnection.ClearAllPools();
            System.IO.Directory.Delete(_directory, recursive: true);
        }
    }

    private async Task<string> AdminAsync(string? stdin, params string[] args)
    {
        using var output = new StringWriter();
        using var input = new StringReader(stdin ?? "");
        var exit = await AdminCli.RunAsync(factory.Services, args, output, input);
        Assert.True(exit == 0, output.ToString());
        return output.ToString();
    }

    [Fact]
    public async Task Two_auditors_work_on_one_paper_through_the_forms_and_a_conflict_is_decided_by_its_preparer()
    {
        factory.RequireDatabase();
        factory.Clock.CatchUpWithRealTime();
        var an = await factory.EnrolAsync();
        var binh = await factory.EnrolAsync();
        var reviewer = await factory.EnrolAsync();
        var outsider = await factory.EnrolAsync();

        // ----- The administrator sets the engagement up from the console and loads the methodology.
        var code = "ENG-" + Guid.NewGuid().ToString("N")[..8];
        await AdminAsync(null, "engagement", "create", code, "Công ty ABC", "AUP-2026-014", "2026-01-01", "2026-12-31");
        await AdminAsync(null, "engagement", "member", code, an.Email, "preparer");
        await AdminAsync(null, "engagement", "member", code, binh.Email, "preparer");
        await AdminAsync(null, "engagement", "member", code, reviewer.Email, "managerqa");
        Assert.Contains("thêm 2", await AdminAsync(CashArea, "templates", "import", "en"), StringComparison.Ordinal);
        // Importing the same text again adds nothing; a changed procedure becomes version 2 and version 1 is kept.
        Assert.Contains("thêm 0, không đổi 2", await AdminAsync(CashArea, "templates", "import", "en"), StringComparison.Ordinal);
        Assert.Contains("AUP-CSH-03 v2", await AdminAsync(CashArea.Replace("surprise cash count", "surprise count of cash", StringComparison.Ordinal),
            "templates", "import", "en"), StringComparison.Ordinal);
        Assert.Contains(code, await AdminAsync(null, "engagement", "list"), StringComparison.Ordinal);

        await using var deskAn = new Desk(factory, an);
        await using var deskBinh = new Desk(factory, binh);
        await using var deskReviewer = new Desk(factory, reviewer);
        await using var deskOutsider = new Desk(factory, outsider);
        Assert.Null(await deskAn.StartAsync());
        Assert.Null(await deskBinh.StartAsync());
        Assert.Null(await deskReviewer.StartAsync());
        Assert.Null(await deskOutsider.StartAsync());

        // ----- Each user is shown their own engagements, and nobody else's.
        Assert.Equal(code, Assert.Single(deskAn.Shell.Engagements).Code);
        Assert.Empty(deskOutsider.Shell.Engagements);
        Assert.Contains("Preparer", deskAn.Engagement.Detail, StringComparison.Ordinal);
        Assert.True(deskAn.Engagement.CanCreate);
        Assert.False(deskReviewer.Engagement.CanCreate);

        // ----- An starts a working paper from the library, offline as far as the form is concerned, and fills it in.
        Assert.Equal("CSH", Assert.Single(deskAn.Engagement.Areas).Code);
        deskAn.Engagement.SelectedTemplate = deskAn.Engagement.AreaTemplates.Single(t => t.Template.Code == "AUP-CSH-03");
        Assert.Equal(2, deskAn.Engagement.SelectedTemplate.Template.Version);
        await deskAn.Engagement.CreatePaperCommand.ExecuteAsync(null);
        Assert.Contains("mới chỉ có trên máy bạn", deskAn.Paper.Notice, StringComparison.Ordinal);
        Assert.Equal("AUP-CSH-03 · Cash Count", deskAn.Paper.Title);
        Assert.Equal("Perform a surprise count of cash at the time of the audit.", deskAn.Paper.Procedure);
        Assert.Equal(["Cash Count Report."], deskAn.Paper.EvidenceToCheck);
        Assert.Equal(
            [WorkingPaperLayout.StepKey(1), WorkingPaperLayout.Population, WorkingPaperLayout.Sample, WorkingPaperLayout.Exceptions, WorkingPaperLayout.Findings],
            deskAn.Paper.Fields.Select(f => f.Key));
        Assert.Equal("1. Physically count all cash kept in the safe.", deskAn.Field(WorkingPaperLayout.StepKey(1)).Label);

        await deskAn.TypeAndLeaveAsync(WorkingPaperLayout.Exceptions, "0");
        Assert.Equal("Chưa gửi lên máy chủ", deskAn.Field(WorkingPaperLayout.Exceptions).StateText);
        // A second paper for the same procedure is not offered twice.
        await deskAn.Engagement.CreatePaperCommand.ExecuteAsync(null);
        Assert.Contains("đã có giấy làm việc", deskAn.Engagement.Error, StringComparison.Ordinal);

        await deskAn.SyncAsync();
        Assert.Null(deskAn.Paper.Notice);
        Assert.StartsWith("Đã đồng bộ", deskAn.Field(WorkingPaperLayout.Exceptions).StateText, StringComparison.Ordinal);
        Assert.Equal(0, deskAn.Loop.Status.Pending);

        // ----- Binh receives it and opens the same form; the reviewer sees it read-only.
        await deskBinh.SyncAsync();
        deskBinh.Engagement.SelectedPaper = Assert.Single(deskBinh.Engagement.Papers);
        await deskBinh.Paper.ReloadAsync(Ct);
        Assert.Equal("0", deskBinh.Field(WorkingPaperLayout.Exceptions).Text);
        Assert.True(deskBinh.Paper.CanEdit);
        await deskReviewer.SyncAsync();
        deskReviewer.Engagement.SelectedPaper = Assert.Single(deskReviewer.Engagement.Papers);
        await deskReviewer.Paper.ReloadAsync(Ct);
        Assert.False(deskReviewer.Paper.CanEdit);
        Assert.Contains("soát xét", deskReviewer.Paper.Notice, StringComparison.Ordinal);

        // ----- Both change the same box. Binh is still typing when An's value arrives on Binh's laptop.
        deskBinh.Field(WorkingPaperLayout.Exceptions).Text = "3";
        await deskAn.TypeAndLeaveAsync(WorkingPaperLayout.Exceptions, "2");
        await deskAn.SyncAsync();
        await deskBinh.SyncAsync();
        // What Binh typed is not overwritten by the sync, and the form says the box has moved underneath.
        Assert.Equal("3", deskBinh.Field(WorkingPaperLayout.Exceptions).Text);
        Assert.True(deskBinh.Field(WorkingPaperLayout.Exceptions).NeedsAttention);
        // Binh's save is judged against the value Binh was looking at ("0"), so the master flags it instead of taking it.
        await deskBinh.Paper.SaveFieldCommand.ExecuteAsync(deskBinh.Field(WorkingPaperLayout.Exceptions));
        await deskBinh.SyncAsync();
        Assert.Equal("2", deskBinh.Field(WorkingPaperLayout.Exceptions).Text);
        var seenByBinh = Assert.Single(deskBinh.Paper.Conflicts);
        Assert.Equal(("3", "2", false), (seenByBinh.ProposedValue, seenByBinh.CurrentValue, seenByBinh.CanResolve));
        Assert.Contains("1 xung đột", Assert.Single(deskBinh.Engagement.Papers).Flags, StringComparison.Ordinal);

        // ----- Everyone sees both values; only the paper's preparer is offered the decision.
        await deskReviewer.SyncAsync();
        Assert.False(Assert.Single(deskReviewer.Paper.Conflicts).CanResolve);
        await deskAn.SyncAsync();
        var seenByAn = Assert.Single(deskAn.Paper.Conflicts);
        Assert.Equal(("3", "2", true, "Exceptions noted"), (seenByAn.ProposedValue, seenByAn.CurrentValue, seenByAn.CanResolve, seenByAn.FieldLabel));
        Assert.Equal("Có xung đột chờ xử lý", deskAn.Field(WorkingPaperLayout.Exceptions).StateText);

        // A button that is not offered does nothing when it is forced.
        await deskBinh.Paper.UseProposedCommand.ExecuteAsync(seenByBinh);
        await deskBinh.SyncAsync();
        Assert.Single(deskBinh.Paper.Conflicts);

        await deskAn.Paper.UseProposedCommand.ExecuteAsync(seenByAn);
        await deskAn.SyncAsync();
        await deskBinh.SyncAsync();
        Assert.Empty(deskAn.Paper.Conflicts);
        Assert.Empty(deskBinh.Paper.Conflicts);
        Assert.Equal(("3", "3"), (deskAn.Field(WorkingPaperLayout.Exceptions).Text, deskBinh.Field(WorkingPaperLayout.Exceptions).Text));
        Assert.Equal(0, deskBinh.Loop.Status.NeedsAttention);

        // ----- Submitting for review travels the same way.
        Assert.True(deskAn.Paper.CanSubmit);
        await deskAn.Paper.SubmitCommand.ExecuteAsync(null);
        Assert.False(deskAn.Paper.CanSubmit);
        await deskAn.SyncAsync();
        await deskReviewer.SyncAsync();
        Assert.Equal("Chờ soát xét", deskReviewer.Paper.StatusText);

        // ----- The record on the master: who did what, and the engagement set-up, in the audit log.
        var trail = await factory.AuditAsync();
        Assert.Single(trail, a => a.Action == "engagement.created" && a.PayloadJson.Contains(code, StringComparison.Ordinal));
        Assert.Equal(3, trail.Count(a => a.Action == "engagement.member.added" && a.PayloadJson.Contains(code, StringComparison.Ordinal)));
        Assert.Equal(2, trail.Count(a => a.Action == "templates.imported"));
        Assert.Single(trail, a => a.Action == "sync.resolve.accepted" && a.ActorUserId == an.Id);
        // Binh's forced button never left the laptop; the master's own refusal of such a change is scenario S2.
        Assert.DoesNotContain(trail, a => a.Action.StartsWith("sync.resolve", StringComparison.Ordinal) && a.ActorUserId == binh.Id);

        // ----- Signing out is refused while this laptop still holds something the master has not settled.
        deskBinh.Field(WorkingPaperLayout.Findings).Text = "Chênh lệch 3 phiếu chi.";
        await deskBinh.Paper.SaveFieldCommand.ExecuteAsync(deskBinh.Field(WorkingPaperLayout.Findings));
        var signedOut = false;
        deskBinh.Shell.SignedOut += (_, _) => signedOut = true;
        await deskBinh.Shell.SignOutCommand.ExecuteAsync(null);
        Assert.False(signedOut);
        Assert.Contains("chưa gửi", deskBinh.Shell.LastError, StringComparison.Ordinal);
        await using (var db = deskBinh.OpenDb())
        {
            // ... and another person signing in on that laptop would be refused for the same reason.
            Assert.Contains("thay đổi chưa gửi", await SessionGate.WhyRefusedAsync(db, an.Id, Ct), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Directory_api_needs_a_session_and_the_console_refuses_bad_input()
    {
        factory.RequireDatabase();
        using var anonymous = factory.CreateClient();
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await anonymous.GetAsync(new Uri("/api/engagements", UriKind.Relative), Ct)).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await anonymous.GetAsync(new Uri("/api/templates", UriKind.Relative), Ct)).StatusCode);

        using var output = new StringWriter();
        using var empty = new StringReader("no procedures here");
        Assert.Equal(1, await AdminCli.RunAsync(factory.Services, ["templates", "import", "vi"], output, empty));
        Assert.Equal(1, await AdminCli.RunAsync(factory.Services, ["templates", "import", "fr"], output, empty));
        Assert.Equal(1, await AdminCli.RunAsync(factory.Services, ["engagement", "create", "X-1", "Client", "C-1", "2026-12-31", "2026-01-01"], output));
        Assert.Equal(1, await AdminCli.RunAsync(factory.Services, ["engagement", "member", "NO-SUCH", (await factory.EnrolAsync()).Email, "preparer"], output));
        Assert.Equal(2, await AdminCli.RunAsync(factory.Services, ["engagement", "member", "X-1", "a@audit.io.vn", "owner"], output));
        Assert.Contains("template_empty", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("engagement_unknown", output.ToString(), StringComparison.Ordinal);
    }
}
