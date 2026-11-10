using AuditPlatform.Contracts.Sync;

namespace AuditPlatform.Client.Core.Sync;

/// <summary>Applies changes pulled from the master to the local replica. Implemented with the chosen sync engine (ADR-0004).</summary>
public interface ILocalChangeSink
{
    Task ApplyAsync(Guid engagementId, IReadOnlyList<ChangeEnvelope> changes, CancellationToken cancellationToken);
}
