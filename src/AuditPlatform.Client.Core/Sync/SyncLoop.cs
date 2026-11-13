using AuditPlatform.Client.Core.Directory;
using AuditPlatform.Client.Core.LocalStore;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Client.Core.Sync;

/// <param name="Pending">Changes waiting to be sent.</param>
/// <param name="NeedsAttention">Changes the master flagged or refused.</param>
/// <param name="Error">Why the last round did not complete; null when it did.</param>
public sealed record SyncStatus(ConnectionState Connection, int Pending, int NeedsAttention, DateTimeOffset? LastSyncedAt, string? Error);

/// <summary>
/// The client's sync loop (ADR-0004, open item 2): connect, read the directory, subscribe, push and pull. A round
/// runs when the master signals changes, when the user saves, when the connection comes back, and on a timer.
/// Everything it does can be repeated: a round that fails halfway is simply run again.
/// </summary>
public sealed class SyncLoop : IAsyncDisposable
{
    private readonly LocalUnitFactory _units;
    private readonly ISyncTransport _transport;
    private readonly IDirectorySource _directory;
    private readonly ClientOptions _options;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _round = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<Guid> _subscribed = [];
    private readonly Dictionary<Guid, string> _stopped = [];
    private readonly Lock _gate = new();
    private readonly HashSet<Guid> _locked = [];
    private Task? _running;
    private bool _directoryDue = true;

    public SyncLoop(LocalUnitFactory units, ISyncTransport transport, IDirectorySource directory, ClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _units = units;
        _transport = transport;
        _directory = directory;
        _options = options;
        _transport.ChangesAvailable += (_, _) => Poke();
        _transport.EngagementLocked += (_, id) =>
        {
            lock (_gate)
            {
                _locked.Add(id);
            }

            Poke();
        };
        _transport.StateChanged += (_, state) =>
        {
            if (state != ConnectionState.Online)
            {
                // Group membership on the hub dies with the connection: subscribe again after every reconnect.
                lock (_gate)
                {
                    _subscribed.Clear();
                    _directoryDue = true;
                }
            }

            Poke();
        };
    }

    public SyncStatus Status { get; private set; } = new(ConnectionState.Offline, 0, 0, null, null);

    public event EventHandler<SyncStatus>? StatusChanged;

    /// <summary>The local copy of the directory or of an engagement changed; forms reload.</summary>
    public event EventHandler? DataChanged;

    /// <summary>A pulled change broke the seal of a signed working paper.</summary>
    public event EventHandler<SealBrokenEventArgs>? SealBroken;

    /// <summary>Engagements this laptop has stopped synchronising, with the reason (a replica that cannot follow the master).</summary>
    public IReadOnlyDictionary<Guid, string> Stopped
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<Guid, string>(_stopped);
            }
        }
    }

    public void Start() => _running ??= Task.Run(() => RunAsync(_stop.Token));

    /// <summary>Asks for a round as soon as possible. Cheap, and safe from any thread.</summary>
    public void Poke()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // A round is already requested.
        }
        catch (ObjectDisposedException)
        {
            // The application is closing.
        }
    }

    /// <summary>One round. Never throws for an unreachable or refusing master: the outcome is in <see cref="Status"/>.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await _round.WaitAsync(cancellationToken);
        string? error = null;
        var changed = false;
        try
        {
            await using var unit = await _units.OpenAsync(cancellationToken);
            unit.Replica.SealBroken += (_, e) => SealBroken?.Invoke(this, e);
            try
            {
                changed |= await ApplyLocksAsync(unit, cancellationToken);
                if (_transport.State == ConnectionState.Offline)
                {
                    await _transport.ConnectAsync(cancellationToken);
                }

                if (_transport.State == ConnectionState.Online)
                {
                    changed |= await RefreshDirectoryAsync(unit, cancellationToken);
                    foreach (var engagementId in await unit.Db.Engagements.AsNoTracking().Select(e => e.Id).ToListAsync(cancellationToken))
                    {
                        (var moved, var problem) = await SyncEngagementAsync(unit, engagementId, cancellationToken);
                        changed |= moved;
                        error ??= problem;
                    }
                }
            }
#pragma warning disable CA1031 // Whatever stops a round (no network, a refused token, a busy database), the next round tries again.
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
            {
                error = ex.Message;
                unit.Db.ChangeTracker.Clear();
            }

            Status = new SyncStatus(
                _transport.State,
                await unit.Db.Outbox.CountAsync(o => o.State == OutboxState.Pending, cancellationToken),
                await unit.Db.Outbox.CountAsync(o => o.State != OutboxState.Pending, cancellationToken),
                (await unit.Db.SyncState.AsNoTracking().Select(s => s.LastSyncedAt).ToListAsync(cancellationToken)).Max(),
                error);
        }
        finally
        {
            _round.Release();
        }

        StatusChanged?.Invoke(this, Status);
        if (changed)
        {
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_running is not null)
        {
            try
            {
                await _running;
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _stop.Dispose();
        _wake.Dispose();
        _round.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await RunOnceAsync(cancellationToken);

            // Whichever comes first: a poke, or the interval.
            await _wake.WaitAsync(_options.SyncInterval, cancellationToken);
        }
    }

    private async Task<bool> ApplyLocksAsync(LocalUnit unit, CancellationToken cancellationToken)
    {
        Guid[] locked;
        lock (_gate)
        {
            locked = [.. _locked];
            _locked.Clear();
        }

        foreach (var id in locked)
        {
            await unit.Agent.MarkLockedAsync(id, cancellationToken);
        }

        return locked.Length > 0;
    }

    private async Task<bool> RefreshDirectoryAsync(LocalUnit unit, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_directoryDue)
            {
                return false;
            }
        }

        var snapshot = await _directory.FetchAsync(cancellationToken);
        await DirectoryStore.ApplyAsync(unit.Db, snapshot, cancellationToken);
        lock (_gate)
        {
            // Read again on the next round if the master did not answer in full.
            _directoryDue = snapshot.Engagements is null || snapshot.Templates is null;
        }

        return snapshot.Engagements is not null || snapshot.Templates is not null;
    }

    private async Task<(bool Changed, string? Error)> SyncEngagementAsync(LocalUnit unit, Guid engagementId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_stopped.TryGetValue(engagementId, out var reason))
            {
                return (false, reason);
            }
        }

        try
        {
            bool subscribed;
            lock (_gate)
            {
                subscribed = _subscribed.Contains(engagementId);
            }

            if (!subscribed)
            {
                await _transport.SubscribeAsync(engagementId, cancellationToken);
                lock (_gate)
                {
                    _subscribed.Add(engagementId);
                }
            }

            var (delivered, received) = await unit.Agent.SyncAsync(engagementId, cancellationToken);
            return (delivered + received > 0, null);
        }
        catch (ReplicaDivergedException ex)
        {
            // Never retried on its own: the replica stays at its last good change until it is rebuilt from the master.
            unit.Db.ChangeTracker.Clear();
            lock (_gate)
            {
                _stopped[engagementId] = ex.Message;
            }

            return (false, ex.Message);
        }
    }
}
