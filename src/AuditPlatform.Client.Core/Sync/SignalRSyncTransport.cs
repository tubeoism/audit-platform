using AuditPlatform.Contracts.Sync;
using Microsoft.AspNetCore.SignalR.Client;

namespace AuditPlatform.Client.Core.Sync;

/// <summary>SignalR (WebSocket over 443) connection to the master hub with automatic reconnect.</summary>
public sealed class SignalRSyncTransport : ISyncTransport, IAsyncDisposable
{
    private readonly HubConnection _connection;

    public SignalRSyncTransport(ClientOptions options, Func<Task<string?>> accessTokenProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(options.MasterUrl, SyncHubRoutes.Path), o => o.AccessTokenProvider = accessTokenProvider)
            .WithAutomaticReconnect()
            .Build();

        _connection.Reconnecting += _ => SetState(ConnectionState.Connecting);
        _connection.Reconnected += _ => SetState(ConnectionState.Online);
        _connection.Closed += _ => SetState(ConnectionState.Offline);
        _connection.On<Guid, SyncCursor>(nameof(ISyncClient.ChangesAvailable), (id, _) => ChangesAvailable?.Invoke(this, id));
        _connection.On<Guid, DateTimeOffset>(nameof(ISyncClient.EngagementLocked), (id, _) => EngagementLocked?.Invoke(this, id));
    }

    public ConnectionState State { get; private set; }

    public event EventHandler<ConnectionState>? StateChanged;
    public event EventHandler<Guid>? ChangesAvailable;
    public event EventHandler<Guid>? EngagementLocked;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await SetState(ConnectionState.Connecting);
        try
        {
            await _connection.StartAsync(cancellationToken);
            await SetState(ConnectionState.Online);
        }
        catch
        {
            await SetState(ConnectionState.Offline);
            throw;
        }
    }

    public Task SubscribeAsync(Guid engagementId, CancellationToken cancellationToken) =>
        _connection.InvokeAsync(nameof(ISyncHub.Subscribe), engagementId, cancellationToken);

    public Task<PushResult> PushAsync(Guid engagementId, IReadOnlyList<ChangeEnvelope> changes, CancellationToken cancellationToken) =>
        _connection.InvokeAsync<PushResult>(nameof(ISyncHub.Push), engagementId, changes, cancellationToken);

    public Task<PullResult> PullAsync(Guid engagementId, SyncCursor since, int maxChanges, CancellationToken cancellationToken) =>
        _connection.InvokeAsync<PullResult>(nameof(ISyncHub.Pull), engagementId, since, maxChanges, cancellationToken);

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private Task SetState(ConnectionState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
        return Task.CompletedTask;
    }
}
