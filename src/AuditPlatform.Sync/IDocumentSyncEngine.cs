using AuditPlatform.Contracts.Sync;

namespace AuditPlatform.Sync;

/// <summary>
/// Merges client changes into the master's state for one engagement and serves them back in master order
/// (ADR-0004: field-level versioning with master arbitration).
/// Implementations must be idempotent per <see cref="ChangeEnvelope.ChangeId"/>, must never silently overwrite a
/// reviewer's note or conclusion, and must not decide membership: that is enforced by the master before the call.
/// </summary>
public interface IDocumentSyncEngine
{
    /// <summary>Payload kinds this engine accepts, e.g. "field-v1".</summary>
    IReadOnlySet<string> SupportedPayloadKinds { get; }

    ValueTask<PushResult> ApplyAsync(Guid engagementId, IReadOnlyList<ChangeEnvelope> changes, CancellationToken cancellationToken);

    ValueTask<PullResult> ReadSinceAsync(Guid engagementId, SyncCursor since, int maxChanges, CancellationToken cancellationToken);
}
