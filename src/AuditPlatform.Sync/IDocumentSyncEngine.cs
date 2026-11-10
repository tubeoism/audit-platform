using AuditPlatform.Contracts.Sync;

namespace AuditPlatform.Sync;

/// <summary>
/// Merges client changes into the master's state for one engagement and serves them back in master order.
/// Candidates under evaluation (spike week 1):
/// <list type="bullet">
/// <item>Field-level versioning with master arbitration and flagged conflicts (plain .NET).</item>
/// <item>CRDT via bindings (Yrs through YDotNet, or Automerge).</item>
/// </list>
/// Implementations must be idempotent per <see cref="ChangeEnvelope.ChangeId"/>, must never silently overwrite a
/// reviewer's note or conclusion, and must not perform authorisation: that is enforced by the master before the call.
/// </summary>
public interface IDocumentSyncEngine
{
    /// <summary>Payload kinds this engine accepts, e.g. "field-v1".</summary>
    IReadOnlySet<string> SupportedPayloadKinds { get; }

    ValueTask<PushResult> ApplyAsync(Guid engagementId, IReadOnlyList<ChangeEnvelope> changes, CancellationToken cancellationToken);

    ValueTask<PullResult> ReadSinceAsync(Guid engagementId, SyncCursor since, int maxChanges, CancellationToken cancellationToken);
}
