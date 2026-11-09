using System.Text.Json;
using AuditPlatform.AuditLog;
using AuditPlatform.Contracts.Sync;
using AuditPlatform.Sync;

namespace AuditPlatform.Master.Sync;

/// <summary>
/// Policy layer in front of the sync engine. Enforces, in order: membership, engagement lock, payload kind,
/// author identity. Records each accepted push in the hash-chained audit log. Engine-agnostic by design.
/// </summary>
public sealed class SyncCoordinator(IEngagementAccess access, IDocumentSyncEngine engine, IAuditLog auditLog, TimeProvider clock)
{
    public async ValueTask<PushResult> PushAsync(Guid userId, Guid engagementId, IReadOnlyList<ChangeEnvelope> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var grant = await RequireMemberAsync(userId, engagementId, cancellationToken);

        if (grant.IsReadOnly)
        {
            return new PushResult(
                [.. changes.Select(c => new ChangeResult(c.ChangeId, ChangeOutcome.Rejected, "Engagement is locked."))],
                SyncCursor.Start);
        }

        var rejected = new List<ChangeResult>();
        var admissible = new List<ChangeEnvelope>();
        foreach (var change in changes)
        {
            if (change.EngagementId != engagementId)
            {
                rejected.Add(new ChangeResult(change.ChangeId, ChangeOutcome.Rejected, "Change belongs to a different engagement."));
            }
            else if (!engine.SupportedPayloadKinds.Contains(change.PayloadKind))
            {
                rejected.Add(new ChangeResult(change.ChangeId, ChangeOutcome.Rejected, $"Unsupported payload kind '{change.PayloadKind}'."));
            }
            else
            {
                // The identity on the connection is authoritative; never trust the author claimed by the client.
                admissible.Add(change with { AuthorUserId = userId });
            }
        }

        if (admissible.Count == 0)
        {
            return new PushResult(rejected, SyncCursor.Start);
        }

        var applied = await engine.ApplyAsync(engagementId, admissible, cancellationToken);

        await auditLog.AppendAsync(new AuditEvent(
            clock.GetUtcNow(), userId, "sync.push", "engagement", engagementId.ToString("N"), engagementId,
            JsonSerializer.Serialize(new
            {
                accepted = applied.Results.Count(r => r.Outcome == ChangeOutcome.Accepted),
                duplicate = applied.Results.Count(r => r.Outcome == ChangeOutcome.Duplicate),
                conflict = applied.Results.Count(r => r.Outcome == ChangeOutcome.Conflict),
                rejected = rejected.Count + applied.Results.Count(r => r.Outcome == ChangeOutcome.Rejected),
                cursor = applied.Cursor.Sequence,
            })), cancellationToken);

        return new PushResult([.. rejected, .. applied.Results], applied.Cursor);
    }

    public async ValueTask<PullResult> PullAsync(Guid userId, Guid engagementId, SyncCursor since, int maxChanges, CancellationToken cancellationToken)
    {
        await RequireMemberAsync(userId, engagementId, cancellationToken);
        return await engine.ReadSinceAsync(engagementId, since, maxChanges, cancellationToken);
    }

    public async ValueTask EnsureMemberAsync(Guid userId, Guid engagementId, CancellationToken cancellationToken) =>
        await RequireMemberAsync(userId, engagementId, cancellationToken);

    private async ValueTask<EngagementAccess> RequireMemberAsync(Guid userId, Guid engagementId, CancellationToken cancellationToken)
    {
        var grant = await access.GetAsync(engagementId, userId, cancellationToken);
        // Same failure for "does not exist" and "not a member" so engagement ids cannot be probed.
        if (!grant.Exists || !grant.IsMember)
        {
            throw new UnauthorizedAccessException("Not a member of this engagement.");
        }

        return grant;
    }
}
