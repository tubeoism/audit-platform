using AuditPlatform.Client.Core.LocalStore;
using AuditPlatform.Contracts.Sync;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Client.Core.Sync;

/// <summary>
/// Moves changes between the local outbox and the master. Offline-first: local edits are always written to the
/// outbox first and pushed when a connection exists. Nothing is deleted from the outbox until the master answers.
/// </summary>
public sealed class SyncAgent(LocalDbContext db, ISyncTransport transport, ILocalChangeSink sink, ClientOptions options, TimeProvider clock)
{
    public const int PushBatchSize = 200;
    public const int PullBatchSize = 500;

    public async Task EnqueueAsync(ChangeEnvelope change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var engagement = await db.Engagements.AsNoTracking().FirstOrDefaultAsync(e => e.Id == change.EngagementId, cancellationToken);
        if (engagement is { IsReadOnly: true })
        {
            throw new InvalidOperationException($"Engagement {engagement.Code} is locked; local edits are not allowed.");
        }

        db.Outbox.Add(new OutboxChangeRow
        {
            ChangeId = change.ChangeId,
            EngagementId = change.EngagementId,
            DocumentId = change.DocumentId,
            AuthorUserId = change.AuthorUserId,
            CreatedAt = change.CreatedAt,
            PayloadKind = change.PayloadKind,
            Payload = change.Payload,
            State = OutboxState.Pending,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Pushes pending changes for one engagement. Returns the number of changes the master accepted or already had.</summary>
    public async Task<int> PushPendingAsync(Guid engagementId, CancellationToken cancellationToken)
    {
        if (transport.State != ConnectionState.Online)
        {
            return 0;
        }

        var delivered = 0;
        while (true)
        {
            var batch = await db.Outbox
                .Where(o => o.EngagementId == engagementId && o.State == OutboxState.Pending)
                .OrderBy(o => o.CreatedAt)
                .Take(PushBatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                return delivered;
            }

            var result = await transport.PushAsync(engagementId, [.. batch.Select(b => b.ToEnvelope(options.ClientId))], cancellationToken);
            var byId = batch.ToDictionary(b => b.ChangeId);
            foreach (var r in result.Results)
            {
                if (!byId.TryGetValue(r.ChangeId, out var row))
                {
                    continue;
                }

                row.Attempts++;
                switch (r.Outcome)
                {
                    case ChangeOutcome.Accepted:
                    case ChangeOutcome.Duplicate:
                        db.Outbox.Remove(row);
                        delivered++;
                        break;
                    case ChangeOutcome.Conflict:
                        row.State = OutboxState.Conflict;
                        row.LastError = r.Reason;
                        break;
                    case ChangeOutcome.Rejected:
                        row.State = OutboxState.Rejected;
                        row.LastError = r.Reason;
                        break;
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            if (result.Results.Count == 0)
            {
                // Master answered nothing for this batch; stop rather than loop forever.
                return delivered;
            }
        }
    }

    /// <summary>Pulls everything the master has after the stored cursor and hands it to the local replica.</summary>
    public async Task<int> PullAsync(Guid engagementId, CancellationToken cancellationToken)
    {
        if (transport.State != ConnectionState.Online)
        {
            return 0;
        }

        var state = await db.SyncState.FirstOrDefaultAsync(s => s.EngagementId == engagementId, cancellationToken);
        if (state is null)
        {
            state = new SyncStateRow { EngagementId = engagementId };
            db.SyncState.Add(state);
        }

        var received = 0;
        PullResult page;
        do
        {
            page = await transport.PullAsync(engagementId, new SyncCursor(state.LastPulledSequence), PullBatchSize, cancellationToken);
            if (page.Changes.Count > 0)
            {
                await sink.ApplyAsync(engagementId, page.Changes, cancellationToken);
                received += page.Changes.Count;
            }

            state.LastPulledSequence = page.Cursor.Sequence;
            state.LastSyncedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }
        while (page.HasMore);

        return received;
    }
}
