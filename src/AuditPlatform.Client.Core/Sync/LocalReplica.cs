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
public sealed class ReplicaDivergedException(string message, Exception? inner = null) : Exception(message, inner);

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
                throw new ReplicaDivergedException($"Change {change.ChangeId} cannot be read by this version of the client.", e);
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
