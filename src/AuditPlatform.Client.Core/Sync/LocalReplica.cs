using System.Text.Json;
using AuditPlatform.Client.Core.LocalStore;
using AuditPlatform.Contracts.Sync;
using AuditPlatform.Sync;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Client.Core.Sync;

/// <summary>
/// The replica cannot follow the master's log: a gap, an unknown change, or a change the local reducer would refuse
/// (for a sign-off: the content hash it recomputed differs from the one the master sealed). The replica stops at the
/// last good change instead of drifting; it has to be rebuilt from the master.
/// </summary>
public sealed class ReplicaDivergedException(string message, Exception? inner = null, bool rebuildHelps = true) : Exception(message, inner)
{
    /// <summary>
    /// False when the cause is not in the replica but in this client (a change it cannot read): a rebuild would read
    /// the same change again and stop again, so it is not offered; the client has to be updated.
    /// </summary>
    public bool RebuildHelps { get; } = rebuildHelps;
}

/// <param name="Resubmitted">Changes this laptop made that the old master had accepted, put back in the outbox to be sent again.</param>
/// <param name="PendingKept">Changes that were already waiting in the outbox.</param>
public sealed record ReplicaRebuild(int Resubmitted, int PendingKept);

public sealed class SealBrokenEventArgs(Guid engagementId, Guid documentId, Guid changeId, Guid changedBy, IReadOnlyList<SignOffRecord> brokenSignOffs) : EventArgs
{
    public Guid EngagementId { get; } = engagementId;
    public Guid DocumentId { get; } = documentId;

    /// <summary>The edit that changed signed content.</summary>
    public Guid ChangeId { get; } = changeId;
    public Guid ChangedBy { get; } = changedBy;
    public IReadOnlyList<SignOffRecord> BrokenSignOffs { get; } = brokenSignOffs;
}

/// <summary>
/// The SQLite replica of ADR-0004: the master's log as pulled (<c>replica_log</c>) and the working papers folded
/// from it (<c>documents</c>) with the same reducer the master runs. It never contains anything the master has not
/// decided, so after an engagement is locked it equals the archive.
/// </summary>
public sealed class LocalReplica(LocalDbContext db) : ILocalChangeSink
{
    /// <summary>
    /// Raised when a pulled change broke the seal of a signed working paper, so the client can tell the reviewer.
    /// The broken sign-off itself is part of the replicated document and survives restarts.
    /// </summary>
    public event EventHandler<SealBrokenEventArgs>? SealBroken;

    /// <summary>The confirmed state of a working paper; an empty, non-existent document if the replica has none.</summary>
    public async Task<FieldDocument> GetDocumentAsync(Guid engagementId, Guid documentId, CancellationToken cancellationToken)
    {
        var state = await db.Documents.AsNoTracking()
            .Where(d => d.DocumentId == documentId && d.EngagementId == engagementId)
            .Select(d => d.State)
            .FirstOrDefaultAsync(cancellationToken);
        return state is null ? new FieldDocument(documentId, engagementId) : FieldDocument.FromJson(state);
    }

    /// <summary>
    /// Throws away this laptop's copy of the engagement's log and documents so that the next pull reads them again from
    /// the master, for a replica that cannot follow the master (a gap, an unreadable change) or whose history is not the
    /// master's any more (the master was restored from an older backup). Nothing the author did is lost:
    /// <list type="bullet">
    /// <item>changes still waiting in the outbox stay there, and keep their order;</item>
    /// <item>changes this laptop made that the old master had accepted (they are in the replica's log, not in the outbox)
    /// go back to the outbox, before the waiting ones, because a restored master may no longer have them. The master
    /// answers each as it would any push: a duplicate if it has it, otherwise it decides it again and may flag a conflict;</item>
    /// <item>flagged and refused changes keep their state and their reason.</item>
    /// </list>
    /// Evidence files and the directory are not touched.
    /// </summary>
    /// <param name="clientId">This installation's id: the log rows it authored carry it.</param>
    /// <param name="authorUserId">
    /// The signed-in user. Only the changes this user made are put back: the master stamps a pushed change with the
    /// identity of the connection, so another person's work sent under this one's name would be misattributed.
    /// </param>
    public async Task<ReplicaRebuild> RebuildAsync(Guid engagementId, Guid clientId, Guid authorUserId, CancellationToken cancellationToken)
    {
        // Entities the context still tracks would be stale after the delete below.
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        // A locked or archived engagement accepts nothing more: its replica is the archive, and putting changes back
        // would only leave them refused in the outbox for good.
        var readOnly = await db.Engagements.AsNoTracking().AnyAsync(e => e.Id == engagementId && e.IsReadOnly, cancellationToken);
        var own = readOnly
            ? []
            : await db.ReplicaLog.AsNoTracking()
                .Where(r => r.EngagementId == engagementId && r.ClientId == clientId && r.AuthorUserId == authorUserId)
                .OrderBy(r => r.Sequence)
                .ToListAsync(cancellationToken);
        var outbox = await db.Outbox.AsNoTracking().Where(o => o.EngagementId == engagementId).ToListAsync(cancellationToken);
        var known = outbox.Select(o => o.ChangeId).ToHashSet();
        var pending = outbox.Where(o => o.State == OutboxState.Pending).OrderBy(o => o.LocalSequence).ToList();

        await db.ReplicaLog.Where(r => r.EngagementId == engagementId).ExecuteDeleteAsync(cancellationToken);
        await db.Documents.Where(d => d.EngagementId == engagementId).ExecuteDeleteAsync(cancellationToken);
        await db.SyncState.Where(s => s.EngagementId == engagementId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastPulledSequence, 0L).SetProperty(x => x.LastSyncedAt, (DateTimeOffset?)null), cancellationToken);

        // The waiting changes are put back after the resubmitted ones: the outbox is pushed in the order of its rows,
        // and the earlier changes have to reach the master first.
        await db.Outbox.Where(o => o.EngagementId == engagementId && o.State == OutboxState.Pending).ExecuteDeleteAsync(cancellationToken);
        var resubmit = own.Where(r => !known.Contains(r.ChangeId)).ToList();
        foreach (var row in resubmit)
        {
            db.Outbox.Add(new OutboxChangeRow
            {
                ChangeId = row.ChangeId, EngagementId = engagementId, DocumentId = row.DocumentId, AuthorUserId = row.AuthorUserId,
                CreatedAt = row.ClientCreatedAt, PayloadKind = row.PayloadKind, Payload = row.Payload, State = OutboxState.Pending,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var row in pending)
        {
            db.Outbox.Add(new OutboxChangeRow
            {
                ChangeId = row.ChangeId, EngagementId = engagementId, DocumentId = row.DocumentId, AuthorUserId = row.AuthorUserId,
                CreatedAt = row.CreatedAt, PayloadKind = row.PayloadKind, Payload = row.Payload, State = OutboxState.Pending,
                Attempts = row.Attempts, LastError = row.LastError,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return new ReplicaRebuild(resubmit.Count, pending.Count);
    }

    /// <summary>Folds the local copy of the log from the beginning, ignoring the stored document. For verification and rebuild.</summary>
    public async Task<FieldDocument> ReplayDocumentAsync(Guid engagementId, Guid documentId, CancellationToken cancellationToken)
    {
        var doc = new FieldDocument(documentId, engagementId);
        var rows = await db.ReplicaLog.AsNoTracking()
            .Where(r => r.EngagementId == engagementId && r.DocumentId == documentId)
            .OrderBy(r => r.Sequence)
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            doc.Apply(row.ChangeId, row.AuthorUserId, FieldPayload.Decode(row.Payload));
        }

        return doc;
    }

    public async Task ApplyAsync(Guid engagementId, IReadOnlyList<ChangeEnvelope> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var docs = new Dictionary<Guid, (FieldDocument Doc, LocalDocumentRow? Row)>();
        var broken = new List<SealBrokenEventArgs>();
        var ids = changes.Select(c => c.ChangeId).ToList();
        var mine = await db.Outbox.Where(o => ids.Contains(o.ChangeId)).ToDictionaryAsync(o => o.ChangeId, cancellationToken);
        var last = await db.ReplicaLog.Where(r => r.EngagementId == engagementId).MaxAsync(r => (long?)r.Sequence, cancellationToken) ?? 0;

        foreach (var change in changes)
        {
            if (change.Sequence <= last && change.Sequence > 0)
            {
                continue; // Already applied.
            }

            if (change.EngagementId != engagementId || change.Sequence != last + 1 || change.PayloadKind != FieldPayload.Kind)
            {
                throw new ReplicaDivergedException(
                    $"Change {change.ChangeId} (sequence {change.Sequence}, kind '{change.PayloadKind}') does not follow local sequence {last}.");
            }

            if (!docs.TryGetValue(change.DocumentId, out var entry))
            {
                var row = await db.Documents.FirstOrDefaultAsync(d => d.DocumentId == change.DocumentId, cancellationToken);
                entry = (row is null ? new FieldDocument(change.DocumentId, engagementId) : FieldDocument.FromJson(row.State), row);
                docs[change.DocumentId] = entry;
            }

            ApplyResult decided;
            try
            {
                decided = entry.Doc.Apply(change.ChangeId, change.AuthorUserId, FieldPayload.Decode(change.Payload));
            }
            catch (JsonException e)
            {
                throw new ReplicaDivergedException(
                    $"Thay đổi {change.ChangeId} không đọc được bằng phiên bản ứng dụng này: hãy cập nhật ứng dụng. / Change {change.ChangeId} cannot be read by this version of the client: update the application.",
                    e, rebuildHelps: false);
            }

            if (decided.Decision == Decision.Rejected)
            {
                throw new ReplicaDivergedException($"Replica diverged from the master on change {change.ChangeId}: {decided.Reason}");
            }

            db.ReplicaLog.Add(new ReplicaChangeRow
            {
                EngagementId = engagementId, Sequence = change.Sequence, ChangeId = change.ChangeId, DocumentId = change.DocumentId,
                AuthorUserId = change.AuthorUserId, ClientId = change.ClientId, ClientCreatedAt = change.CreatedAt,
                ReceivedAt = change.ReceivedAt, PayloadKind = change.PayloadKind, Payload = change.Payload,
            });
            last = change.Sequence;

            // The log is as good as the master's answer: if the answer to a push was lost, the change settles here.
            if (mine.TryGetValue(change.ChangeId, out var own) && own.State == OutboxState.Pending)
            {
                if (decided.Decision == Decision.Accepted)
                {
                    db.Outbox.Remove(own);
                }
                else
                {
                    own.State = OutboxState.Conflict;
                    own.LastError = decided.Reason;
                }
            }

            if (decided.SealBroken)
            {
                broken.Add(new SealBrokenEventArgs(engagementId, change.DocumentId, change.ChangeId, change.AuthorUserId, decided.BrokenSignOffs));
            }
        }

        foreach (var (doc, existing) in docs.Values)
        {
            var row = existing;
            if (row is null)
            {
                row = new LocalDocumentRow { DocumentId = doc.Id, EngagementId = engagementId, TemplateCode = "", Status = "", ContentHash = "", State = "" };
                db.Documents.Add(row);
            }

            row.TemplateCode = doc.TemplateCode;
            row.Status = doc.Status.ToString();
            row.ContentHash = doc.ComputeContentHash();
            row.OpenConflicts = doc.OpenConflicts.Count();
            row.LastSequence = last;
            row.State = doc.ToJson();
        }

        await db.SaveChangesAsync(cancellationToken);

        // A flagged conflict leaves its author's list once somebody resolved it, or the author superseded it.
        var flagged = await db.Outbox
            .Where(o => o.EngagementId == engagementId && o.State == OutboxState.Conflict)
            .ToListAsync(cancellationToken);
        foreach (var row in flagged)
        {
            if (docs.TryGetValue(row.DocumentId, out var entry)
                && entry.Doc.Conflicts.TryGetValue(row.ChangeId, out var conflict) && conflict.Status != ConflictStatus.Open)
            {
                db.Outbox.Remove(row);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var e in broken)
        {
            SealBroken?.Invoke(this, e);
        }
    }
}
