using System.Net;
using System.Net.Http.Json;
using AuditPlatform.Client.Core;
using AuditPlatform.Client.Core.Auth;
using AuditPlatform.Client.Core.LocalStore;
using AuditPlatform.Client.Core.Sync;
using AuditPlatform.Contracts.Auth;
using AuditPlatform.Contracts.Sync;
using AuditPlatform.Domain.Engagements;
using AuditPlatform.Domain.WorkingPapers;
using AuditPlatform.Master.Data;
using AuditPlatform.Master.Data.Entities;
using AuditPlatform.Master.Notifications;
using AuditPlatform.Master.Sync;
using AuditPlatform.Master.Tests.Auth;
using AuditPlatform.Sync;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuditPlatform.Master.Tests.Sync;

/// <summary>
/// The whole path, nothing replaced but the clock and the mail sender: the real master host on PostgreSQL, real
/// accounts with TOTP, the client's own session, SignalR transport, SQLite replica and sync agent.
/// </summary>
public sealed class SyncEndToEndTests(AuthFactory factory) : IClassFixture<AuthFactory>
{
    private const string Exceptions = "exceptions_noted";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class MemoryStore : IRefreshTokenStore
    {
        private string? _token;
        public string? Load() => _token;
        public void Save(string refreshToken) => _token = refreshToken;
        public void Clear() => _token = null;
    }

    /// <summary>One person's laptop, signed in.</summary>
    private sealed class Seat : IAsyncDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly HttpClient _http;
        private readonly AuthFactory _factory;
        private readonly Guid _engagement;
        private TaskCompletionSource _changes = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Seat(AuthFactory factory, TestUser user, Guid engagement)
        {
            _factory = factory;
            _engagement = engagement;
            User = user;
            var options = new ClientOptions { MasterUrl = new Uri("http://localhost/"), ClientId = Guid.NewGuid() };
            _http = factory.CreateClient();
            Session = new AuthSession(_http, options, new MemoryStore(), factory.Clock);
            Transport = new SignalRSyncTransport(options, () => Session.GetAccessTokenAsync(), o =>
            {
                // The in-process test server has no sockets; long polling exercises the same hub and the same authentication.
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            });
            Transport.ChangesAvailable += (_, _) => _changes.TrySetResult();

            _connection.Open();
            Db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(_connection).Options);
            Db.Database.EnsureCreated();
            Db.Engagements.Add(new LocalEngagementRow { Id = engagement, Code = "ENG-E2E", ClientName = "Client", Status = EngagementStatus.Fieldwork });
            Db.SaveChanges();
            Replica = new LocalReplica(Db);
            Replica.SealBroken += (_, e) => SealBreaks.Add(e);
            Agent = new SyncAgent(Db, Transport, Replica, options, factory.Clock);
            Work = new EngagementWorkspace(Db, Agent, Replica, options, factory.Clock, engagement, user.Id);
        }

        public TestUser User { get; }
        public AuthSession Session { get; }
        public SignalRSyncTransport Transport { get; }
        public LocalDbContext Db { get; }
        public LocalReplica Replica { get; }
        public SyncAgent Agent { get; }
        public EngagementWorkspace Work { get; }
        public List<SealBrokenEventArgs> SealBreaks { get; } = [];

        public async Task SignInAndConnectAsync(bool subscribe = true)
        {
            var signedIn = await Session.SignInAsync(User.Email, User.Password, _factory.NextCode(User.Secret), null, Ct);
            Assert.True(signedIn.Succeeded, signedIn.Error?.Message);
            await Transport.ConnectAsync(Ct);
            if (subscribe)
            {
                await Transport.SubscribeAsync(_engagement, Ct);
            }
        }

        public Task<(int Delivered, int Received)> SyncAsync() => Agent.SyncAsync(_engagement, Ct);

        /// <summary>Waits for the master's "changes available" signal, then pulls.</summary>
        public async Task PullWhenToldAsync()
        {
            await _changes.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            _changes = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await Agent.PullAsync(_engagement, Ct);
        }

        public async ValueTask DisposeAsync()
        {
            await Transport.DisposeAsync();
            Session.Dispose();
            _http.Dispose();
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private async Task<Guid> CreateEngagementAsync(params (TestUser User, EngagementRole Role)[] members)
    {
        var id = Guid.NewGuid();
        await factory.ScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MasterDbContext>();
            db.Engagements.Add(new EngagementRow
            {
                Id = id, Code = "ENG-" + id.ToString("N")[..8], ClientName = "Client", ContractReference = "AUP-2026",
                PeriodStart = new DateOnly(2026, 1, 1), PeriodEnd = new DateOnly(2026, 12, 31), Status = EngagementStatus.Fieldwork,
                Members = [.. members.Select(m => new EngagementMemberRow { EngagementId = id, UserId = m.User.Id, Role = m.Role })],
            });
            return await db.SaveChangesAsync(Ct);
        });
        return id;
    }

    private Task<int> DispatchNotificationsAsync() =>
        factory.ScopeAsync(sp => sp.GetRequiredService<NotificationSender>().DispatchPendingAsync(Ct));

    [Fact]
    public async Task Edit_sync_sign_off_with_totp_break_the_seal_notify_the_reviewer_then_lock()
    {
        factory.RequireDatabase();
        factory.Clock.CatchUpWithRealTime();
        var preparerUser = await factory.EnrolAsync();
        var managerUser = await factory.EnrolAsync();
        var outsiderUser = await factory.EnrolAsync();
        var engagement = await CreateEngagementAsync((preparerUser, EngagementRole.Preparer), (managerUser, EngagementRole.ManagerQa));
        var paper = Guid.NewGuid();

        await using var preparer = new Seat(factory, preparerUser, engagement);
        await using var manager = new Seat(factory, managerUser, engagement);
        await using var outsider = new Seat(factory, outsiderUser, engagement);
        await preparer.SignInAndConnectAsync();
        await manager.SignInAndConnectAsync();

        // ----- A signed-in user who is not a member gets nothing, and cannot tell whether the engagement exists.
        await outsider.SignInAndConnectAsync(subscribe: false);
        var refusedSubscribe = await Assert.ThrowsAsync<HubException>(() => outsider.Transport.SubscribeAsync(engagement, Ct));
        var refusedPull = await Assert.ThrowsAsync<HubException>(() => outsider.Transport.PullAsync(engagement, SyncCursor.Start, 10, Ct));
        var refusedGhost = await Assert.ThrowsAsync<HubException>(() => outsider.Transport.PullAsync(Guid.NewGuid(), SyncCursor.Start, 10, Ct));
        Assert.Contains("Not a member", refusedSubscribe.Message, StringComparison.Ordinal);
        Assert.Equal(refusedPull.Message, refusedGhost.Message);

        // ----- The preparer works offline, then synchronises; the manager is told and pulls.
        await preparer.Work.CreatePaperAsync(paper, "AUP-CSH-03", 1, Ct);
        await preparer.Work.SetFieldAsync(paper, Exceptions, "0", Ct);
        await preparer.Work.SetFieldAsync(paper, "conclusion", "Không có ngoại lệ trong 25 mẫu.", Ct);
        await preparer.Work.SubmitForReviewAsync(paper, Ct);
        Assert.Equal((4, 4), await preparer.SyncAsync());
        await manager.PullWhenToldAsync();
        var seen = await manager.Work.ConfirmedAsync(paper, Ct);
        Assert.Equal((WorkingPaperStatus.ReadyForReview, "Không có ngoại lệ trong 25 mẫu."), (seen.Status, seen.Value("conclusion")));

        // ----- The author recorded by the master is the signed-in user, whatever the laptop claims.
        var forged = new ChangeEnvelope(Guid.NewGuid(), engagement, paper, managerUser.Id, Guid.NewGuid(), factory.Clock.GetUtcNow(),
            FieldPayload.Kind, FieldPayload.Encode(new SetField("sample_size", "25", null)));
        Assert.Equal(ChangeOutcome.Accepted, (await preparer.Transport.PushAsync(engagement, [forged], Ct)).Results.Single().Outcome);
        await preparer.Agent.PullAsync(engagement, Ct);
        Assert.Equal(preparerUser.Id, (await preparer.Work.ConfirmedAsync(paper, Ct)).Fields["sample_size"].Author);
        await manager.PullWhenToldAsync();

        // ----- Sign-off needs the reviewer role and a grant for exactly the content on the master.
        var staleHash = (await manager.Work.ConfirmedAsync(paper, Ct)).ComputeContentHash();
        var notReviewer = await preparer.Session.SignOffAsync(engagement, paper, nameof(EngagementRole.ManagerQa), staleHash, factory.NextCode(preparerUser.Secret), Ct);
        Assert.Equal("not_reviewer", notReviewer.Error?.Error);
        var wrongLevel = await manager.Session.SignOffAsync(engagement, paper, nameof(EngagementRole.JapanDesk), staleHash, factory.NextCode(managerUser.Secret), Ct);
        Assert.Equal("not_reviewer", wrongLevel.Error?.Error);
        var noCode = await manager.Session.SignOffAsync(engagement, paper, nameof(EngagementRole.ManagerQa), staleHash, "000000", Ct);
        Assert.Equal("code_invalid", noCode.Error?.Error);

        // The preparer changes the paper while the manager is entering the code: what the manager saw is no longer
        // what the master holds, so the grant does not fit and nothing is signed.
        await preparer.Work.SetFieldAsync(paper, Exceptions, "1", Ct);
        await preparer.SyncAsync();
        var outdated = await manager.Session.SignOffAsync(engagement, paper, nameof(EngagementRole.ManagerQa), staleHash, factory.NextCode(managerUser.Secret), Ct);
        Assert.Equal("step_up_required", outdated.Error?.Error);

        await manager.PullWhenToldAsync();
        var reviewed = await manager.Work.ConfirmedAsync(paper, Ct);
        var signed = await manager.Session.SignOffAsync(engagement, paper, nameof(EngagementRole.ManagerQa), reviewed.ComputeContentHash(), factory.NextCode(managerUser.Secret), Ct);
        Assert.True(signed.Succeeded, signed.Error?.Message);
        Assert.Equal(reviewed.ComputeContentHash(), signed.Value!.ContentHash);

        // Both laptops replay the sign-off and recompute the same hash; the signer is the manager.
        await preparer.PullWhenToldAsync();
        await manager.PullWhenToldAsync();
        var sealedPaper = await preparer.Work.ConfirmedAsync(paper, Ct);
        var signOff = Assert.Single(sealedPaper.SignOffs);
        Assert.Equal((WorkingPaperStatus.ManagerQaSignedOff, managerUser.Id, signed.Value.ContentHash, signed.Value.ChangeId),
            (sealedPaper.Status, signOff.SignedBy, signOff.ContentHash, signOff.ChangeId));

        // Signing twice is refused by the paper's own rules, without spending another code.
        var again = await manager.Session.SignOffAsync(engagement, paper, nameof(EngagementRole.ManagerQa), signed.Value.ContentHash, factory.NextCode(managerUser.Secret), Ct);
        Assert.Equal("sign_off_refused", again.Error?.Error);

        // ----- An edit after sign-off is accepted, breaks the seal, and the reviewer is told.
        var breaking = await preparer.Work.SetFieldAsync(paper, Exceptions, "2", Ct);
        await preparer.SyncAsync();
        await manager.PullWhenToldAsync();
        var reopened = await manager.Work.ConfirmedAsync(paper, Ct);
        Assert.Equal((WorkingPaperStatus.ReadyForReview, breaking), (reopened.Status, reopened.SignOffs.Single().BrokenByChangeId));
        Assert.Equal(breaking, Assert.Single(manager.SealBreaks).ChangeId);
        Assert.Equal(breaking, Assert.Single(preparer.SealBreaks).ChangeId);

        // The notification is owed from the moment the edit committed. A mail outage delays it; it is not lost.
        var mailsBefore = factory.Mail.Sent.Count;
        factory.Mail.Fail = true;
        Assert.Equal(0, await DispatchNotificationsAsync());
        factory.Mail.Fail = false;
        Assert.Equal(0, await DispatchNotificationsAsync());           // not hammered: the retry waits
        factory.Clock.Advance(NotificationSender.RetryAfter + TimeSpan.FromSeconds(1));
        Assert.Equal(1, await DispatchNotificationsAsync());
        Assert.Equal(0, await DispatchNotificationsAsync());           // sent once
        var mail = Assert.Single(factory.Mail.Sent.Skip(mailsBefore));
        Assert.Equal(managerUser.Email, mail.ToAddress);
        Assert.Contains("AUP-CSH-03", mail.Subject, StringComparison.Ordinal);
        Assert.Contains("sign-off", mail.TextBody, StringComparison.Ordinal);
        // No working-paper content leaves the platform by e-mail.
        Assert.DoesNotContain("ngoại lệ", mail.TextBody, StringComparison.Ordinal);

        var trail = (await factory.AuditAsync()).Where(a => a.EngagementId == engagement).ToList();
        var seal = Assert.Single(trail, a => a.Action == "sync.seal.broken");
        Assert.Equal(preparerUser.Id, seal.ActorUserId);
        Assert.Contains(signed.Value.ChangeId.ToString(), seal.PayloadJson, StringComparison.Ordinal);
        Assert.Contains(managerUser.Id.ToString(), seal.PayloadJson, StringComparison.Ordinal);
        Assert.Single(trail, a => a.Action == "sync.sign-off.accepted" && a.ActorUserId == managerUser.Id);
        Assert.Single(trail, a => a.Action == "sync.sign-off.step_up_refused");
        Assert.Single(trail, a => a.Action == "notify.seal-broken.sent");

        // ----- After the engagement lock nothing is accepted any more; the author keeps the refused edit.
        await factory.ScopeAsync(sp => sp.GetRequiredService<MasterDbContext>().Engagements.Where(e => e.Id == engagement)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, EngagementStatus.Locked), Ct));
        var late = await preparer.Work.SetFieldAsync(paper, Exceptions, "9", Ct);
        await preparer.SyncAsync();
        var kept = Assert.Single(await preparer.Work.NeedsAttentionAsync(Ct));
        Assert.Equal((late, OutboxState.Rejected, FieldSyncEngine.LockedReason), (kept.ChangeId, kept.State, kept.LastError));
        var lockedSignOff = await manager.Session.SignOffAsync(engagement, paper, nameof(EngagementRole.ManagerQa), reopened.ComputeContentHash(), factory.NextCode(managerUser.Secret), Ct);
        Assert.Equal("engagement_locked", lockedSignOff.Error?.Error);
        Assert.Equal("2", (await manager.Work.ConfirmedAsync(paper, Ct)).Value(Exceptions));
    }

    [Fact]
    public async Task The_manager_moves_the_engagement_to_reporting_and_locks_it_only_when_every_paper_is_signed_off_and_clean()
    {
        factory.RequireDatabase();
        factory.Clock.CatchUpWithRealTime();
        var preparerUser = await factory.EnrolAsync();
        var qaUser = await factory.EnrolAsync();
        var japanUser = await factory.EnrolAsync();
        var managerUser = await factory.EnrolAsync();
        var outsiderUser = await factory.EnrolAsync();
        var engagement = await CreateEngagementAsync((preparerUser, EngagementRole.Preparer), (qaUser, EngagementRole.ManagerQa),
            (japanUser, EngagementRole.JapanDesk), (managerUser, EngagementRole.EngagementManager));

        await using var preparer = new Seat(factory, preparerUser, engagement);
        await using var qa = new Seat(factory, qaUser, engagement);
        await using var japan = new Seat(factory, japanUser, engagement);
        await using var manager = new Seat(factory, managerUser, engagement);
        await using var outsider = new Seat(factory, outsiderUser, engagement);
        foreach (var seat in new[] { preparer, qa, japan, manager })
        {
            await seat.SignInAndConnectAsync();
        }

        await outsider.SignInAndConnectAsync(subscribe: false);
        var lockedSeen = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        preparer.Transport.EngagementLocked += (_, id) => lockedSeen.TrySetResult(id);

        async Task SignAsync(Seat reviewer, Guid paper, EngagementRole level)
        {
            await reviewer.SyncAsync();
            var hash = (await reviewer.Work.ConfirmedAsync(paper, Ct)).ComputeContentHash();
            var signed = await reviewer.Session.SignOffAsync(engagement, paper, level.ToString(), hash, factory.NextCode(reviewer.User.Secret), Ct);
            Assert.True(signed.Succeeded, signed.Error?.Message);
        }

        // ----- Two papers: the first goes through both review levels, the second is left in draft.
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await preparer.Work.CreatePaperAsync(first, "AUP-CSH-03", 1, Ct);
        await preparer.Work.SetFieldAsync(first, Exceptions, "0", Ct);
        await preparer.Work.SubmitForReviewAsync(first, Ct);
        await preparer.Work.CreatePaperAsync(second, "AUP-CSH-04", 1, Ct);
        await preparer.SyncAsync();
        await SignAsync(qa, first, EngagementRole.ManagerQa);
        await SignAsync(japan, first, EngagementRole.JapanDesk);

        // ----- Only an engagement manager of the engagement moves its phases; everybody else gets the same answer.
        Assert.Equal("not_manager", (await preparer.Session.AdvanceEngagementAsync(engagement, nameof(EngagementStatus.Review), Ct)).Error?.Error);
        Assert.Equal("not_manager", (await outsider.Session.AdvanceEngagementAsync(engagement, nameof(EngagementStatus.Review), Ct)).Error?.Error);
        Assert.Equal("not_manager", (await manager.Session.AdvanceEngagementAsync(Guid.NewGuid(), nameof(EngagementStatus.Review), Ct)).Error?.Error);
        Assert.Equal("status_invalid", (await manager.Session.AdvanceEngagementAsync(engagement, "Finished", Ct)).Error?.Error);
        Assert.Equal("transition_refused", (await manager.Session.AdvanceEngagementAsync(engagement, nameof(EngagementStatus.Reporting), Ct)).Error?.Error);
        Assert.Equal("transition_refused", (await manager.Session.AdvanceEngagementAsync(engagement, nameof(EngagementStatus.Locked), Ct)).Error?.Error);
        Assert.Equal(nameof(EngagementStatus.Review), (await manager.Session.AdvanceEngagementAsync(engagement, nameof(EngagementStatus.Review), Ct)).Value!.Status);
        Assert.Equal(nameof(EngagementStatus.Reporting), (await manager.Session.AdvanceEngagementAsync(engagement, nameof(EngagementStatus.Reporting), Ct)).Value!.Status);

        // ----- What stands in the way is listed; a non-member cannot even look.
        Assert.Null(await outsider.Session.GetLockReadinessAsync(engagement, Ct));
        var blocked = (await manager.Session.GetLockReadinessAsync(engagement, Ct))!;
        Assert.False(blocked.Ready);
        Assert.Equal((nameof(EngagementStatus.Reporting), 2), (blocked.Status, blocked.PaperCount));
        var draftBlocker = Assert.Single(blocked.Blockers);
        Assert.Equal((second, "AUP-CSH-04"), (draftBlocker.WorkingPaperId, draftBlocker.TemplateCode));
        Assert.Equal("lock_blocked", (await manager.Session.LockEngagementAsync(engagement, blocked.Digest, factory.NextCode(managerUser.Secret), Ct)).Error?.Error);

        // A signed-off paper with an open review note is not clean either.
        var note = Guid.NewGuid();
        await qa.Work.RaiseNoteAsync(first, note, Exceptions, "Attach the signed count sheet.", Ct);
        await qa.SyncAsync();
        Assert.Equal(new HashSet<Guid> { first, second },
            (await manager.Session.GetLockReadinessAsync(engagement, Ct))!.Blockers.Select(b => b.WorkingPaperId!.Value).ToHashSet());

        // The second paper goes through review; only the note is left.
        await preparer.Work.SubmitForReviewAsync(second, Ct);
        await preparer.SyncAsync();
        await SignAsync(qa, second, EngagementRole.ManagerQa);
        await SignAsync(japan, second, EngagementRole.JapanDesk);
        var open = (await manager.Session.GetLockReadinessAsync(engagement, Ct))!;
        Assert.Equal(first, Assert.Single(open.Blockers).WorkingPaperId);

        await preparer.SyncAsync();
        await preparer.Work.RespondAsync(first, note, "Attached.", Ct);
        await preparer.SyncAsync();
        await qa.SyncAsync();
        await qa.Work.ClearNoteAsync(first, note, Ct);
        await qa.SyncAsync();
        var ready = (await manager.Session.GetLockReadinessAsync(engagement, Ct))!;
        Assert.True(ready.Ready, string.Join(" ", ready.Blockers.Select(b => b.Reason)));
        Assert.NotEqual(open.Digest, ready.Digest);

        // ----- The lock needs the manager, a fresh code, and a grant for exactly this state.
        Assert.Equal("code_invalid", (await manager.Session.LockEngagementAsync(engagement, ready.Digest, "000000", Ct)).Error?.Error);
        Assert.Equal("step_up_required", (await manager.Session.LockEngagementAsync(engagement, open.Digest, factory.NextCode(managerUser.Secret), Ct)).Error?.Error);
        Assert.Equal("not_manager", (await preparer.Session.LockEngagementAsync(engagement, ready.Digest, factory.NextCode(preparerUser.Secret), Ct)).Error?.Error);
        Assert.Equal("not_manager", (await qa.Session.LockEngagementAsync(engagement, ready.Digest, factory.NextCode(qaUser.Secret), Ct)).Error?.Error);
        Assert.Equal("not_manager", (await outsider.Session.LockEngagementAsync(engagement, ready.Digest, factory.NextCode(outsiderUser.Secret), Ct)).Error?.Error);

        var locked = await manager.Session.LockEngagementAsync(engagement, ready.Digest, factory.NextCode(managerUser.Secret), Ct);
        Assert.True(locked.Succeeded, locked.Error?.Message);
        Assert.Equal(ready.Digest, locked.Value!.Digest);
        Assert.Equal(engagement, await lockedSeen.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct));

        var row = await factory.ScopeAsync(sp => sp.GetRequiredService<MasterDbContext>().Engagements.AsNoTracking().SingleAsync(e => e.Id == engagement, Ct));
        Assert.Equal((EngagementStatus.Locked, managerUser.Id), (row.Status, row.LockedBy));
        Assert.NotNull(row.LockedAt);

        // ----- After the lock nothing moves, and it cannot be taken back.
        Assert.Equal("engagement_locked", (await manager.Session.AdvanceEngagementAsync(engagement, nameof(EngagementStatus.Review), Ct)).Error?.Error);
        Assert.Equal("engagement_locked", (await manager.Session.LockEngagementAsync(engagement, ready.Digest, factory.NextCode(managerUser.Secret), Ct)).Error?.Error);
        var late = await preparer.Work.SetFieldAsync(first, Exceptions, "9", Ct);
        await preparer.SyncAsync();
        var kept = Assert.Single(await preparer.Work.NeedsAttentionAsync(Ct));
        Assert.Equal((late, OutboxState.Rejected), (kept.ChangeId, kept.State));

        var trail = (await factory.AuditAsync()).Where(a => a.EngagementId == engagement).Select(a => a.Action).ToList();
        Assert.Equal(2, trail.Count(a => a == "engagement.status.changed"));
        Assert.Equal(2, trail.Count(a => a == "engagement.status.refused"));
        Assert.Single(trail, a => a == "engagement.lock.refused");
        Assert.Single(trail, a => a == "engagement.lock.step_up_refused");
        Assert.Single(trail, a => a == "engagement.locked");
    }

    [Fact]
    public async Task Sign_off_api_refuses_callers_without_a_session_and_non_members()
    {
        factory.RequireDatabase();
        using var client = factory.CreateClient();
        var member = await factory.EnrolAsync();
        var stranger = await factory.EnrolAsync();
        var engagement = await CreateEngagementAsync((member, EngagementRole.ManagerQa));
        var request = new SignOffRequest(engagement, Guid.NewGuid(), nameof(EngagementRole.ManagerQa), "no-grant");

        using (var anonymous = await client.PostAsJsonAsync(SyncRoutes.SignOff, request, Ct))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        async Task<(HttpStatusCode Status, string? Code)> PostAsync(TestUser user, SignOffRequest body)
        {
            var tokens = await factory.SignInAsync(client, user);
            using var message = AuthFactory.Authorized(HttpMethod.Post, SyncRoutes.SignOff, tokens.AccessToken, body);
            using var response = await client.SendAsync(message, Ct);
            return (response.StatusCode, (await response.Content.ReadFromJsonAsync<SyncError>(Ct))?.Error);
        }

        Assert.Equal((HttpStatusCode.Forbidden, "not_member"), await PostAsync(stranger, request));
        Assert.Equal((HttpStatusCode.Forbidden, "not_member"), await PostAsync(stranger, request with { EngagementId = Guid.NewGuid() }));
        Assert.Equal((HttpStatusCode.BadRequest, "level_invalid"), await PostAsync(member, request with { Level = "Preparer" }));
        // A member with the role, but the working paper does not exist: refused by the rules, no grant needed to learn that.
        Assert.Equal((HttpStatusCode.Conflict, "sign_off_refused"), await PostAsync(member, request));
    }
}
