using System.Text.Json;
using AuditPlatform.AuditLog;
using AuditPlatform.Contracts.Sync;
using AuditPlatform.Domain.Engagements;
using AuditPlatform.Master.Auth;
using AuditPlatform.Master.Data;
using AuditPlatform.Master.Data.Entities;
using AuditPlatform.Master.Notifications;
using AuditPlatform.Sync;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Master.Sync;

public enum SignOffStatus
{
    SignedOff = 0,

    /// <summary>No valid grant for this user, working paper and content: wrong, spent or expired, or the content changed.</summary>
    StepUpRefused = 1,

    /// <summary>The rules of the working paper refuse the sign-off (open notes, open conflicts, wrong status, own paper).</summary>
    Refused = 2,

    Locked = 3,
}

public sealed record SignOffAttempt(SignOffStatus Status, string? Reason, Guid ChangeId, string? ContentHash, SyncCursor Cursor);

/// <summary>The replica and the master no longer share a history; the client is told with <see cref="SyncErrors.ReplicaMustRebuild"/>.</summary>
public abstract class ReplicaMustRebuildException(string message) : Exception(message);

/// <summary>The client's cursor is beyond the end of the master's log, e.g. after the master was restored from a backup.</summary>
public sealed class ReplicaAheadOfMasterException(long clientSequence, long masterSequence) : ReplicaMustRebuildException(
    $"{SyncErrors.ReplicaMustRebuild}: This replica is at change {clientSequence} but the master's log ends at {masterSequence}. The replica must be rebuilt from the master.");

/// <summary>
/// The client's last change is not the one the master has at that position: the master was restored from an older
/// backup and has grown past it with other changes, so the numbers line up but the histories do not.
/// </summary>
public sealed class ReplicaDivergedFromMasterException(long sequence) : ReplicaMustRebuildException(
    $"{SyncErrors.ReplicaMustRebuild}: The master's log has a different change at position {sequence} than this replica. The replica must be rebuilt from the master.");

/// <summary>
/// The durable engine of ADR-0004: field-level versioning with master arbitration, on PostgreSQL.
/// One transaction per push: it locks the engagement row (which serialises all writers of the engagement and makes
/// the engagement lock airtight), decides every change with <see cref="FieldDocument"/>, appends the accepted and
/// flagged ones to <c>changes</c> with gap-free sequences, stores the materialised documents, keeps a receipt per
/// change id for idempotent redelivery, and writes each decision to the hash-chained audit log with before and after.
/// </summary>
public sealed class FieldSyncEngine(MasterDbContext db, IAuditLog auditLog, TimeProvider clock, NotificationSignal notifications)
    : IDocumentSyncEngine
{
    public const string LockedReason = "Engagement is locked.";
    public const string SealBrokenKind = "seal-broken";

    public static readonly Guid MasterClientId = Guid.Empty;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public IReadOnlySet<string> SupportedPayloadKinds { get; } = new HashSet<string> { FieldPayload.Kind };

    public async ValueTask<PushResult> ApplyAsync(Guid engagementId, IReadOnlyList<ChangeEnvelope> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var unit = await OpenAsync(engagementId, [.. changes.Select(c => c.ChangeId)], cancellationToken);
        if (unit.Refusal is { } refusal)
        {
            // Nothing is recorded: the author keeps the change, and it can be judged again if the engagement reopens.
            return new PushResult([.. changes.Select(c => new ChangeResult(c.ChangeId, ChangeOutcome.Rejected, refusal))], new SyncCursor(unit.Sequence));
        }

        var results = new List<ChangeResult>(changes.Count);
        foreach (var change in changes)
        {
            results.Add(await AdmitAsync(unit, change, fromMaster: false, cancellationToken));
        }

        await CommitAsync(unit, tx, cancellationToken);
        return new PushResult(results, new SyncCursor(unit.Sequence));
    }

    public async ValueTask<PullResult> ReadSinceAsync(Guid engagementId, SyncCursor since, int maxChanges, CancellationToken cancellationToken)
    {
        var max = Math.Max(1, maxChanges);
        var rows = await db.Changes.AsNoTracking()
            .Where(c => c.EngagementId == engagementId && c.Sequence > since.Sequence)
            .OrderBy(c => c.Sequence)
            .Take(max + 1)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0 && since.Sequence > 0)
        {
            var head = await HeadAsync(engagementId, cancellationToken);
            if (since.Sequence > head)
            {
                throw new ReplicaAheadOfMasterException(since.Sequence, head);
            }
        }

        // The numbers can line up while the history behind them differs (a restore, then new changes): compare ids.
        if (since is { Sequence: > 0, AnchorChangeId: { } anchor })
        {
            var atCursor = await db.Changes.AsNoTracking()
                .Where(c => c.EngagementId == engagementId && c.Sequence == since.Sequence)
                .Select(c => (Guid?)c.ChangeId)
                .FirstOrDefaultAsync(cancellationToken);
            if (atCursor is { } held && held != anchor)
            {
                throw new ReplicaDivergedFromMasterException(since.Sequence);
            }
        }

        var page = rows.Take(max).Select(ToEnvelope).ToList();
        return new PullResult(page, page.Count > 0 ? new SyncCursor(page[^1].Sequence, page[^1].ChangeId) : since, rows.Count > max);
    }

    /// <summary>
    /// Online sign-off. The master computes the content hash it seals; the caller must hold a step-up grant for
    /// exactly that hash (ADR-0005), which is spent in the same transaction as the sign-off. The grant is not spent
    /// when the working paper's own rules refuse the sign-off.
    /// </summary>
    public async ValueTask<SignOffAttempt> SignOffAsync(
        Guid engagementId, Guid documentId, EngagementRole level, Guid reviewer, string? stepUpToken, ISignOffStepUp stepUp,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stepUp);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var unit = await OpenAsync(engagementId, [], cancellationToken);
        if (unit.Refusal is { } locked)
        {
            return new SignOffAttempt(SignOffStatus.Locked, locked, Guid.Empty, null, new SyncCursor(unit.Sequence));
        }

        var loaded = await LoadAsync(unit, documentId, cancellationToken);
        var refusal = loaded is null ? "Unknown working paper." : loaded.Doc.WhySignOffIsRefused(level.ToString(), reviewer);
        if (refusal is not null)
        {
            unit.Audit.Add(Event(unit, reviewer, "sync.sign-off.refused", documentId, new { level = level.ToString(), reason = refusal }));
            await CommitAsync(unit, tx, cancellationToken);
            return new SignOffAttempt(SignOffStatus.Refused, refusal, Guid.Empty, null, new SyncCursor(unit.Sequence));
        }

        var hash = loaded!.Doc.ComputeContentHash();
        if (!await stepUp.ConsumeAsync(reviewer, stepUpToken, documentId, hash, cancellationToken))
        {
            unit.Audit.Add(Event(unit, reviewer, "sync.sign-off.step_up_refused", documentId, new { level = level.ToString(), contentHash = hash }));
            await CommitAsync(unit, tx, cancellationToken);
            return new SignOffAttempt(SignOffStatus.StepUpRefused,
                "A fresh authenticator code for this working paper at its current content is required.", Guid.Empty, hash, new SyncCursor(unit.Sequence));
        }

        var envelope = new ChangeEnvelope(Guid.NewGuid(), engagementId, documentId, reviewer, MasterClientId, unit.Now,
            FieldPayload.Kind, FieldPayload.Encode(new SignOffPaper(level.ToString(), hash)));
        var result = await AdmitAsync(unit, envelope, fromMaster: true, cancellationToken);
        if (result.Outcome != ChangeOutcome.Accepted)
        {
            // Cannot happen after the check above; if it does, the grant must not be spent for nothing.
            throw new InvalidOperationException($"Sign-off was refused after its checks passed: {result.Reason}");
        }

        await CommitAsync(unit, tx, cancellationToken);
        return new SignOffAttempt(SignOffStatus.SignedOff, null, envelope.ChangeId, hash, new SyncCursor(unit.Sequence));
    }

    /// <summary>The materialised state of one working paper, or null if the master has none.</summary>
    public async ValueTask<FieldDocument?> GetDocumentAsync(Guid engagementId, Guid documentId, CancellationToken cancellationToken)
    {
        var state = await db.SyncDocuments.AsNoTracking()
            .Where(d => d.DocumentId == documentId && d.EngagementId == engagementId)
            .Select(d => d.State)
            .FirstOrDefaultAsync(cancellationToken);
        return state is null ? null : FieldDocument.FromJson(state);
    }

    /// <summary>
    /// Folds the change log of one working paper from the beginning, ignoring the materialised state. Used to prove
    /// that the stored document is exactly what the log says, and to rebuild it.
    /// </summary>
    public async ValueTask<FieldDocument> ReplayDocumentAsync(Guid engagementId, Guid documentId, CancellationToken cancellationToken)
    {
        var doc = new FieldDocument(documentId, engagementId);
        var rows = db.Changes.AsNoTracking()
            .Where(c => c.EngagementId == engagementId && c.DocumentId == documentId)
            .OrderBy(c => c.Sequence)
            .AsAsyncEnumerable();
        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            if (doc.Apply(row.ChangeId, row.AuthorUserId, FieldPayload.Decode(row.Payload)).Decision == Decision.Rejected)
            {
                throw new InvalidOperationException($"Change {row.ChangeId} at sequence {row.Sequence} is in the log but the reducer rejects it.");
            }
        }

        return doc;
    }

    public async ValueTask<long> HeadAsync(Guid engagementId, CancellationToken cancellationToken) =>
        await db.Changes.Where(c => c.EngagementId == engagementId).MaxAsync(c => (long?)c.Sequence, cancellationToken) ?? 0;

    // ----- One transaction ------------------------------------------------------------------------------------------

    private sealed class Loaded(FieldDocument doc, SyncDocumentRow? row)
    {
        public FieldDocument Doc { get; } = doc;
        public SyncDocumentRow? Row { get; set; } = row;
        public bool Dirty { get; set; }
    }

    private sealed class Unit(Guid engagementId, DateTimeOffset now)
    {
        public Guid EngagementId { get; } = engagementId;
        public DateTimeOffset Now { get; } = now;
        public string? Refusal { get; set; }
        public long Sequence { get; set; }
        public Dictionary<Guid, SyncReceiptRow> Receipts { get; set; } = [];
        public Dictionary<Guid, Loaded?> Docs { get; } = [];
        public List<AuditEvent> Audit { get; } = [];
        public List<Guid>? Reviewers { get; set; }
        public Dictionary<Guid, HashSet<EngagementRole>> Roles { get; } = [];
        public bool HasNotifications { get; set; }
    }

    private async Task<Unit> OpenAsync(Guid engagementId, Guid[] changeIds, CancellationToken cancellationToken)
    {
        // Truncated to microseconds, which is what PostgreSQL stores: what is returned now equals what is read back later.
        var now = clock.GetUtcNow();
        var unit = new Unit(engagementId, new DateTimeOffset(now.UtcTicks - (now.UtcTicks % 10), TimeSpan.Zero));

        // The row lock orders every writer of this engagement, and whoever locks the engagement takes the same lock:
        // a change is decided either wholly before the lock or wholly after it.
        var status = await db.Database
            .SqlQuery<string>($"""SELECT status AS "Value" FROM engagements WHERE id = {engagementId} FOR UPDATE""")
            .ToListAsync(cancellationToken);
        if (status.Count == 0)
        {
            unit.Refusal = "Unknown engagement.";
            return unit;
        }

        unit.Sequence = await HeadAsync(engagementId, cancellationToken);
        if (status[0] is nameof(EngagementStatus.Locked) or nameof(EngagementStatus.Archived))
        {
            unit.Refusal = LockedReason;
            return unit;
        }

        if (changeIds.Length > 0)
        {
            unit.Receipts = await db.SyncReceipts.AsNoTracking()
                .Where(r => changeIds.Contains(r.ChangeId))
                .ToDictionaryAsync(r => r.ChangeId, cancellationToken);
        }

        return unit;
    }

    private async Task CommitAsync(Unit unit, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx, CancellationToken cancellationToken)
    {
        foreach (var loaded in unit.Docs.Values)
        {
            if (loaded is not { Dirty: true, Doc.Exists: true })
            {
                continue;
            }

            var doc = loaded.Doc;
            if (loaded.Row is null)
            {
                loaded.Row = new SyncDocumentRow
                {
                    DocumentId = doc.Id, EngagementId = doc.EngagementId, TemplateCode = doc.TemplateCode, Status = "", ContentHash = "", State = "",
                };
                db.SyncDocuments.Add(loaded.Row);
            }

            loaded.Row.PreparerId = doc.PreparerId;
            loaded.Row.Status = doc.Status.ToString();
            loaded.Row.ContentHash = doc.ComputeContentHash();
            loaded.Row.OpenConflicts = doc.OpenConflicts.Count();
            loaded.Row.LastSequence = unit.Sequence;
            loaded.Row.UpdatedAt = unit.Now;
            loaded.Row.State = doc.ToJson();
            loaded.Dirty = false;
        }

        await auditLog.AppendManyAsync(unit.Audit, cancellationToken);
        unit.Audit.Clear();
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        if (unit.HasNotifications)
        {
            notifications.Pulse();
        }
    }

    private async Task<Loaded?> LoadAsync(Unit unit, Guid documentId, CancellationToken cancellationToken)
    {
        if (unit.Docs.TryGetValue(documentId, out var cached))
        {
            return cached;
        }

        var row = await db.SyncDocuments.FirstOrDefaultAsync(d => d.DocumentId == documentId, cancellationToken);
        Loaded? loaded = row switch
        {
            null => new Loaded(new FieldDocument(documentId, unit.EngagementId), null),
            _ when row.EngagementId != unit.EngagementId => null,
            _ => new Loaded(FieldDocument.FromJson(row.State), row),
        };
        unit.Docs[documentId] = loaded;
        return loaded;
    }

    private async Task<ChangeResult> AdmitAsync(Unit unit, ChangeEnvelope change, bool fromMaster, CancellationToken cancellationToken)
    {
        if (unit.Receipts.TryGetValue(change.ChangeId, out var receipt))
        {
            if (receipt.EngagementId != unit.EngagementId)
            {
                return new ChangeResult(change.ChangeId, ChangeOutcome.Rejected, "This change id was already used elsewhere.");
            }

            // A redelivery gets its original answer, so a lost answer cannot turn a conflict or a rejection into "delivered".
            var original = Enum.Parse<ChangeOutcome>(receipt.Outcome);
            return new ChangeResult(change.ChangeId, original == ChangeOutcome.Accepted ? ChangeOutcome.Duplicate : original, receipt.Reason);
        }

        FieldOp? op = null;
        ApplyResult decided;
        Loaded? loaded = null;
        try
        {
            op = FieldPayload.Decode(change.Payload);
        }
        catch (JsonException)
        {
            // The parser's message can quote the payload; the author only needs to know it was not understood.
        }

        if (op is null)
        {
            decided = ApplyResult.Reject("Malformed field-v1 payload.");
        }
        else if (op.MasterOnly && !fromMaster)
        {
            decided = ApplyResult.Reject("This operation needs an online, stepped-up session on the master.");
        }
        else if ((loaded = await LoadAsync(unit, change.DocumentId, cancellationToken)) is null)
        {
            decided = ApplyResult.Reject("Working paper belongs to another engagement.");
        }
        else if (!fromMaster && FieldPolicy.WhyRefused(op, await RolesAsync(unit, change.AuthorUserId, cancellationToken), loaded.Doc, change.AuthorUserId) is { } notAllowed)
        {
            // Membership opened the door; the role decides what may be done inside (ADR-0004, "Authorisation").
            decided = ApplyResult.Reject(notAllowed);
        }
        else if (op is LinkEvidence link && !await EvidenceReceivedAsync(unit, link.ContentHash, cancellationToken))
        {
            // The record never points at a file the master cannot produce (ADR-0004, "Evidence").
            decided = ApplyResult.Reject(FieldPolicy.EvidenceNotReceived);
        }
        else
        {
            decided = loaded.Doc.Apply(change.ChangeId, change.AuthorUserId, op);
        }

        var outcome = decided.Decision switch
        {
            Decision.Accepted => ChangeOutcome.Accepted,
            Decision.Conflict => ChangeOutcome.Conflict,
            _ => ChangeOutcome.Rejected,
        };

        long? sequence = null;
        if (outcome != ChangeOutcome.Rejected)
        {
            // Accepted changes and flagged conflicts are both part of the shared record; rejections are not.
            sequence = ++unit.Sequence;
            loaded!.Dirty = true;
            db.Changes.Add(new ChangeRow
            {
                EngagementId = unit.EngagementId, Sequence = sequence.Value, ChangeId = change.ChangeId, DocumentId = change.DocumentId,
                AuthorUserId = change.AuthorUserId, ClientId = change.ClientId, ClientCreatedAt = change.CreatedAt.ToUniversalTime(), ReceivedAt = unit.Now,
                PayloadKind = change.PayloadKind, Payload = change.Payload,
            });
        }

        var row = new SyncReceiptRow
        {
            ChangeId = change.ChangeId, EngagementId = unit.EngagementId, DocumentId = change.DocumentId, AuthorUserId = change.AuthorUserId,
            Outcome = outcome.ToString(), Reason = decided.Reason, Sequence = sequence, ReceivedAt = unit.Now,
        };
        db.SyncReceipts.Add(row);
        unit.Receipts[change.ChangeId] = row;

        unit.Audit.Add(Event(unit, change.AuthorUserId, $"sync.{op?.Name ?? "malformed"}.{outcome.ToString().ToLowerInvariant()}", change.DocumentId, new
        {
            changeId = change.ChangeId,
            sequence,
            clientId = change.ClientId,
            clientCreatedAt = change.CreatedAt,
            change = op is null ? (JsonElement?)null : FieldPayload.Describe(op),
            before = decided.Field?.Before,
            reason = decided.Reason,
        }));

        if (decided.SealBroken)
        {
            await RecordBrokenSealAsync(unit, change, loaded!.Doc, decided, cancellationToken);
        }

        return new ChangeResult(change.ChangeId, outcome, decided.Reason);
    }

    private async Task<bool> EvidenceReceivedAsync(Unit unit, string hash, CancellationToken cancellationToken) =>
        AuditPlatform.Evidence.EvidenceHash.IsValid(hash)
        && await db.EvidenceIntake.AsNoTracking().AnyAsync(i => i.EngagementId == unit.EngagementId && i.Hash == hash, cancellationToken);

    private async Task<HashSet<EngagementRole>> RolesAsync(Unit unit, Guid userId, CancellationToken cancellationToken)
    {
        if (!unit.Roles.TryGetValue(userId, out var roles))
        {
            roles = [.. await db.EngagementMembers.AsNoTracking()
                .Where(m => m.EngagementId == unit.EngagementId && m.UserId == userId)
                .Select(m => m.Role)
                .ToListAsync(cancellationToken)];
            unit.Roles[userId] = roles;
        }

        return roles;
    }

    /// <summary>
    /// Compensating control for "editing after sign-off is allowed" (ADR-0004): the break is written to the audit log
    /// with the sign-offs it voided, and every reviewer of the engagement is owed a notification.
    /// </summary>
    private async Task RecordBrokenSealAsync(Unit unit, ChangeEnvelope change, FieldDocument doc, ApplyResult decided, CancellationToken cancellationToken)
    {
        unit.Audit.Add(Event(unit, change.AuthorUserId, "sync.seal.broken", change.DocumentId, new
        {
            changeId = change.ChangeId,
            sequence = unit.Sequence,
            field = decided.Field?.Key,
            contentHash = doc.ComputeContentHash(),
            brokenSignOffs = decided.BrokenSignOffs.Select(s => new
            {
                signOffChangeId = s.ChangeId, level = s.Level.ToString(), signedBy = s.SignedBy, contentHash = s.ContentHash,
            }),
        }));

        unit.Reviewers ??= await db.EngagementMembers.AsNoTracking()
            .Where(m => m.EngagementId == unit.EngagementId && (m.Role == EngagementRole.ManagerQa || m.Role == EngagementRole.JapanDesk))
            .Select(m => m.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        // The people whose seal was broken, and whoever reviews this engagement now. Not the person who made the change.
        var recipients = decided.BrokenSignOffs.Select(s => s.SignedBy).Concat(unit.Reviewers).Distinct().Where(u => u != change.AuthorUserId);
        foreach (var recipient in recipients.Order())
        {
            db.Notifications.Add(new NotificationRow
            {
                Id = Guid.NewGuid(), Kind = SealBrokenKind, RecipientUserId = recipient, EngagementId = unit.EngagementId,
                DocumentId = change.DocumentId, ChangeId = change.ChangeId, ActorUserId = change.AuthorUserId, CreatedAt = unit.Now,
            });
            unit.HasNotifications = true;
        }
    }

    private static AuditEvent Event(Unit unit, Guid actor, string action, Guid documentId, object payload) =>
        new(unit.Now, actor, action, "working_paper", documentId.ToString("D"), unit.EngagementId, JsonSerializer.Serialize(payload, Json));

    private static ChangeEnvelope ToEnvelope(ChangeRow row) =>
        new(row.ChangeId, row.EngagementId, row.DocumentId, row.AuthorUserId, row.ClientId, row.ClientCreatedAt,
            row.PayloadKind, row.Payload, row.Sequence, row.ReceivedAt);
}
