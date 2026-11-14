using AuditPlatform.Client.Core;
using AuditPlatform.Client.Core.Auth;
using AuditPlatform.Client.Core.Directory;
using AuditPlatform.Client.Core.Evidence;
using AuditPlatform.Sync;
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

    private sealed class Opened : IFileLauncher
    {
        public List<string> Paths { get; } = [];

        public void Open(string path) => Paths.Add(path);
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
        private readonly LocalUnitFactory _units;

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
            var units = _units = new LocalUnitFactory(_contexts, Transport, options, factory.Clock);
            _evidenceHttp = factory.CreateClient();
            Files = new MasterEvidenceTransport(_evidenceHttp, options, () => Session.GetAccessTokenAsync());
            Evidence = new LocalEvidence(options, Files, Launcher);
            Loop = new SyncLoop(units, Transport, new MasterDirectorySource(Session), options, Evidence);
            Shell = new ShellViewModel(units, Loop, Session, options, new NoDispatcher(), Evidence);
        }

        private readonly HttpClient _evidenceHttp;

        public MasterEvidenceTransport Files { get; }
        public LocalEvidence Evidence { get; }
        public Opened Launcher { get; } = new();

        /// <summary>A file somewhere on the laptop, outside the application's own folders.</summary>
        public string Write(string name, string content)
        {
            var folder = Path.Combine(_directory, "desktop");
            System.IO.Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, content);
            return path;
        }

        public string DataDirectory => _directory;
        public TestUser User { get; }
        public AuthSession Session { get; }
        public SignalRSyncTransport Transport { get; }
        public SyncLoop Loop { get; }
        public ShellViewModel Shell { get; }
        public EngagementViewModel Engagement => Shell.Engagement!;
        public WorkingPaperViewModel Paper => Shell.Engagement!.Paper!;

        public LocalDbContext OpenDb() => _contexts.CreateDbContext();

        public Task<LocalUnit> OpenUnitAsync() => _units.OpenAsync(Ct);

        public Guid UserId => Session.User!.Id;

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
            _evidenceHttp.Dispose();
            SqliteConnection.ClearAllPools();
            foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

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
        // The audit log is shared with the other tests of this class, so imports are counted from here.
        var importsBefore = (await factory.AuditAsync()).Count(a => a.Action == "templates.imported");
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
        Assert.Equal(importsBefore + 2, trail.Count(a => a.Action == "templates.imported"));
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
    public async Task Review_notes_are_raised_answered_and_cleared_and_the_paper_is_signed_off_with_a_fresh_code_on_the_forms()
    {
        factory.RequireDatabase();
        factory.Clock.CatchUpWithRealTime();
        var an = await factory.EnrolAsync();
        var qa = await factory.EnrolAsync();
        var japan = await factory.EnrolAsync();
        var code = "ENG-" + Guid.NewGuid().ToString("N")[..8];
        await AdminAsync(null, "engagement", "create", code, "Công ty ABC", "AUP-2026-016", "2026-01-01", "2026-12-31");
        await AdminAsync(null, "engagement", "member", code, an.Email, "preparer");
        await AdminAsync(null, "engagement", "member", code, qa.Email, "managerqa");
        await AdminAsync(null, "engagement", "member", code, japan.Email, "japandesk");
        // Its own procedure, so that the tests of this class can run in either order on the shared library.
        await AdminAsync(CashArea[..CashArea.IndexOf("### 1.", StringComparison.Ordinal)] + """
            ### 9. AUP-CSH-91: Petty Cash

            - **Procedure:** Review petty cash vouchers.
            - **Audit Content:**
                - Inspect each voucher for approval.
            - **Evidence to Check:**
                - Voucher file.
            """, "templates", "import", "en");

        await using var deskAn = new Desk(factory, an);
        await using var deskQa = new Desk(factory, qa);
        await using var deskJapan = new Desk(factory, japan);
        Assert.Null(await deskAn.StartAsync());
        Assert.Null(await deskQa.StartAsync());
        Assert.Null(await deskJapan.StartAsync());

        // ----- An prepares the paper and submits it for review.
        deskAn.Engagement.SelectedTemplate = deskAn.Engagement.AreaTemplates.Single(t => t.Template.Code == "AUP-CSH-91");
        await deskAn.Engagement.CreatePaperCommand.ExecuteAsync(null);
        var step = WorkingPaperLayout.StepKey(1);
        await deskAn.TypeAndLeaveAsync(step, "Đã kiểm tra 40 phiếu chi, đủ chữ ký phê duyệt.");
        await deskAn.TypeAndLeaveAsync(WorkingPaperLayout.Exceptions, "0");
        await deskAn.Paper.SubmitCommand.ExecuteAsync(null);
        await deskAn.SyncAsync();
        // The preparer reviews nothing: no note form, no sign-off.
        Assert.False(deskAn.Paper.CanRaiseNote);
        Assert.False(deskAn.Paper.CanSignOff);

        // ----- The Manager QA reviewer sees the paper, may raise notes and may sign, and nothing stands in the way yet.
        await deskQa.SyncAsync();
        deskQa.Engagement.SelectedPaper = Assert.Single(deskQa.Engagement.Papers);
        await deskQa.Paper.ReloadAsync(Ct);
        Assert.Equal("Chờ soát xét", deskQa.Paper.StatusText);
        Assert.True(deskQa.Paper.CanRaiseNote);
        Assert.True(deskQa.Paper.CanSignOff);
        Assert.Equal("Ký soát xét cấp 1 (Manager QA)", deskQa.Paper.SignOffTitle);
        Assert.Null(deskQa.Paper.SignOffBlocker);
        Assert.True(deskQa.Paper.SignOffCommand.CanExecute(null));
        // The Japan Desk reviewer cannot sign yet: the paper is not at their level.
        await deskJapan.SyncAsync();
        deskJapan.Engagement.SelectedPaper = Assert.Single(deskJapan.Engagement.Papers);
        await deskJapan.Paper.ReloadAsync(Ct);
        Assert.False(deskJapan.Paper.CanSignOff);

        // ----- The reviewer raises a note offline: it shows as unsent, and signing waits for the master to have it.
        deskQa.Paper.NewNoteField = deskQa.Field(WorkingPaperLayout.Exceptions);
        deskQa.Paper.NewNoteText = "  Giải thích vì sao không có ngoại lệ khi 3 phiếu thiếu chữ ký?  ";
        await deskQa.Paper.RaiseNoteCommand.ExecuteAsync(null);
        Assert.Null(deskQa.Paper.Error);
        Assert.Equal("", deskQa.Paper.NewNoteText);
        var raised = Assert.Single(deskQa.Paper.Notes);
        Assert.Equal(("Exceptions noted", "Giải thích vì sao không có ngoại lệ khi 3 phiếu thiếu chữ ký?", "Chưa gửi lên máy chủ"),
            (raised.FieldLabel, raised.Text, raised.StateText));
        Assert.Contains("chưa gửi", deskQa.Paper.SignOffBlocker, StringComparison.Ordinal);
        Assert.False(deskQa.Paper.SignOffCommand.CanExecute(null));
        await deskQa.SyncAsync();
        raised = Assert.Single(deskQa.Paper.Notes);
        Assert.Equal("Chờ người lập trả lời", raised.StateText);
        Assert.Contains("1 ý kiến chưa xử lý", deskQa.Paper.SignOffBlocker, StringComparison.Ordinal);
        Assert.False(raised.CanClear);
        // A reply the reviewer forces on their own note is refused on the form, as it would be by the master.
        Assert.False(raised.CanRespond);

        // ----- An sees the note, answers it; the reviewer reads the answer and closes the note.
        await deskAn.SyncAsync();
        var seen = Assert.Single(deskAn.Paper.Notes);
        Assert.True(seen.CanRespond);
        Assert.False(seen.CanClear);
        await deskAn.Paper.RespondCommand.ExecuteAsync(seen);
        Assert.Contains("Nhập nội dung", deskAn.Paper.Error, StringComparison.Ordinal);
        seen.ResponseText = "Ba phiếu thiếu chữ ký đã được bổ sung ngày 05/01, xem file đính kèm.";
        await deskAn.Paper.RespondCommand.ExecuteAsync(seen);
        Assert.Null(deskAn.Paper.Error);
        Assert.Equal("", seen.ResponseText);
        Assert.Contains("chưa gửi lên máy chủ", Assert.Single(deskAn.Paper.Notes).Responses.Single(), StringComparison.Ordinal);
        await deskAn.SyncAsync();

        await deskQa.SyncAsync();
        raised = Assert.Single(deskQa.Paper.Notes);
        Assert.Equal("Đã có trả lời; chờ người nêu ý kiến xác nhận", raised.StateText);
        Assert.StartsWith("Test User: Ba phiếu", Assert.Single(raised.Responses), StringComparison.Ordinal);
        Assert.True(raised.CanClear);
        Assert.NotNull(deskQa.Paper.SignOffBlocker);
        await deskQa.Paper.ClearNoteCommand.ExecuteAsync(raised);
        await deskQa.SyncAsync();
        Assert.Equal("Đã xử lý xong", Assert.Single(deskQa.Paper.Notes).StateText);
        Assert.Equal("Tất cả 1 ý kiến đã xử lý xong.", deskQa.Paper.NotesSummary);
        Assert.Null(deskQa.Paper.SignOffBlocker);

        // ----- Signing needs a fresh code: a wrong one signs nothing, a missing one is asked for.
        await deskQa.Paper.SignOffCommand.ExecuteAsync(null);
        Assert.Contains("6 chữ số", deskQa.Paper.Error, StringComparison.Ordinal);
        deskQa.Paper.SignOffCode = "000000";
        await deskQa.Paper.SignOffCommand.ExecuteAsync(null);
        Assert.NotNull(deskQa.Paper.Error);
        await deskQa.SyncAsync();
        Assert.Equal("Chờ soát xét", deskQa.Paper.StatusText);
        Assert.Empty(deskQa.Paper.SignOffs);

        deskQa.Paper.SignOffCode = factory.NextCode(qa.Secret);
        await deskQa.Paper.SignOffCommand.ExecuteAsync(null);
        Assert.Null(deskQa.Paper.Error);
        Assert.Contains("Đã ký", deskQa.Paper.SignOffMessage, StringComparison.Ordinal);
        await deskQa.SyncAsync();
        Assert.Equal("Đã ký cấp 1 (Manager QA)", deskQa.Paper.StatusText);
        Assert.Contains("còn hiệu lực", Assert.Single(deskQa.Paper.SignOffs), StringComparison.Ordinal);
        Assert.False(deskQa.Paper.CanSignOff);

        // ----- The Japan Desk reviewer signs the second level the same way.
        await deskJapan.SyncAsync();
        Assert.True(deskJapan.Paper.CanSignOff);
        Assert.Equal("Ký soát xét cấp 2 (Japan Desk)", deskJapan.Paper.SignOffTitle);
        Assert.Null(deskJapan.Paper.SignOffBlocker);
        deskJapan.Paper.SignOffCode = factory.NextCode(japan.Secret);
        await deskJapan.Paper.SignOffCommand.ExecuteAsync(null);
        Assert.Null(deskJapan.Paper.Error);
        await deskJapan.SyncAsync();
        Assert.Equal("Đã ký cấp 2 (Japan Desk)", deskJapan.Paper.StatusText);
        Assert.Equal(2, deskJapan.Paper.SignOffs.Count);

        // ----- An changes the content afterwards: the seal breaks on every laptop and review starts again.
        await deskAn.SyncAsync();
        Assert.Equal("Đã ký cấp 2 (Japan Desk)", deskAn.Paper.StatusText);
        await deskAn.TypeAndLeaveAsync(WorkingPaperLayout.Exceptions, "3");
        await deskAn.SyncAsync();
        await deskQa.SyncAsync();
        Assert.Equal("Chờ soát xét", deskQa.Paper.StatusText);
        Assert.All(deskQa.Paper.SignOffs, line => Assert.Contains("ĐÃ MẤT HIỆU LỰC", line, StringComparison.Ordinal));
        Assert.True(deskQa.Paper.CanSignOff);

        // ----- On the master: each step is on the audit log under the right person.
        var trail = await factory.AuditAsync();
        Assert.Single(trail, a => a.Action == "sync.note-raise.accepted" && a.ActorUserId == qa.Id);
        Assert.Single(trail, a => a.Action == "sync.note-respond.accepted" && a.ActorUserId == an.Id);
        Assert.Single(trail, a => a.Action == "sync.note-clear.accepted" && a.ActorUserId == qa.Id);
        Assert.Single(trail, a => a.Action == "sync.sign-off.accepted" && a.ActorUserId == qa.Id);
        Assert.Single(trail, a => a.Action == "sync.sign-off.accepted" && a.ActorUserId == japan.Id);
        Assert.Contains(trail, a => a.Action == "sync.seal.broken");
    }

    [Fact]
    public async Task The_engagement_manager_moves_the_phases_and_locks_the_engagement_from_the_forms_with_a_fresh_code()
    {
        factory.RequireDatabase();
        factory.Clock.CatchUpWithRealTime();
        var an = await factory.EnrolAsync();
        var qa = await factory.EnrolAsync();
        var japan = await factory.EnrolAsync();
        var lead = await factory.EnrolAsync();
        var code = "ENG-" + Guid.NewGuid().ToString("N")[..8];
        await AdminAsync(null, "engagement", "create", code, "Công ty ABC", "AUP-2026-017", "2026-01-01", "2026-12-31");
        await AdminAsync(null, "engagement", "member", code, an.Email, "preparer");
        await AdminAsync(null, "engagement", "member", code, qa.Email, "managerqa");
        await AdminAsync(null, "engagement", "member", code, japan.Email, "japandesk");
        await AdminAsync(null, "engagement", "member", code, lead.Email, "engagementmanager");
        await AdminAsync(CashArea[..CashArea.IndexOf("### 1.", StringComparison.Ordinal)] + """
            ### 9. AUP-CSH-92: Bank Reconciliation

            - **Procedure:** Agree the bank reconciliation to the statement.
            - **Audit Content:**
                - Re-perform the reconciliation.
            - **Evidence to Check:**
                - Bank statement.
            """, "templates", "import", "en");

        await using var deskAn = new Desk(factory, an);
        await using var deskQa = new Desk(factory, qa);
        await using var deskJapan = new Desk(factory, japan);
        await using var deskLead = new Desk(factory, lead);
        Assert.Null(await deskAn.StartAsync());
        Assert.Null(await deskQa.StartAsync());
        Assert.Null(await deskJapan.StartAsync());
        Assert.Null(await deskLead.StartAsync());

        // ----- Only the manager is offered the phases; a new engagement starts in Planning and can only start fieldwork.
        Assert.False(deskAn.Engagement.ShowLifecycle);
        Assert.False(deskQa.Engagement.ShowLifecycle);
        Assert.True(deskLead.Engagement.ShowLifecycle);
        Assert.Equal([EngagementStatus.Fieldwork], deskLead.Engagement.Phases.Select(p => p.Target));
        Assert.False(deskLead.Engagement.ShowLock);

        // ----- The preparer prepares a paper and the two reviewers sign it off, as in any engagement.
        deskAn.Engagement.SelectedTemplate = deskAn.Engagement.AreaTemplates.Single(t => t.Template.Code == "AUP-CSH-92");
        await deskAn.Engagement.CreatePaperCommand.ExecuteAsync(null);
        await deskAn.TypeAndLeaveAsync(WorkingPaperLayout.StepKey(1), "Đã đối chiếu số dư ngân hàng, không chênh lệch.");
        await deskAn.TypeAndLeaveAsync(WorkingPaperLayout.Exceptions, "0");
        await deskAn.Paper.SubmitCommand.ExecuteAsync(null);
        await deskAn.SyncAsync();

        await deskLead.Engagement.AdvanceCommand.ExecuteAsync(Assert.Single(deskLead.Engagement.Phases));
        Assert.Equal("Đã chuyển sang Fieldwork.", deskLead.Engagement.LifecycleMessage);
        Assert.StartsWith("Fieldwork", deskLead.Engagement.Detail, StringComparison.Ordinal);
        Assert.Equal([EngagementStatus.Review], deskLead.Engagement.Phases.Select(p => p.Target));
        await deskLead.Engagement.AdvanceCommand.ExecuteAsync(deskLead.Engagement.Phases.Single());
        await deskLead.Engagement.AdvanceCommand.ExecuteAsync(deskLead.Engagement.Phases.Single(p => p.Target == EngagementStatus.Reporting));
        Assert.True(deskLead.Engagement.ShowLock);
        Assert.Equal([EngagementStatus.Review], deskLead.Engagement.Phases.Select(p => p.Target));

        // ----- With the paper still in review the lock is not offered; what is missing is said in words.
        await deskLead.Engagement.RefreshLockCommand.ExecuteAsync(null);
        Assert.False(deskLead.Engagement.LockReady);
        Assert.Contains("AUP-CSH-92", Assert.Single(deskLead.Engagement.LockBlockers), StringComparison.Ordinal);
        deskLead.Engagement.LockUnderstood = true;
        deskLead.Engagement.LockCode = "123456";
        Assert.False(deskLead.Engagement.LockCommand.CanExecute(null));

        foreach (var (desk, user) in new[] { (deskQa, qa), (deskJapan, japan) })
        {
            await desk.SyncAsync();
            desk.Engagement.SelectedPaper = Assert.Single(desk.Engagement.Papers);
            await desk.Paper.ReloadAsync(Ct);
            desk.Paper.SignOffCode = factory.NextCode(user.Secret);
            await desk.Paper.SignOffCommand.ExecuteAsync(null);
            Assert.Null(desk.Paper.Error);
            await desk.SyncAsync();
        }

        // ----- Everything is signed: the manager can lock, but only with a fresh, valid code and a ticked acknowledgement.
        await deskLead.Engagement.RefreshLockCommand.ExecuteAsync(null);
        Assert.True(deskLead.Engagement.LockReady);
        Assert.Empty(deskLead.Engagement.LockBlockers);
        Assert.Contains("Đủ điều kiện", deskLead.Engagement.LockSummary, StringComparison.Ordinal);
        deskLead.Engagement.LockUnderstood = false;
        deskLead.Engagement.LockCode = factory.NextCode(lead.Secret);
        Assert.False(deskLead.Engagement.LockCommand.CanExecute(null));
        deskLead.Engagement.LockUnderstood = true;
        deskLead.Engagement.LockCode = "000000";
        await deskLead.Engagement.LockCommand.ExecuteAsync(null);
        Assert.NotNull(deskLead.Engagement.LifecycleMessage);
        Assert.Equal("", deskLead.Engagement.LockCode);
        Assert.DoesNotContain("khoá", deskLead.Engagement.Detail, StringComparison.Ordinal);

        deskLead.Engagement.LockCode = factory.NextCode(lead.Secret);
        await deskLead.Engagement.LockCommand.ExecuteAsync(null);
        Assert.Equal("Engagement đã khoá. Từ giờ chỉ xem.", deskLead.Engagement.LifecycleMessage);
        Assert.Contains("đã khoá, chỉ xem", deskLead.Engagement.Detail, StringComparison.Ordinal);
        Assert.False(deskLead.Engagement.ShowLifecycle);
        Assert.False(deskLead.Engagement.ShowLock);
        Assert.Empty(deskLead.Engagement.Phases);

        // ----- The others learn of it from the master, and their forms turn read-only.
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!deskAn.Engagement.Detail.Contains("đã khoá", StringComparison.Ordinal) && DateTime.UtcNow < until)
        {
            await Task.Delay(100, Ct);
            await deskAn.SyncAsync();
        }

        Assert.Contains("đã khoá, chỉ xem", deskAn.Engagement.Detail, StringComparison.Ordinal);
        Assert.False(deskAn.Engagement.CanCreate);
        deskAn.Engagement.SelectedPaper = Assert.Single(deskAn.Engagement.Papers);
        await deskAn.Paper.ReloadAsync(Ct);
        Assert.False(deskAn.Paper.CanEdit);

        var trail = (await factory.AuditAsync()).Where(a => a.ActorUserId == lead.Id).Select(a => a.Action).ToList();
        Assert.Equal(3, trail.Count(a => a == "engagement.status.changed"));
        Assert.Single(trail, a => a == "engagement.locked");
    }

    [Fact]
    public async Task A_replica_that_cannot_follow_the_master_stops_and_is_rebuilt_from_the_master_without_losing_unsent_work()
    {
        factory.RequireDatabase();
        factory.Clock.CatchUpWithRealTime();
        var an = await factory.EnrolAsync();
        var binh = await factory.EnrolAsync();
        var code = "ENG-" + Guid.NewGuid().ToString("N")[..8];
        await AdminAsync(null, "engagement", "create", code, "Công ty ABC", "AUP-2026-018", "2026-01-01", "2026-12-31");
        await AdminAsync(null, "engagement", "member", code, an.Email, "preparer");
        await AdminAsync(null, "engagement", "member", code, binh.Email, "preparer");
        await AdminAsync(CashArea[..CashArea.IndexOf("### 1.", StringComparison.Ordinal)] + """
            ### 9. AUP-CSH-93: Payroll Cash

            - **Procedure:** Agree cash payments of payroll to the payroll register.
            - **Audit Content:**
                - Select a sample of payments.
            - **Evidence to Check:**
                - Payroll register.
            """, "templates", "import", "en");

        await using var deskAn = new Desk(factory, an);
        await using var deskBinh = new Desk(factory, binh);
        Assert.Null(await deskAn.StartAsync());
        Assert.Null(await deskBinh.StartAsync());

        deskAn.Engagement.SelectedTemplate = deskAn.Engagement.AreaTemplates.Single(t => t.Template.Code == "AUP-CSH-93");
        await deskAn.Engagement.CreatePaperCommand.ExecuteAsync(null);
        await deskAn.TypeAndLeaveAsync(WorkingPaperLayout.Exceptions, "0");
        await deskAn.SyncAsync();
        await deskBinh.SyncAsync();
        deskBinh.Engagement.SelectedPaper = Assert.Single(deskBinh.Engagement.Papers);
        await deskBinh.Paper.ReloadAsync(Ct);
        Assert.Null(deskAn.Engagement.ReplicaProblem);

        // ----- A hole in An's copy of the log (a damaged file, say): the next change from the master no longer follows it.
        await using (var db = deskAn.OpenDb())
        {
            var last = await db.ReplicaLog.MaxAsync(r => r.Sequence, Ct);
            Assert.Equal(1, await db.ReplicaLog.Where(r => r.Sequence == last).ExecuteDeleteAsync(Ct));
        }

        await deskBinh.TypeAndLeaveAsync(WorkingPaperLayout.Exceptions, "4");
        await deskBinh.SyncAsync();
        await deskAn.SyncAsync();
        // The laptop stops following this engagement instead of drifting, says why, and offers the rebuild.
        Assert.NotNull(deskAn.Engagement.ReplicaProblem);
        Assert.Contains("đã dừng đồng bộ", deskAn.Engagement.ReplicaProblem, StringComparison.Ordinal);
        Assert.NotNull(deskAn.Loop.Status.Error);
        Assert.Single(deskAn.Loop.Stopped);

        // Work done meanwhile is kept in the outbox and is not lost by the rebuild.
        deskAn.Engagement.SelectedPaper = Assert.Single(deskAn.Engagement.Papers);
        await deskAn.Paper.ReloadAsync(Ct);
        await deskAn.TypeAndLeaveAsync(WorkingPaperLayout.StepKey(1), "Đã đối chiếu 25 khoản chi lương bằng tiền mặt.");
        await deskAn.SyncAsync();
        Assert.NotNull(deskAn.Engagement.ReplicaProblem);

        await deskAn.Engagement.RebuildCommand.ExecuteAsync(null);
        Assert.Contains("Đã dựng lại từ máy chủ", deskAn.Engagement.RebuildMessage, StringComparison.Ordinal);
        var backups = System.IO.Directory.GetFiles(Path.Combine(deskAn.DataDirectory, "backups"), "replica-*.db");
        var backup = Assert.Single(backups);
        Assert.Contains(backup, deskAn.Engagement.RebuildMessage, StringComparison.Ordinal);
        await using (var copy = new SqliteConnection($"Data Source={backup};Mode=ReadOnly;Pooling=False"))
        {
            await copy.OpenAsync(Ct);
            using var count = copy.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM replica_log";
            Assert.True(Convert.ToInt64(await count.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture) > 0);
        }

        await deskAn.SyncAsync();
        await deskAn.SyncAsync();
        Assert.Null(deskAn.Engagement.ReplicaProblem);
        Assert.Empty(deskAn.Loop.Stopped);
        Assert.Null(deskAn.Loop.Status.Error);
        Assert.Equal(0, deskAn.Loop.Status.Pending);
        // The copy equals the master's again, and the unsent text got through.
        await using (var unit = await deskAn.OpenUnitAsync())
        {
            var paper = deskAn.Paper.DocumentId;
            var confirmed = await unit.Replica.GetDocumentAsync(deskAn.Engagement.EngagementId, paper, Ct);
            var replayed = await unit.Replica.ReplayDocumentAsync(deskAn.Engagement.EngagementId, paper, Ct);
            Assert.Equal(replayed.ToJson(), confirmed.ToJson());
            Assert.Equal("4", confirmed.Value(WorkingPaperLayout.Exceptions));
        }

        await deskBinh.SyncAsync();
        Assert.Equal("Đã đối chiếu 25 khoản chi lương bằng tiền mặt.", deskBinh.Field(WorkingPaperLayout.StepKey(1)).Text);

        // ----- The master restored from an older backup: this laptop is ahead of it. It stops, and warns before a rebuild.
        await using (var db = deskAn.OpenDb())
        {
            await db.SyncState.ExecuteUpdateAsync(s => s.SetProperty(x => x.LastPulledSequence, 9999L), Ct);
        }

        await deskAn.SyncAsync();
        Assert.Contains("cũ hơn", deskAn.Engagement.ReplicaProblem, StringComparison.Ordinal);
        Assert.Contains("Báo Admin", deskAn.Engagement.ReplicaProblem, StringComparison.Ordinal);
        await deskAn.Engagement.RebuildCommand.ExecuteAsync(null);
        await deskAn.SyncAsync();
        Assert.Null(deskAn.Engagement.ReplicaProblem);
        Assert.Equal(2, System.IO.Directory.GetFiles(Path.Combine(deskAn.DataDirectory, "backups"), "replica-*.db").Length);
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

    [Fact]
    public async Task Evidence_attached_on_a_form_reaches_the_master_before_its_link_and_is_read_back_verified()
    {
        factory.RequireDatabase();
        factory.Clock.CatchUpWithRealTime();
        var an = await factory.EnrolAsync();
        var binh = await factory.EnrolAsync();
        var reviewer = await factory.EnrolAsync();
        var outsider = await factory.EnrolAsync();
        var code = "ENG-" + Guid.NewGuid().ToString("N")[..8];
        await AdminAsync(null, "engagement", "create", code, "Công ty ABC", "AUP-2026-015", "2026-01-01", "2026-12-31");
        await AdminAsync(null, "engagement", "member", code, an.Email, "preparer");
        await AdminAsync(null, "engagement", "member", code, binh.Email, "preparer");
        await AdminAsync(null, "engagement", "member", code, reviewer.Email, "managerqa");
        // Its own procedure, so that this test and the one above can run in either order on the shared library.
        await AdminAsync(CashArea[..CashArea.IndexOf("### 1.", StringComparison.Ordinal)] + """
            ### 9. AUP-CSH-90: Bank Confirmations

            - **Procedure:** Obtain confirmations of the bank balances.
            - **Audit Content:**
                - Agree each confirmation to the ledger.
            - **Evidence to Check:**
                - Bank confirmation letters.
            """, "templates", "import", "en");

        await using var deskAn = new Desk(factory, an);
        await using var deskBinh = new Desk(factory, binh);
        await using var deskReviewer = new Desk(factory, reviewer);
        await using var deskOutsider = new Desk(factory, outsider);
        Assert.Null(await deskAn.StartAsync());
        Assert.Null(await deskBinh.StartAsync());
        Assert.Null(await deskReviewer.StartAsync());
        Assert.Null(await deskOutsider.StartAsync());
        var engagement = deskAn.Engagement.EngagementId;

        // ----- An creates a paper and attaches two files before anything is synchronised.
        deskAn.Engagement.SelectedTemplate = deskAn.Engagement.AreaTemplates.Single(t => t.Template.Code == "AUP-CSH-90");
        await deskAn.Engagement.CreatePaperCommand.ExecuteAsync(null);
        Assert.Null(deskAn.Engagement.Error);
        var countSheet = deskAn.Write("Biên bản kiểm quỹ 31-12.pdf", "cash count sheet, signed by the cashier");
        var ledger = deskAn.Write("So quy.xlsx", "cash ledger export");
        await deskAn.Paper.AttachCommand.ExecuteAsync(new[] { countSheet, ledger, deskAn.Write("empty.txt", ""), countSheet });
        // The empty file and the second copy of the count sheet are refused with a reason; the two good files are in.
        Assert.Contains("rỗng", deskAn.Paper.Error, StringComparison.Ordinal);
        Assert.Contains("đã được đính kèm", deskAn.Paper.Error, StringComparison.Ordinal);
        Assert.Equal(["Biên bản kiểm quỹ 31-12.pdf", "So quy.xlsx"], deskAn.Paper.Evidence.Select(e => e.FileName));
        Assert.All(deskAn.Paper.Evidence, e => Assert.Contains("chờ tải lên máy chủ", e.Detail, StringComparison.Ordinal));
        // The original can go: the application keeps its own copy.
        File.Delete(countSheet);

        await deskAn.SyncAsync();
        Assert.Equal((0, 0, 0), (deskAn.Loop.Status.Pending, deskAn.Loop.Status.PendingFiles, deskAn.Loop.Status.NeedsAttention));
        Assert.All(deskAn.Paper.Evidence, e => Assert.Contains("đã ở máy chủ · có bản trên máy này", e.Detail, StringComparison.Ordinal));
        var sheet = deskAn.Paper.Evidence.Single(e => e.FileName.StartsWith("Biên bản", StringComparison.Ordinal));

        // ----- On the master: the files were received first, then the links were admitted, all on the audit log.
        var audit = (await factory.AuditAsync()).Where(a => a.EngagementId == engagement).ToList();
        var received = audit.FindIndex(a => a.Action == "evidence.received" && a.EntityId == sheet.Hash);
        var linked = audit.FindIndex(a => a.Action == "sync.link.accepted" && a.PayloadJson.Contains(sheet.Hash, StringComparison.Ordinal));
        Assert.True(received >= 0 && linked > received, $"received at {received}, linked at {linked}");
        Assert.Contains(audit, a => a.Action == "evidence.received" && a.ActorUserId == deskAn.UserId
            && a.PayloadJson.Contains("So quy.xlsx", StringComparison.Ordinal));
        Assert.Equal(2, audit.Count(a => a.Action == "evidence.received"));

        // ----- Binh sees the files by name without having them, and gets one when opening it.
        await deskBinh.SyncAsync();
        deskBinh.Engagement.SelectedPaper = Assert.Single(deskBinh.Engagement.Papers);
        await deskBinh.Paper.ReloadAsync(Ct);
        var seen = deskBinh.Paper.Evidence.Single(e => e.Hash == sheet.Hash);
        Assert.Equal("Biên bản kiểm quỹ 31-12.pdf", seen.FileName);
        Assert.Contains("tải về khi mở", seen.Detail, StringComparison.Ordinal);
        Assert.False(deskBinh.Evidence.IsHere(sheet.Hash));
        await deskBinh.Paper.OpenEvidenceCommand.ExecuteAsync(seen);
        Assert.Null(deskBinh.Paper.Error);
        var opened = Assert.Single(deskBinh.Launcher.Paths);
        Assert.Equal("Biên bản kiểm quỹ 31-12.pdf", Path.GetFileName(opened));
        Assert.Equal("cash count sheet, signed by the cashier", await File.ReadAllTextAsync(opened, Ct));
        Assert.True(File.GetAttributes(opened).HasFlag(FileAttributes.ReadOnly));
        Assert.True(deskBinh.Evidence.IsHere(sheet.Hash));
        Assert.Contains((await factory.AuditAsync()), a => a.Action == "evidence.read" && a.EntityId == sheet.Hash && a.ActorUserId == deskBinh.UserId);

        // ----- The reviewer reads evidence and does not add any; an outsider gets nothing, even with the hash.
        await deskReviewer.SyncAsync();
        deskReviewer.Engagement.SelectedPaper = Assert.Single(deskReviewer.Engagement.Papers);
        await deskReviewer.Paper.ReloadAsync(Ct);
        Assert.Equal(2, deskReviewer.Paper.Evidence.Count);
        await deskReviewer.Paper.AttachCommand.ExecuteAsync(new[] { deskReviewer.Write("note.txt", "from the reviewer") });
        Assert.Equal(2, deskReviewer.Paper.Evidence.Count);
        await using (var note = new MemoryStream("from the reviewer"u8.ToArray()))
        {
            var hash = (await AuditPlatform.Evidence.EvidenceHash.ComputeAsync(note, null, Ct)).Hash;
            note.Position = 0;
            Assert.Equal((EvidenceSend.Refused, FieldPolicy.NotAnEditor), await SendAsync(deskReviewer, engagement, hash, note));
        }

        Assert.Null(await deskOutsider.Files.ListAsync(engagement, Ct));
        await using (var stolen = new MemoryStream())
        {
            Assert.False(await deskOutsider.Files.FetchAsync(engagement, sheet.Hash, stolen, Ct));
            Assert.Equal(0, stolen.Length);
        }

        // ----- What the master refuses: content that is not what its address says, and a file over the limit.
        await using (var other = new MemoryStream("something else"u8.ToArray()))
        {
            var (outcome, reason) = await SendAsync(deskAn, engagement, new string('0', 64), other);
            Assert.Equal(EvidenceSend.Refused, outcome);
            Assert.Contains("hashes to", reason, StringComparison.Ordinal);
        }

        await using (var huge = new MemoryStream(new byte[AuthFactory.MaxEvidenceBytes + 1]))
        {
            var (outcome, reason) = await SendAsync(deskAn, engagement, new string('1', 64), huge);
            Assert.Equal(EvidenceSend.Refused, outcome);
            Assert.Contains("at most 1 MB", reason, StringComparison.Ordinal);
        }

        // ----- A link that did not come with its file (a modified client) is refused and returned to its author.
        await using (var unit = await deskBinh.OpenUnitAsync())
        {
            await unit.Workspace(engagement, deskBinh.UserId).LinkEvidenceAsync(deskBinh.Paper.DocumentId, new string('2', 64), Ct);
        }

        await deskBinh.SyncAsync();
        Assert.Contains(FieldPolicy.EvidenceNotReceived, Assert.Single(deskBinh.Paper.Attention).Text, StringComparison.Ordinal);
        Assert.Equal(2, deskBinh.Paper.Evidence.Count);
        await deskBinh.Paper.DismissCommand.ExecuteAsync(deskBinh.Paper.Attention[0]);

        // ----- Removing a link takes the file off the paper, not out of the engagement's record.
        await deskAn.Paper.RemoveEvidenceCommand.ExecuteAsync(deskAn.Paper.Evidence.Single(e => e.FileName == "So quy.xlsx"));
        Assert.Single(deskAn.Paper.Evidence);
        await deskAn.SyncAsync();
        await deskBinh.SyncAsync();
        Assert.Equal(sheet.Hash, Assert.Single(deskBinh.Paper.Evidence).Hash);
        Assert.Equal(2, (await deskAn.Files.ListAsync(engagement, Ct))!.Count);

        // ----- The console re-hashes the whole store: clean now; a file altered or lost on the volume is named.
        var (exit, report) = await RunAdminAsync("evidence", "verify");
        Assert.True(exit == 0, report);
        Assert.Contains("Đã kiểm tra 2 file", report, StringComparison.Ordinal);
        var altered = Path.Combine(factory.EvidenceRoot, sheet.Hash[..2], sheet.Hash[2..4], sheet.Hash);
        File.SetAttributes(altered, FileAttributes.Normal);
        await File.WriteAllTextAsync(altered, "altered on the volume", Ct);
        var lost = (await deskAn.Files.ListAsync(engagement, Ct))!.Single(f => f.Hash != sheet.Hash).Hash;
        var lostPath = Path.Combine(factory.EvidenceRoot, lost[..2], lost[2..4], lost);
        File.SetAttributes(lostPath, FileAttributes.Normal);
        File.Delete(lostPath);
        (exit, report) = await RunAdminAsync("evidence", "verify");
        Assert.Equal(1, exit);
        Assert.Contains("BỊ SỬA    " + sheet.Hash, report, StringComparison.Ordinal);
        Assert.Contains("THIẾU     " + lost, report, StringComparison.Ordinal);
        Assert.Equal(2, (await factory.AuditAsync()).Count(a => a.Action == "evidence.verified" && a.PayloadJson.Contains("operatorName", StringComparison.Ordinal)));

        // Reading a damaged file is refused and recorded, never served.
        await using (var stolen = new MemoryStream())
        {
            Assert.False(await deskBinh.Files.FetchAsync(engagement, sheet.Hash, stolen, Ct));
        }

        Assert.Contains(await factory.AuditAsync(), a => a.Action == "evidence.damaged" && a.EntityId == sheet.Hash);
    }

    private async Task<(int Exit, string Output)> RunAdminAsync(params string[] args)
    {
        using var output = new StringWriter();
        var exit = await AdminCli.RunAsync(factory.Services, args, output, new StringReader(""));
        return (exit, output.ToString());
    }

    private static async Task<(EvidenceSend Outcome, string? Reason)> SendAsync(Desk desk, Guid engagement, string hash, Stream content)
    {
        var result = await desk.Files.SendAsync(engagement, hash, "file.bin", content, Ct);
        return (result.Outcome, result.Reason);
    }
}
