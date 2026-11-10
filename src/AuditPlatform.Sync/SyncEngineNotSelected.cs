using AuditPlatform.Contracts.Sync;

namespace AuditPlatform.Sync;

/// <summary>
/// Placeholder registered until ADR-0004 selects a sync model. Every call fails loudly so that no data is accepted
/// by an engine whose merge semantics have not been validated.
/// </summary>
public sealed class SyncEngineNotSelected : IDocumentSyncEngine
{
    private const string Message = "No sync engine has been selected yet (ADR-0004, spike week 1).";

    public IReadOnlySet<string> SupportedPayloadKinds { get; } = new HashSet<string>();

    public ValueTask<PushResult> ApplyAsync(Guid engagementId, IReadOnlyList<ChangeEnvelope> changes, CancellationToken cancellationToken) =>
        throw new NotSupportedException(Message);

    public ValueTask<PullResult> ReadSinceAsync(Guid engagementId, SyncCursor since, int maxChanges, CancellationToken cancellationToken) =>
        throw new NotSupportedException(Message);
}
