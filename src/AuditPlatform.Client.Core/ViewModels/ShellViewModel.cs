using System.Collections.ObjectModel;
using AuditPlatform.Client.Core.Auth;
using AuditPlatform.Client.Core.Evidence;
using AuditPlatform.Client.Core.LocalStore;
using AuditPlatform.Client.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Client.Core.ViewModels;

public sealed record EngagementItem(Guid Id, string Code, string ClientName, bool IsReadOnly)
{
    public string Heading => IsReadOnly ? $"{Code} (đã khoá)" : Code;
}

/// <summary>The main window: who is signed in, the state of the connection and of the outbox, and the user's engagements.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly LocalUnitFactory _units;
    private readonly SyncLoop _loop;
    private readonly AuthSession _session;
    private readonly ClientOptions _options;
    private readonly IUiDispatcher _ui;
    private readonly LocalEvidence _evidence;
    private CurrentUser? _user;

    public ShellViewModel(LocalUnitFactory units, SyncLoop loop, AuthSession session, ClientOptions options, IUiDispatcher ui, LocalEvidence evidence)
    {
        _evidence = evidence;
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(options);
        _units = units;
        _loop = loop;
        _session = session;
        _options = options;
        _ui = ui;
        MasterUrl = options.MasterUrl.ToString();
        _loop.StatusChanged += (_, status) => _ui.Post(() => Show(status));
        _loop.DataChanged += (_, _) => _ui.Post(() => _ = ReloadAsync(CancellationToken.None));
        _loop.SealBroken += (_, _) => _ui.Post(() =>
            LastError = "Một giấy làm việc đã ký vừa bị thay đổi nội dung: chữ ký mất hiệu lực và giấy quay lại trạng thái chờ soát xét.");
        Show(_loop.Status);
    }

    /// <summary>Raised after the user signed out; the application returns to the sign-in window.</summary>
    public event EventHandler? SignedOut;

    public string MasterUrl { get; }

    public ObservableCollection<EngagementItem> Engagements { get; } = [];

    [ObservableProperty]
    private string _userText = "";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _outboxText = "";

    [ObservableProperty]
    private string? _lastError;

    /// <summary>A neutral message about something the user just did (not an error).</summary>
    [ObservableProperty]
    private string? _notice;

    /// <summary>True while some engagement has stopped synchronising because this laptop's copy no longer follows the master.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RebuildCommand))]
    private bool _needsRebuild;

    [ObservableProperty]
    private EngagementItem? _selectedEngagement;

    [ObservableProperty]
    private EngagementViewModel? _engagement;

    [ObservableProperty]
    private string _emptyText = "";

    /// <summary>Opens the shell for a user, online or from the stored session, and starts synchronising.</summary>
    public async Task OpenAsync(CurrentUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (_user != user)
        {
            // Another person on this laptop: nothing of the previous form survives.
            SelectedEngagement = null;
            Engagements.Clear();
        }

        _user = user;
        UserText = $"{user.DisplayName} · {user.Email}";
        await ReloadAsync(cancellationToken);
        _loop.Start();
        _loop.Poke();
    }

    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        if (_user is null)
        {
            return;
        }

        try
        {
            List<EngagementItem> items;
            await using (var unit = await _units.OpenAsync(cancellationToken))
            {
                items = [.. (await unit.Db.Engagements.AsNoTracking().OrderBy(e => e.Code).ToListAsync(cancellationToken))
                    .Select(e => new EngagementItem(e.Id, e.Code, e.ClientName, e.IsReadOnly))];
            }

            if (!Engagements.SequenceEqual(items))
            {
                var keep = SelectedEngagement?.Id;
                Engagements.Clear();
                items.ForEach(Engagements.Add);
                _keepEngagement = true;
                SelectedEngagement = Engagements.FirstOrDefault(e => e.Id == keep) ?? Engagements.FirstOrDefault();
                _keepEngagement = false;
            }

            EmptyText = Engagements.Count == 0
                ? "Chưa có engagement nào được giao cho bạn trên máy này. Kết nối mạng để nhận danh sách từ máy chủ."
                : "";
            if (Engagement is not null)
            {
                await Engagement.ReloadAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException or Microsoft.Data.Sqlite.SqliteException)
        {
            LastError = ex.Message;
        }
    }

    private bool _keepEngagement;

    partial void OnSelectedEngagementChanged(EngagementItem? value)
    {
        if (_keepEngagement && value?.Id == Engagement?.EngagementId && Engagement is not null)
        {
            return;
        }

        Engagement = value is null || _user is null ? null : new EngagementViewModel(_units, _loop, _options, _user, value.Id, _evidence, _session);
        _ = Engagement?.ReloadAsync(CancellationToken.None);
    }

    [RelayCommand]
    private void SyncNow() => _loop.Poke();

    /// <summary>
    /// Rebuilds the copy of every engagement that stopped following the master, e.g. after the master was restored
    /// from an older backup. Nothing the user did is lost: unsent changes stay, and changes the old master had
    /// accepted that the restored one may lack are sent again.
    /// </summary>
    [RelayCommand(CanExecute = nameof(NeedsRebuild))]
    private async Task RebuildAsync(CancellationToken cancellationToken)
    {
        var stopped = _loop.Stopped.Keys.ToList();
        var resent = 0;
        foreach (var id in stopped)
        {
            resent += (await _loop.RebuildAsync(id, cancellationToken)).Resubmitted;
        }

        NeedsRebuild = _loop.Stopped.Count > 0;
        LastError = null;
        Notice = resent > 0
            ? $"Đã dựng lại {stopped.Count} engagement từ master; {resent} thay đổi của bạn được gửi lại."
            : $"Đã dựng lại {stopped.Count} engagement từ master. Không có thay đổi nào của bạn bị mất.";
    }

    /// <summary>
    /// Signs out. Refused while this user has unsent changes: the next person on this laptop must not be able to
    /// send them under their own name.
    /// </summary>
    [RelayCommand]
    private async Task SignOutAsync(CancellationToken cancellationToken)
    {
        await using (var unit = await _units.OpenAsync(cancellationToken))
        {
            var pending = await unit.Db.Outbox.CountAsync(cancellationToken);
            var files = await unit.Db.Evidence.CountAsync(e => e.Upload == EvidenceUpload.Pending, cancellationToken);
            if (pending == 0 && files > 0)
            {
                LastError = $"Còn {files} file bằng chứng chưa tải lên máy chủ. Hãy đồng bộ xong trước khi đăng xuất.";
                return;
            }

            if (pending > 0)
            {
                LastError = $"Còn {pending} thay đổi chưa gửi hoặc chưa xử lý. Hãy đồng bộ và xử lý xong trước khi đăng xuất.";
                return;
            }
        }

        await _session.SignOutAsync(cancellationToken);
        _user = null;
        SelectedEngagement = null;
        Engagements.Clear();
        SignedOut?.Invoke(this, EventArgs.Empty);
    }

    private void Show(SyncStatus status)
    {
        StatusText = status.Connection switch
        {
            ConnectionState.Online => "Trực tuyến · thay đổi được đồng bộ ngay",
            ConnectionState.Connecting => "Đang kết nối…",
            _ => "Ngoại tuyến · thay đổi được giữ trên máy và gửi khi có kết nối",
        };
        OutboxText = string.Join(" · ", new[]
        {
            status.Pending > 0 ? $"{status.Pending} thay đổi chờ gửi" : "Không có thay đổi chờ gửi",
            status.PendingFiles > 0 ? $"{status.PendingFiles} file chờ tải lên" : null,
            status.NeedsAttention > 0 ? $"{status.NeedsAttention} cần xử lý" : null,
            status.LastSyncedAt is { } at ? $"đồng bộ lần cuối {at.ToLocalTime():HH:mm dd/MM}" : null,
        }.OfType<string>());

        // Being offline is a normal way to work, not an error to show.
        LastError = status.Connection == ConnectionState.Online ? status.Error : null;

        // A stopped engagement is not retried on its own: the user chooses to rebuild it.
        NeedsRebuild = _loop.Stopped.Count > 0;
        if (NeedsRebuild)
        {
            Notice = null;
        }
    }
}
