using AuditPlatform.Client.Core.LocalStore;
using AuditPlatform.Contracts.Sync;
using AuditPlatform.Sync;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Client.Core.Sync;

/// <summary>
/// What one signed-in user does in one engagement on this laptop. Every edit becomes a field-v1 change in the outbox,
/// stamped with the version of what the user was looking at; nothing here needs a connection.
/// What the user sees is the confirmed replica with their own unsent edits laid over it.
/// </summary>
public sealed class EngagementWorkspace(
    LocalDbContext db, SyncAgent agent, LocalReplica replica, ClientOptions options, TimeProvider clock, Guid engagementId, Guid userId)
{
    public Guid EngagementId { get; } = engagementId;
    public Guid UserId { get; } = userId;

    /// <summary>The working paper as the master last confirmed it.</summary>
    public Task<FieldDocument> ConfirmedAsync(Guid documentId, CancellationToken cancellationToken) =>
        replica.GetDocumentAsync(EngagementId, documentId, cancellationToken);

    /// <summary>What the auditor sees in a field: their own unsent edit if there is one, otherwise the confirmed value.</summary>
    public async Task<string?> ViewAsync(Guid documentId, string key, CancellationToken cancellationToken) =>
        (await LastPendingSetAsync(documentId, key, cancellationToken))?.Op.Value
        ?? (await ConfirmedAsync(documentId, cancellationToken)).Value(key);

    /// <summary>Changes of this user that the master flagged or refused, with the reason. They stay until dealt with.</summary>
    public Task<List<OutboxChangeRow>> NeedsAttentionAsync(CancellationToken cancellationToken) =>
        db.Outbox.AsNoTracking()
            .Where(o => o.EngagementId == EngagementId && o.State != OutboxState.Pending)
            .OrderBy(o => o.LocalSequence)
            .ToListAsync(cancellationToken);

    public Task<Guid> CreatePaperAsync(Guid documentId, string templateCode, int templateVersion, CancellationToken cancellationToken) =>
        EnqueueAsync(documentId, new CreatePaper(templateCode, templateVersion, UserId), cancellationToken);

    /// <summary>Replaces the whole value of a field (long text included), based on what the replica holds right now.</summary>
    public async Task<Guid> SetFieldAsync(Guid documentId, string key, string value, CancellationToken cancellationToken) =>
        await SetFieldAsync(documentId, key, value,
            (await ConfirmedAsync(documentId, cancellationToken)).Fields.TryGetValue(key, out var cell) ? cell.ChangeId : null, cancellationToken);

    /// <summary>
    /// Replaces the whole value of a field, based on the version that was on the screen when the user started
    /// typing (<paramref name="seenChangeId"/>, null for an empty field). A form uses this overload: the replica may
    /// have moved on while the user was typing, and the master must then flag the save instead of taking it.
    /// </summary>
    public async Task<Guid> SetFieldAsync(Guid documentId, string key, string value, Guid? seenChangeId, CancellationToken cancellationToken)
    {
        // Chain on the author's own unsent edit, so a series of offline saves of one field is not a conflict with itself.
        var mine = await LastPendingSetAsync(documentId, key, cancellationToken);
        return await EnqueueAsync(documentId, new SetField(key, value, mine?.ChangeId ?? seenChangeId), cancellationToken);
    }

    /// <summary>
    /// A working paper as its form shows it: what the master confirmed, the user's own unsent work on top (a paper
    /// created or submitted offline is already there), and what the master flagged or refused.
    /// </summary>
    public async Task<PaperView> ViewPaperAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var confirmed = await ConfirmedAsync(documentId, cancellationToken);
        var rows = await db.Outbox.AsNoTracking()
            .Where(o => o.EngagementId == EngagementId && o.DocumentId == documentId && o.PayloadKind == FieldPayload.Kind)
            .OrderBy(o => o.LocalSequence)
            .ToListAsync(cancellationToken);
        return PaperView.Build(confirmed, rows);
    }

    /// <summary>Every working paper of the engagement on this laptop, confirmed or still waiting to be sent.</summary>
    public async Task<List<PaperView>> ListPapersAsync(CancellationToken cancellationToken)
    {
        var states = await db.Documents.AsNoTracking().Where(d => d.EngagementId == EngagementId).Select(d => d.State).ToListAsync(cancellationToken);
        var outbox = (await db.Outbox.AsNoTracking()
                .Where(o => o.EngagementId == EngagementId && o.PayloadKind == FieldPayload.Kind)
                .OrderBy(o => o.LocalSequence)
                .ToListAsync(cancellationToken))
            .ToLookup(o => o.DocumentId);
        var confirmed = states.Select(FieldDocument.FromJson).ToDictionary(d => d.Id);
        return [.. confirmed.Keys.Union(outbox.Select(g => g.Key))
            .Select(id => PaperView.Build(confirmed.GetValueOrDefault(id) ?? new FieldDocument(id, EngagementId), [.. outbox[id]]))
            .Where(v => v.Exists)
            .OrderBy(v => v.TemplateCode, StringComparer.Ordinal).ThenBy(v => v.Confirmed.Id)];
    }

    /// <summary>Drops a change the master refused, once its author has read why. Flagged conflicts are not dropped here: a person decides them.</summary>
    public async Task DismissRejectedAsync(Guid changeId, CancellationToken cancellationToken)
    {
        await db.Outbox.Where(o => o.EngagementId == EngagementId && o.ChangeId == changeId && o.State == OutboxState.Rejected)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Closes a flagged conflict by choosing the value to keep, based on the field as the replica shows it now.</summary>
    public async Task<Guid> ResolveConflictAsync(Guid documentId, Guid conflictId, string value, CancellationToken cancellationToken)
    {
        var doc = await ConfirmedAsync(documentId, cancellationToken);
        if (!doc.Conflicts.TryGetValue(conflictId, out var conflict))
        {
            throw new InvalidOperationException("This conflict is not in the local replica; synchronise first.");
        }

        return await EnqueueAsync(documentId,
            new ResolveConflict(conflictId, value, doc.Fields.TryGetValue(conflict.Key, out var cell) ? cell.ChangeId : null), cancellationToken);
    }

    public Task<Guid> LinkEvidenceAsync(Guid documentId, string contentHash, CancellationToken cancellationToken) =>
        EnqueueAsync(documentId, new LinkEvidence(contentHash), cancellationToken);

    public Task<Guid> UnlinkEvidenceAsync(Guid documentId, string contentHash, CancellationToken cancellationToken) =>
        EnqueueAsync(documentId, new UnlinkEvidence(contentHash), cancellationToken);

    public Task<Guid> RaiseNoteAsync(Guid documentId, Guid noteId, string fieldKey, string text, CancellationToken cancellationToken) =>
        EnqueueAsync(documentId, new RaiseNote(noteId, fieldKey, text), cancellationToken);

    public Task<Guid> RespondAsync(Guid documentId, Guid noteId, string response, CancellationToken cancellationToken) =>
        EnqueueAsync(documentId, new RespondNote(noteId, response), cancellationToken);

    /// <summary>Clears a note on the latest response the reviewer can see; the master refuses if a newer one exists.</summary>
    public async Task<Guid> ClearNoteAsync(Guid documentId, Guid noteId, CancellationToken cancellationToken)
    {
        var doc = await ConfirmedAsync(documentId, cancellationToken);
        if (!doc.Notes.TryGetValue(noteId, out var note) || note.Responses.Count == 0)
        {
            throw new InvalidOperationException("A review note can be cleared only after a response has been received.");
        }

        return await EnqueueAsync(documentId, new ClearNote(noteId, note.Responses[^1].ChangeId), cancellationToken);
    }

    public Task<Guid> SubmitForReviewAsync(Guid documentId, CancellationToken cancellationToken) =>
        EnqueueAsync(documentId, new SubmitForReview(), cancellationToken);

    /// <summary>Queues any operation as it is. Sign-off is not queued: it is an online call (see <c>AuthSession.SignOffAsync</c>).</summary>
    public async Task<Guid> EnqueueAsync(Guid documentId, FieldOp op, CancellationToken cancellationToken)
    {
        var envelope = new ChangeEnvelope(Guid.NewGuid(), EngagementId, documentId, UserId, options.ClientId, clock.GetUtcNow(),
            FieldPayload.Kind, FieldPayload.Encode(op));
        await agent.EnqueueAsync(envelope, cancellationToken);
        return envelope.ChangeId;
    }

    private async Task<(Guid ChangeId, SetField Op)?> LastPendingSetAsync(Guid documentId, string key, CancellationToken cancellationToken)
    {
        var pending = await db.Outbox.AsNoTracking()
            .Where(o => o.EngagementId == EngagementId && o.DocumentId == documentId && o.State == OutboxState.Pending
                && o.PayloadKind == FieldPayload.Kind)
            .OrderByDescending(o => o.LocalSequence)
            .Select(o => new { o.ChangeId, o.Payload })
            .ToListAsync(cancellationToken);
        foreach (var row in pending)
        {
            if (FieldPayload.Decode(row.Payload) is SetField set && set.Key == key)
            {
                return (row.ChangeId, set);
            }
        }

        return null;
    }
}
