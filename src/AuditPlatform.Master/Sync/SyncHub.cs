using AuditPlatform.Contracts.Sync;
using AuditPlatform.Master.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace AuditPlatform.Master.Sync;

[Authorize]
internal sealed class SyncHub(SyncCoordinator coordinator, IOptions<MasterOptions> options) : Hub<ISyncClient>, ISyncHub
{
    public async Task Subscribe(Guid engagementId)
    {
        await Guard(() => coordinator.EnsureMemberAsync(Context.User.GetUserId(), engagementId, Context.ConnectionAborted).AsTask());
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(engagementId), Context.ConnectionAborted);
    }

    public Task Unsubscribe(Guid engagementId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(engagementId), Context.ConnectionAborted);

    public async Task<PushResult> Push(Guid engagementId, IReadOnlyList<ChangeEnvelope> changes)
    {
        var result = await Guard(() => coordinator.PushAsync(Context.User.GetUserId(), engagementId, changes, Context.ConnectionAborted).AsTask());
        if (result.Results.Any(r => r.Outcome == ChangeOutcome.Accepted))
        {
            await Clients.OthersInGroup(GroupName(engagementId)).ChangesAvailable(engagementId, result.Cursor);
        }

        return result;
    }

    public Task<PullResult> Pull(Guid engagementId, SyncCursor since, int maxChanges) =>
        Guard(() => coordinator.PullAsync(Context.User.GetUserId(), engagementId, since,
            Math.Clamp(maxChanges, 1, options.Value.MaxPullBatch), Context.ConnectionAborted).AsTask());

    internal static string GroupName(Guid engagementId) => $"engagement:{engagementId:N}";

    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new HubException(ex.Message);
        }
        catch (NotSupportedException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    private static async Task Guard(Func<Task> action) =>
        await Guard(async () => { await action(); return true; });
}
