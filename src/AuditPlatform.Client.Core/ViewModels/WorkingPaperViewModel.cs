using System.Collections.ObjectModel;
using AuditPlatform.Client.Core.Directory;
using AuditPlatform.Client.Core.LocalStore;
using AuditPlatform.Client.Core.Sync;
using AuditPlatform.Client.Core.WorkingPapers;
using AuditPlatform.Domain.Engagements;
using AuditPlatform.Domain.WorkingPapers;
using AuditPlatform.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Client.Core.ViewModels;

/// <summary>One box on the form. Remembers which version of the field was on screen when the user started typing.</summary>
public sealed partial class FieldViewModel(FieldSpec spec) : ObservableObject
{
    private string _loaded = "";

    public string Key { get; } = spec.Key;
    public string Label { get; } = spec.Label;
    public FieldSection Section { get; } = spec.Section;

    [ObservableProperty]
    private string _text = "";

    [ObservableProperty]
    private string _stateText = "";

    [ObservableProperty]
    private bool _needsAttention;

    /// <summary>The change that produced the value the user is looking at; the save is judged against it.</summary>
    public Guid? SeenChangeId { get; private set; }

    public bool IsDirty => !string.Equals(Text, _loaded, StringComparison.Ordinal);

    /// <summary>Takes the replica's value unless the user is in the middle of an edit, which is never overwritten.</summary>
    internal void Show(string value, Guid? confirmedChangeId, bool pending, bool flagged, string? author)
    {
        var typing = IsDirty;
        if (!typing)
        {
            _loaded = value;
            Text = value;
            SeenChangeId = confirmedChangeId;
        }

        var moved = typing && confirmedChangeId != SeenChangeId;
        NeedsAttention = flagged || moved;
        StateText = moved ? "Người khác vừa đổi ô này; khi lưu, hai giá trị sẽ được giữ lại để xử lý"
            : flagged ? "Có xung đột chờ xử lý"
            : typing ? "Đang sửa"
            : pending ? "Chưa gửi lên máy chủ"
            : author is null ? "" : $"Đã đồng bộ · {author}";
    }

    internal void MarkSaved() => _loaded = Text;
}

public sealed record ConflictViewModel(
    Guid Id, string FieldLabel, string AuthorName, string ProposedValue, string CurrentValue, bool CanResolve);

public sealed record AttentionViewModel(Guid ChangeId, string Text);

/// <summary>
/// The form of one working paper: the procedure, one box per field, flagged conflicts with both values, and what
/// the master refused. Every save goes to the outbox first; the form works the same with or without a connection.
/// </summary>
public sealed partial class WorkingPaperViewModel : ObservableObject
{
    private readonly LocalUnitFactory _units;
    private readonly SyncLoop _loop;
    private readonly ClientOptions _options;
    private readonly CurrentUser _user;
    private readonly Guid _engagementId;

    public WorkingPaperViewModel(LocalUnitFactory units, SyncLoop loop, ClientOptions options, CurrentUser user, Guid engagementId, Guid documentId)
    {
        _units = units;
        _loop = loop;
        _options = options;
        _user = user;
        _engagementId = engagementId;
        DocumentId = documentId;
    }

    public Guid DocumentId { get; }

    public ObservableCollection<FieldViewModel> Fields { get; } = [];
    public ObservableCollection<string> EvidenceToCheck { get; } = [];
    public ObservableCollection<ConflictViewModel> Conflicts { get; } = [];
    public ObservableCollection<AttentionViewModel> Attention { get; } = [];
    public ObservableCollection<string> SignOffs { get; } = [];

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _procedure = "";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _preparerName = "";

    [ObservableProperty]
    private string? _notice;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private bool _canSubmit;

    [ObservableProperty]
    private bool _canEdit;

    [ObservableProperty]
    private bool _hasConflicts;

    [ObservableProperty]
    private bool _hasAttention;

    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        await using var unit = await _units.OpenAsync(cancellationToken);
        var view = await unit.Workspace(_engagementId, _user.Id).ViewPaperAsync(DocumentId, cancellationToken);
        var engagement = await unit.Db.Engagements.AsNoTracking().FirstOrDefaultAsync(e => e.Id == _engagementId, cancellationToken);
        var members = await unit.Db.Members.AsNoTracking().Where(m => m.EngagementId == _engagementId).ToListAsync(cancellationToken);
        var names = members.ToDictionary(m => m.UserId, m => m.DisplayName);
        var roles = members.FirstOrDefault(m => m.UserId == _user.Id)?.RoleSet() ?? new HashSet<EngagementRole>();
        var template = await DirectoryStore.FindTemplateAsync(unit.Db, view.TemplateCode, view.TemplateVersion, _options.TemplateLanguage, cancellationToken);
        var doc = view.Confirmed;
        string Name(Guid id) => names.GetValueOrDefault(id, "Người không còn trong engagement");

        var writable = engagement is { IsReadOnly: false };
        CanEdit = writable && FieldPolicy.CanEdit(roles);
        CanSubmit = CanEdit && view.Status == WorkingPaperStatus.Draft;
        Title = template is null ? view.TemplateCode : $"{template.Code} · {template.Title}";
        Procedure = template?.Procedure ?? "";
        StatusText = Texts.Status(view.Status);
        PreparerName = Name(view.PreparerId);
        Notice = engagement is { IsReadOnly: true } ? "Engagement đã khoá: chỉ xem."
            : template is null ? $"Máy này chưa có mẫu thủ tục {view.TemplateCode} phiên bản {view.TemplateVersion}; hãy đồng bộ khi có mạng."
            : view.IsLocalOnly ? "Giấy làm việc này mới chỉ có trên máy bạn; sẽ gửi lên khi có kết nối."
            : !CanEdit ? "Bạn có vai trò soát xét trong engagement này: xem, không sửa nội dung."
            : null;

        Replace(EvidenceToCheck, template?.EvidenceToCheck ?? []);

        // Fields of the layout first, then anything saved under a key this layout does not know: content is never hidden.
        var specs = WorkingPaperLayout.For(template, _options.TemplateLanguage).ToList();
        var known = specs.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        specs.AddRange(doc.Fields.Keys.Concat(view.PendingValues.Keys).Distinct().Where(k => !known.Contains(k)).Order(StringComparer.Ordinal)
            .Select(WorkingPaperLayout.Unknown));
        if (!Fields.Select(f => f.Key).SequenceEqual(specs.Select(s => s.Key)))
        {
            var kept = Fields.ToDictionary(f => f.Key);
            Fields.Clear();
            foreach (var spec in specs)
            {
                Fields.Add(kept.GetValueOrDefault(spec.Key) ?? new FieldViewModel(spec));
            }
        }

        var open = doc.OpenConflicts.ToList();
        foreach (var field in Fields)
        {
            doc.Fields.TryGetValue(field.Key, out var cell);
            field.Show(view.Value(field.Key), cell?.ChangeId, view.PendingValues.ContainsKey(field.Key),
                open.Any(c => c.Key == field.Key), cell is null ? null : Name(cell.Author));
        }

        var labels = specs.ToDictionary(s => s.Key, s => s.Label);
        var canResolve = writable && FieldPolicy.CanResolve(roles, doc, _user.Id);
        Replace(Conflicts, open.Select(c => new ConflictViewModel(
            c.Id, labels.GetValueOrDefault(c.Key, c.Key), Name(c.Author), c.ProposedValue, doc.Value(c.Key) ?? "", canResolve)));
        HasConflicts = Conflicts.Count > 0;

        Replace(Attention, view.Attention.Where(a => a.State == OutboxState.Rejected).Select(a => new AttentionViewModel(a.ChangeId,
            $"Máy chủ từ chối thay đổi của bạn{(a.Op is SetField set ? $" ở ô “{labels.GetValueOrDefault(set.Key, set.Key)}”: “{set.Value}”" : $" ({a.Op.Name})")}. Lý do: {a.Reason}")));
        HasAttention = Attention.Count > 0;

        Replace(SignOffs, doc.SignOffs.Select(s =>
            $"{(s.Level == EngagementRole.ManagerQa ? "Cấp 1 (Manager QA)" : "Cấp 2 (Japan Desk)")} · {Name(s.SignedBy)}"
            + (s.IsBroken ? " · ĐÃ MẤT HIỆU LỰC do nội dung thay đổi sau khi ký" : " · còn hiệu lực")));
    }

    /// <summary>Saves one box if its text changed. Called when the box loses focus.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SaveFieldAsync(FieldViewModel? field, CancellationToken cancellationToken)
    {
        if (field is null || !field.IsDirty || !CanEdit)
        {
            return;
        }

        await RunAsync(async unit =>
        {
            await unit.Workspace(_engagementId, _user.Id).SetFieldAsync(DocumentId, field.Key, field.Text, field.SeenChangeId, cancellationToken);
            field.MarkSaved();
        }, cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private Task SubmitAsync(CancellationToken cancellationToken) =>
        RunAsync(unit => unit.Workspace(_engagementId, _user.Id).SubmitForReviewAsync(DocumentId, cancellationToken), cancellationToken);

    /// <summary>Decides a conflict by keeping the value the master holds.</summary>
    [RelayCommand]
    private Task KeepCurrentAsync(ConflictViewModel? conflict, CancellationToken cancellationToken) =>
        ResolveAsync(conflict, conflict?.CurrentValue, cancellationToken);

    /// <summary>Decides a conflict by taking the value that was proposed.</summary>
    [RelayCommand]
    private Task UseProposedAsync(ConflictViewModel? conflict, CancellationToken cancellationToken) =>
        ResolveAsync(conflict, conflict?.ProposedValue, cancellationToken);

    [RelayCommand]
    private Task DismissAsync(AttentionViewModel? item, CancellationToken cancellationToken) =>
        item is null ? Task.CompletedTask
            : RunAsync(unit => unit.Workspace(_engagementId, _user.Id).DismissRejectedAsync(item.ChangeId, cancellationToken), cancellationToken);

    private Task ResolveAsync(ConflictViewModel? conflict, string? value, CancellationToken cancellationToken) =>
        conflict is not { CanResolve: true } || value is null ? Task.CompletedTask
            : RunAsync(unit => unit.Workspace(_engagementId, _user.Id).ResolveConflictAsync(DocumentId, conflict.Id, value, cancellationToken), cancellationToken);

    private async Task RunAsync(Func<LocalUnit, Task> action, CancellationToken cancellationToken)
    {
        Error = null;
        try
        {
            await using (var unit = await _units.OpenAsync(cancellationToken))
            {
                await action(unit);
            }

            _loop.Poke();
            await ReloadAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException)
        {
            Error = ex.Message;
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        var next = items.ToList();
        if (target.SequenceEqual(next))
        {
            return;
        }

        target.Clear();
        foreach (var item in next)
        {
            target.Add(item);
        }
    }
}
