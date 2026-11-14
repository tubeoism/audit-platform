using System.Collections.ObjectModel;
using AuditPlatform.Client.Core.Auth;
using AuditPlatform.Client.Core.Directory;
using AuditPlatform.Client.Core.Evidence;
using AuditPlatform.Client.Core.LocalStore;
using AuditPlatform.Client.Core.Sync;
using AuditPlatform.Domain.Engagements;
using AuditPlatform.Domain.Templates;
using AuditPlatform.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Client.Core.ViewModels;

public sealed record PaperItem(Guid Id, string Code, string Title, string StatusText, string PreparerName, string Flags)
{
    public string Heading => $"{Code} · {Title}";
}

public sealed record AreaItem(string Code, string Name)
{
    public override string ToString() => $"{Code} · {Name}";
}

public sealed record TemplateItem(ProcedureTemplate Template)
{
    public override string ToString() => $"{Template.Code} · {Template.Title}";
}

public sealed record PhaseItem(EngagementStatus Target)
{
    public string Label => $"Chuyển sang {Target}";
}

/// <summary>One engagement: its working papers, starting a new one from the methodology library, and (for its manager) its phases and lock.</summary>
public sealed partial class EngagementViewModel : ObservableObject
{
    private readonly LocalUnitFactory _units;
    private readonly SyncLoop _loop;
    private readonly ClientOptions _options;
    private readonly CurrentUser _user;
    private readonly LocalEvidence _evidence;
    private readonly AuthSession _session;
    private List<ProcedureTemplate> _templates = [];

    public EngagementViewModel(
        LocalUnitFactory units, SyncLoop loop, ClientOptions options, CurrentUser user, Guid engagementId, LocalEvidence evidence, AuthSession session)
    {
        _evidence = evidence;
        _session = session;
        _units = units;
        _loop = loop;
        _options = options;
        _user = user;
        EngagementId = engagementId;
    }

    public Guid EngagementId { get; }

    public ObservableCollection<PaperItem> Papers { get; } = [];
    public ObservableCollection<AreaItem> Areas { get; } = [];
    public ObservableCollection<TemplateItem> AreaTemplates { get; } = [];
    public ObservableCollection<PhaseItem> Phases { get; } = [];
    public ObservableCollection<string> LockBlockers { get; } = [];

    [ObservableProperty]
    private string _heading = "";

    [ObservableProperty]
    private string _detail = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreatePaperCommand))]
    private bool _canCreate;

    [ObservableProperty]
    private AreaItem? _selectedArea;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreatePaperCommand))]
    private TemplateItem? _selectedTemplate;

    [ObservableProperty]
    private PaperItem? _selectedPaper;

    [ObservableProperty]
    private WorkingPaperViewModel? _paper;

    [ObservableProperty]
    private string? _error;

    /// <summary>The engagement's manager sees the phase buttons; nobody else does.</summary>
    [ObservableProperty]
    private bool _showLifecycle;

    /// <summary>The lock panel is offered in Reporting only.</summary>
    [ObservableProperty]
    private bool _showLock;

    [ObservableProperty]
    private string _lockSummary = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LockCommand))]
    private bool _lockReady;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LockCommand))]
    private string _lockCode = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LockCommand))]
    private bool _lockUnderstood;

    [ObservableProperty]
    private string? _lifecycleMessage;

    /// <summary>Why this laptop stopped following the master for this engagement; null when it follows normally.</summary>
    [ObservableProperty]
    private string? _replicaProblem;

    [ObservableProperty]
    private string? _rebuildMessage;

    private string? _lockDigest;
    private EngagementStatus _status;
    private bool _isManager;

    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        await using var unit = await _units.OpenAsync(cancellationToken);
        var engagement = await unit.Db.Engagements.AsNoTracking().FirstOrDefaultAsync(e => e.Id == EngagementId, cancellationToken);
        if (engagement is null)
        {
            Heading = "Engagement không còn trong danh sách của bạn";
            CanCreate = false;
            return;
        }

        var members = await unit.Db.Members.AsNoTracking().Where(m => m.EngagementId == EngagementId).ToListAsync(cancellationToken);
        var names = members.ToDictionary(m => m.UserId, m => m.DisplayName);
        var roles = members.FirstOrDefault(m => m.UserId == _user.Id)?.RoleSet() ?? new HashSet<EngagementRole>();
        Heading = $"{engagement.Code} · {engagement.ClientName}";
        Detail = $"{engagement.Status}{(engagement.IsReadOnly ? " · đã khoá, chỉ xem" : "")} · vai trò của bạn: {(roles.Count == 0 ? "chưa rõ" : string.Join(", ", roles.Order()))}";
        CanCreate = !engagement.IsReadOnly && FieldPolicy.CanEdit(roles);
        ReplicaProblem = _loop.Stopped.GetValueOrDefault(EngagementId);
        _status = engagement.Status;
        _isManager = !engagement.IsReadOnly && roles.Contains(EngagementRole.EngagementManager);
        ShowLifecycle = _isManager;
        ShowLock = _isManager && _status == EngagementStatus.Reporting;
        var phases = _isManager
            ? new[] { EngagementStatus.Fieldwork, EngagementStatus.Review, EngagementStatus.Reporting }
                .Where(target => Engagement.CanMove(_status, target)).Select(target => new PhaseItem(target)).ToList()
            : [];
        if (!Phases.SequenceEqual(phases))
        {
            Phases.Clear();
            phases.ForEach(Phases.Add);
        }

        if (!ShowLock)
        {
            LockBlockers.Clear();
            LockReady = false;
            LockSummary = "";
            LockCode = "";
            LockUnderstood = false;
            _lockDigest = null;
        }

        _templates = await DirectoryStore.LatestTemplatesAsync(unit.Db, _options.TemplateLanguage, cancellationToken);
        var titles = _templates.ToDictionary(t => t.Code, t => t.Title);
        var areas = _templates.GroupBy(t => t.AreaCode).Select(g => new AreaItem(g.Key, g.First().AreaName)).ToList();
        if (!Areas.SequenceEqual(areas))
        {
            var keep = SelectedArea?.Code;
            Areas.Clear();
            areas.ForEach(Areas.Add);
            SelectedArea = Areas.FirstOrDefault(a => a.Code == keep) ?? Areas.FirstOrDefault();
        }

        var papers = (await unit.Workspace(EngagementId, _user.Id).ListPapersAsync(cancellationToken)).Select(v => new PaperItem(
            v.Confirmed.Id, v.TemplateCode, titles.GetValueOrDefault(v.TemplateCode, ""), Texts.Status(v.Status),
            names.GetValueOrDefault(v.PreparerId, "?"),
            string.Join(" · ", new[]
            {
                v.IsLocalOnly ? "chưa gửi" : v.PendingCount > 0 ? $"{v.PendingCount} thay đổi chưa gửi" : null,
                v.Confirmed.OpenConflicts.Count() is > 0 and var n ? $"{n} xung đột" : null,
                v.Attention.Any(a => a.State == OutboxState.Rejected) ? "bị từ chối" : null,
            }.OfType<string>()))).ToList();
        if (!Papers.SequenceEqual(papers))
        {
            // The list can be empty for a moment (a rebuild from the master); the paper the user had open comes back with it.
            var keep = SelectedPaper?.Id ?? _wantedPaper;
            Papers.Clear();
            papers.ForEach(Papers.Add);
            _keepPaper = true;
            SelectedPaper = Papers.FirstOrDefault(p => p.Id == keep);
            _keepPaper = false;
        }

        if (Paper is not null)
        {
            await Paper.ReloadAsync(cancellationToken);
        }
    }

    private bool _keepPaper;

    partial void OnSelectedAreaChanged(AreaItem? value)
    {
        AreaTemplates.Clear();
        foreach (var template in _templates.Where(t => t.AreaCode == value?.Code))
        {
            AreaTemplates.Add(new TemplateItem(template));
        }

        SelectedTemplate = AreaTemplates.FirstOrDefault();
    }

    private Guid? _wantedPaper;

    partial void OnSelectedPaperChanged(PaperItem? value)
    {
        if (value is not null)
        {
            _wantedPaper = value.Id;
        }

        if (_keepPaper && value?.Id == Paper?.DocumentId)
        {
            return;
        }

        Paper = value is null ? null : new WorkingPaperViewModel(_units, _loop, _options, _user, EngagementId, value.Id, _evidence, _session);
        _ = Paper?.ReloadAsync(CancellationToken.None);
    }

    private bool CanCreatePaper() => CanCreate && SelectedTemplate is not null;

    /// <summary>Starts a working paper from the selected procedure, pinned to the template version on this laptop.</summary>
    [RelayCommand(CanExecute = nameof(CanCreatePaper))]
    private async Task CreatePaperAsync(CancellationToken cancellationToken)
    {
        Error = null;
        if (SelectedTemplate?.Template is not { } template)
        {
            return;
        }

        if (Papers.Any(p => p.Code == template.Code))
        {
            Error = $"Engagement này đã có giấy làm việc cho {template.Code}.";
            return;
        }

        var id = Guid.NewGuid();
        try
        {
            await using (var unit = await _units.OpenAsync(cancellationToken))
            {
                await unit.Workspace(EngagementId, _user.Id).CreatePaperAsync(id, template.Code, template.Version, cancellationToken);
            }
        }
        catch (InvalidOperationException ex)
        {
            Error = ex.Message;
            return;
        }

        _loop.Poke();
        await ReloadAsync(cancellationToken);
        SelectedPaper = Papers.FirstOrDefault(p => p.Id == id);
    }

    /// <summary>Moves the engagement to another phase. Online only; the master decides who may.</summary>
    [RelayCommand]
    private async Task AdvanceAsync(PhaseItem? phase, CancellationToken cancellationToken)
    {
        LifecycleMessage = null;
        if (phase is null)
        {
            return;
        }

        var result = await _session.AdvanceEngagementAsync(EngagementId, phase.Target.ToString(), cancellationToken);
        if (result.Value is null)
        {
            LifecycleMessage = result.Error?.Message ?? "Không chuyển được giai đoạn.";
            return;
        }

        await using (var unit = await _units.OpenAsync(cancellationToken))
        {
            var row = await unit.Db.Engagements.FirstOrDefaultAsync(e => e.Id == EngagementId, cancellationToken);
            if (row is not null && Enum.TryParse<EngagementStatus>(result.Value.Status, out var status))
            {
                row.Status = status;
                await unit.Db.SaveChangesAsync(cancellationToken);
            }
        }

        LifecycleMessage = $"Đã chuyển sang {result.Value.Status}.";
        await ReloadAsync(cancellationToken);
        if (ShowLock)
        {
            await RefreshLockAsync(cancellationToken);
        }
    }

    /// <summary>Asks the master what stands between the engagement and its lock.</summary>
    [RelayCommand]
    private async Task RefreshLockAsync(CancellationToken cancellationToken)
    {
        LifecycleMessage = null;
        var readiness = await _session.GetLockReadinessAsync(EngagementId, cancellationToken);
        LockBlockers.Clear();
        if (readiness is null)
        {
            _lockDigest = null;
            LockReady = false;
            LockSummary = "Không kết nối được máy chủ; việc khoá cần trực tuyến.";
            return;
        }

        _lockDigest = readiness.Digest;
        foreach (var blocker in readiness.Blockers)
        {
            LockBlockers.Add(blocker.Reason);
        }

        LockReady = readiness.Ready;
        LockSummary = readiness.Ready
            ? $"Đủ điều kiện: {readiness.PaperCount} giấy làm việc đã qua cả hai cấp soát xét, không còn ghi chú hay xung đột mở."
            : $"Chưa đủ điều kiện khoá ({readiness.Blockers.Count} vướng mắc).";
    }

    private bool CanLock() => LockReady && LockUnderstood && LockCode.Length == 6 && LockCode.All(char.IsAsciiDigit);

    /// <summary>
    /// Locks the engagement for good. A fresh authenticator code is bought for the exact state shown; if anyone changed
    /// anything since, the master refuses and the list above is refreshed.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLock))]
    private async Task LockAsync(CancellationToken cancellationToken)
    {
        LifecycleMessage = null;
        if (_lockDigest is null)
        {
            return;
        }

        var result = await _session.LockEngagementAsync(EngagementId, _lockDigest, LockCode, cancellationToken);
        LockCode = "";
        if (result.Value is null)
        {
            var why = result.Error?.Message ?? "Không khoá được engagement.";
            await RefreshLockAsync(cancellationToken);
            LifecycleMessage = why;
            return;
        }

        await using (var unit = await _units.OpenAsync(cancellationToken))
        {
            await unit.Agent.MarkLockedAsync(EngagementId, cancellationToken);
        }

        LockUnderstood = false;
        _loop.Poke();
        LifecycleMessage = "Engagement đã khoá. Từ giờ chỉ xem.";
        await ReloadAsync(cancellationToken);
    }

    /// <summary>
    /// Rebuilds this laptop's copy of the engagement from the master. Unsent edits stay; the whole local database is
    /// copied aside first.
    /// </summary>
    [RelayCommand]
    private async Task RebuildAsync(CancellationToken cancellationToken)
    {
        RebuildMessage = null;
        try
        {
            var copy = await _loop.RebuildReplicaAsync(EngagementId, cancellationToken);
            RebuildMessage = $"Đã dựng lại từ máy chủ. Bản sao dữ liệu cũ của máy này: {copy}";
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            RebuildMessage = $"Không dựng lại được: {ex.Message}";
        }

        await ReloadAsync(cancellationToken);
    }
}
