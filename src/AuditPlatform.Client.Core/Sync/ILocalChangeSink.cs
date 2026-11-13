using AuditPlatform.Contracts.Sync;

namespace AuditPlatform.Client.Core.Sync;

/// <summary>
/// Applies changes pulled from the master to the local replica, in master order. The sync agent calls it inside the
/// transaction that also advances the pull cursor, so a page is applied exactly once or not at all.
/// </summary>
public interface ILocalChangeSink
{
    Task ApplyAsync(Guid engagementId, IReadOnlyList<ChangeEnvelope> changes, CancellationToken cancellationToken);
}
