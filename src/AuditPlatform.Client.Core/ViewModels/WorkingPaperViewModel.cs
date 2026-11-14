using System.Collections.ObjectModel;
using AuditPlatform.Client.Core.Auth;
using AuditPlatform.Client.Core.Directory;
using AuditPlatform.Client.Core.Evidence;
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
/// One review note with the replies to it. Kept as one instance per note, so that a reply being typed is not lost when
/// the form reloads after a sync.
/// </summary>
public sealed partial class NoteViewModel(Guid id, string fieldKey) : ObservableObject
{
    public Guid Id { get; } = id;
    public string FieldKey { get; } = fieldKey;

    [ObservableProperty]
    private string _fieldLabel = "";

    [ObservableProperty]
    private string _text = "";

    [ObservableProperty]
    private string _raisedBy = "";

    [ObservableProperty]
    private IReadOnlyList<string> _responses = [];

    [ObservableProperty]
    private string _stateText = "";

    [ObservableProperty]
    private bool _isOpen;

    /// <summary>The preparer side: answer a note that is still open.</summary>
    [ObservableProperty]
    private bool _canRespond;

    /// <summary>The reviewer side: accept the answer and close the note. Only the reviewer who raised it can.</summary>
    [ObservableProperty]
    private bool _canClear;

    [ObservableProperty]
    private string _responseText = "";
}

/// <summary>One evidence file linked to the paper. <see cref="Detail"/> says where the file is: here, on the way, or at the master only.</summary>
public sealed record EvidenceItemViewModel(string Hash, string FileName, string Detail, bool NeedsAttention)
{
    /// <summary>Enough of the hash to compare by eye with the intake record.</summary>
    public string ShortHash => Hash.Length > 12 ? Hash[..12] : Hash;
}

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
    private readonly LocalEvidence _evidence;
    private readonly AuthSession _session;
    private EngagementRole? _signOffLevel;

    public WorkingPaperViewModel(
        LocalUnitFactory units, SyncLoop loop, ClientOptions options, CurrentUser user, Guid engagementId, Guid documentId, LocalEvidence evidence,
        AuthSession session)
    {
        _evidence = evidence;
        _session = session;
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
    public ObservableCollection<EvidenceItemViewModel> Evidence { get; } = [];
    public ObservableCollection<ConflictViewModel> Conflicts { get; } = [];
    public ObservableCollection<AttentionViewModel> Attention { get; } = [];
    public ObservableCollection<string> SignOffs { get; } = [];
    public ObservableCollection<NoteViewModel> Notes { get; } = [];

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

    // ----- Review notes: raised by a reviewer, answered by the preparer, closed by the reviewer who raised them.

    [ObservableProperty]
    private bool _canRaiseNote;

    [ObservableProperty]
    private FieldViewModel? _newNoteField;

    [ObservableProperty]
    private string _newNoteText = "";

    [ObservableProperty]
    private string _notesSummary = "";

    // ----- Sign-off: an online step for a reviewer, with a fresh authenticator code.

    /// <summary>True when this user holds the review level that is next in line for this paper.</summary>
    [ObservableProperty]
    private bool _canSignOff;

    [ObservableProperty]
    private string _signOffTitle = "";

    /// <summary>Why the paper cannot be signed off yet, in words; null when it can.</summary>
    [ObservableProperty]
    private string? _signOffBlocker;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignOffCommand))]
    private bool _signOffReady;

    [ObservableProperty]
    private string _signOffCode = "";

    [ObservableProperty]
    private string? _signOffMessage;

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

        var files = (await unit.Db.Evidence.AsNoTracking().Where(e => e.EngagementId == _engagementId).ToListAsync(cancellationToken))
            .ToDictionary(e => e.Hash);
        Replace(Evidence, view.Evidence.Select(hash =>
        {
            files.TryGetValue(hash, out var file);
            var where = file switch
            {
                null => "chưa rõ tên file; đồng bộ khi có mạng",
                { Upload: EvidenceUpload.Refused } => $"máy chủ không nhận: {file.LastError}",
                { Upload: EvidenceUpload.Pending } => "chờ tải lên máy chủ",
                _ when view.PendingEvidence.Contains(hash) => "đã ở máy chủ · chờ gửi liên kết",
                _ when _evidence.IsHere(hash) => "đã ở máy chủ · có bản trên máy này",
                _ => "đã ở máy chủ · tải về khi mở",
            };
            return new EvidenceItemViewModel(hash, file?.FileName ?? hash[..Math.Min(16, hash.Length)],
                file is null ? where : $"{LocalEvidence.Size(file.SizeBytes)} · {Name(file.AddedBy)} · {where}",
                file is null or { Upload: EvidenceUpload.Refused });
        }).OrderBy(e => e.FileName, StringComparer.CurrentCultureIgnoreCase).ThenBy(e => e.Hash, StringComparer.Ordinal));

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
            $"Máy chủ từ chối thay đổi của bạn{(a.Op is SetField set ? $" ở ô “{labels.GetValueOrDefault(set.Key, set.Key)}”: “{set.Value}”" : a.Op is LinkEvidence link ? $": đính kèm file “{files.GetValueOrDefault(link.ContentHash)?.FileName ?? link.ContentHash[..Math.Min(16, link.ContentHash.Length)]}”"
                : $" ({a.Op.Name})")}. Lý do: {a.Reason}")));
        HasAttention = Attention.Count > 0;

        ShowNotes(view, doc, specs, roles, writable, Name);
        ShowSignOffState(view, doc, roles, writable);

        Replace(SignOffs, doc.SignOffs.Select(s =>
            $"{(s.Level == EngagementRole.ManagerQa ? "Cấp 1 (Manager QA)" : "Cấp 2 (Japan Desk)")} · {Name(s.SignedBy)}"
            + (s.IsBroken ? " · ĐÃ MẤT HIỆU LỰC do nội dung thay đổi sau khi ký" : " · còn hiệu lực")));
    }

    private void ShowNotes(
        PaperView view, FieldDocument doc, List<FieldSpec> specs, IReadOnlySet<EngagementRole> roles, bool writable, Func<Guid, string> name)
    {
        var labels = specs.ToDictionary(s => s.Key, s => s.Label);
        var order = specs.Select((s, i) => (s.Key, i)).ToDictionary(x => x.Key, x => x.i);
        var canEdit = writable && FieldPolicy.CanEdit(roles);
        var canReview = writable && FieldPolicy.CanReview(roles);
        var responsesOf = view.PendingResponses.ToLookup(r => r.NoteId);

        var rows = new List<(Guid Id, string Key, string Text, Guid By, List<string> Replies, bool Cleared, bool Unsent, bool Confirmed, bool ClearUnsent)>();
        foreach (var note in doc.Notes.Values)
        {
            var replies = note.Responses.Select(r => $"{name(r.Author)}: {r.Text}").ToList();
            replies.AddRange(responsesOf[note.Id].Select(r => $"{name(r.Author)}: {r.Text} (chưa gửi lên máy chủ)"));
            rows.Add((note.Id, note.FieldKey, note.Text, note.RaisedBy, replies, note.IsCleared, false, note.Responses.Count > 0, view.PendingClears.Contains(note.Id)));
        }

        foreach (var note in view.PendingNotes.Where(n => !doc.Notes.ContainsKey(n.NoteId)))
        {
            var replies = responsesOf[note.NoteId].Select(r => $"{name(r.Author)}: {r.Text} (chưa gửi lên máy chủ)").ToList();
            rows.Add((note.NoteId, note.FieldKey, note.Text, note.Author, replies, false, true, false, false));
        }

        // Open notes first, then by the order of the boxes on the form.
        var ordered = rows.OrderBy(r => r.Cleared).ThenBy(r => order.GetValueOrDefault(r.Key, int.MaxValue)).ThenBy(r => r.Id).ToList();
        var next = new List<NoteViewModel>();
        foreach (var row in ordered)
        {
            var note = Notes.FirstOrDefault(n => n.Id == row.Id) ?? new NoteViewModel(row.Id, row.Key);
            note.FieldLabel = labels.GetValueOrDefault(row.Key, row.Key);
            note.Text = row.Text;
            note.RaisedBy = $"Ý kiến của {name(row.By)}";
            note.Responses = row.Replies;
            note.IsOpen = !row.Cleared;
            note.StateText = row.Cleared ? "Đã xử lý xong"
                : row.Unsent ? "Chưa gửi lên máy chủ"
                : row.ClearUnsent ? "Xác nhận xử lý xong đang chờ gửi lên máy chủ"
                : row.Replies.Count == 0 ? "Chờ người lập trả lời"
                : "Đã có trả lời; chờ người nêu ý kiến xác nhận";
            note.CanRespond = canEdit && !row.Cleared && !row.Unsent;
            note.CanClear = canReview && !row.Cleared && !row.ClearUnsent && row.By == _user.Id && row.Confirmed;
            next.Add(note);
        }

        if (!Notes.SequenceEqual(next))
        {
            Notes.Clear();
            foreach (var note in next)
            {
                Notes.Add(note);
            }
        }

        var open = ordered.Count(r => !r.Cleared);
        NotesSummary = ordered.Count == 0 ? "" : open == 0 ? $"Tất cả {ordered.Count} ý kiến đã xử lý xong." : $"Còn {open} ý kiến chưa xử lý xong trên {ordered.Count}.";
        CanRaiseNote = canReview && view.Exists && !view.IsLocalOnly;
        if (NewNoteField is null || !Fields.Contains(NewNoteField))
        {
            NewNoteField = Fields.FirstOrDefault();
        }
    }

    /// <summary>
    /// The next review level this user can sign, and what stands in the way. The same rule the master applies, so the
    /// button is offered only when the master is likely to accept it; the master still decides.
    /// </summary>
    private void ShowSignOffState(PaperView view, FieldDocument doc, IReadOnlySet<EngagementRole> roles, bool writable)
    {
        _signOffLevel = view.Status switch
        {
            WorkingPaperStatus.ReadyForReview when roles.Contains(EngagementRole.ManagerQa) => EngagementRole.ManagerQa,
            WorkingPaperStatus.ManagerQaSignedOff when roles.Contains(EngagementRole.JapanDesk) => EngagementRole.JapanDesk,
            _ => null,
        };
        CanSignOff = writable && _signOffLevel is not null && view.Exists && !view.IsLocalOnly;
        SignOffTitle = _signOffLevel switch
        {
            EngagementRole.ManagerQa => "Ký soát xét cấp 1 (Manager QA)",
            EngagementRole.JapanDesk => "Ký soát xét cấp 2 (Japan Desk)",
            _ => "",
        };
        var openNotes = doc.Notes.Values.Count(n => !n.IsCleared);
        SignOffBlocker = !CanSignOff ? null
            : doc.PreparerId == _user.Id ? "Người lập không được ký soát xét giấy làm việc của chính mình."
            : openNotes > 0 ? $"Còn {openNotes} ý kiến chưa xử lý xong; cần xử lý hết trước khi ký."
            : doc.OpenConflicts.Any() ? "Còn xung đột chưa được quyết định; cần xử lý hết trước khi ký."
            : view.PendingCount > 0 ? "Máy này còn thay đổi chưa gửi lên máy chủ; hãy đồng bộ xong rồi mới ký, để chữ ký đóng dấu đúng nội dung bạn đang xem."
            : doc.WhySignOffIsRefused(_signOffLevel!.Value.ToString(), _user.Id);
        SignOffReady = CanSignOff && SignOffBlocker is null;
    }

    /// <summary>Raises a note on the selected box. It is queued like any other change and works offline.</summary>
    [RelayCommand]
    private async Task RaiseNoteAsync(CancellationToken cancellationToken)
    {
        if (!CanRaiseNote)
        {
            return;
        }

        if (NewNoteField is null || string.IsNullOrWhiteSpace(NewNoteText))
        {
            Error = "Chọn ô cần nêu ý kiến và nhập nội dung.";
            return;
        }

        var key = NewNoteField.Key;
        var text = NewNoteText.Trim();
        await RunAsync(unit => unit.Workspace(_engagementId, _user.Id).RaiseNoteAsync(DocumentId, Guid.NewGuid(), key, text, cancellationToken), cancellationToken);
        if (Error is null)
        {
            NewNoteText = "";
        }
    }

    /// <summary>The preparer answers a note. Answers are added, never replaced; the reviewer decides when the note is done.</summary>
    [RelayCommand]
    private async Task RespondAsync(NoteViewModel? note, CancellationToken cancellationToken)
    {
        if (note is not { CanRespond: true })
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(note.ResponseText))
        {
            Error = "Nhập nội dung trả lời.";
            return;
        }

        var text = note.ResponseText.Trim();
        await RunAsync(unit => unit.Workspace(_engagementId, _user.Id).RespondAsync(DocumentId, note.Id, text, cancellationToken), cancellationToken);
        if (Error is null)
        {
            note.ResponseText = "";
        }
    }

    /// <summary>The reviewer who raised a note closes it, on the latest answer they have read.</summary>
    [RelayCommand]
    private Task ClearNoteAsync(NoteViewModel? note, CancellationToken cancellationToken) =>
        note is not { CanClear: true } ? Task.CompletedTask
            : RunAsync(unit => unit.Workspace(_engagementId, _user.Id).ClearNoteAsync(DocumentId, note.Id, cancellationToken), cancellationToken);

    /// <summary>
    /// Signs the paper off at the next review level. Needs the master and a fresh authenticator code: the code buys a
    /// single-use grant for exactly the content shown on this laptop, so what is sealed is what the reviewer saw.
    /// </summary>
    [RelayCommand(CanExecute = nameof(SignOffReady))]
    private async Task SignOffAsync(CancellationToken cancellationToken)
    {
        Error = null;
        SignOffMessage = null;
        if (_signOffLevel is not { } level || !SignOffReady)
        {
            return;
        }

        var code = new string(SignOffCode.Where(char.IsAsciiDigit).ToArray());
        if (code.Length != 6)
        {
            Error = "Nhập mã 6 chữ số hiện trên ứng dụng xác thực.";
            return;
        }

        string hash;
        await using (var unit = await _units.OpenAsync(cancellationToken))
        {
            hash = (await unit.Workspace(_engagementId, _user.Id).ConfirmedAsync(DocumentId, cancellationToken)).ComputeContentHash();
        }

        var result = await _session.SignOffAsync(_engagementId, DocumentId, level.ToString(), hash, code, cancellationToken);
        if (result.Value is null)
        {
            Error = result.Error?.Message ?? "Máy chủ không nhận chữ ký.";
            return;
        }

        SignOffCode = "";
        SignOffMessage = "Đã ký. Đang đồng bộ trạng thái mới từ máy chủ.";
        _loop.Poke();
        await ReloadAsync(cancellationToken);
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

    /// <summary>Attaches files to the paper: each is copied into the local store and linked. One bad file does not stop the others.</summary>
    [RelayCommand]
    private async Task AttachAsync(IReadOnlyList<string>? paths, CancellationToken cancellationToken)
    {
        if (paths is not { Count: > 0 } || !CanEdit)
        {
            return;
        }

        var problems = new List<string>();
        await RunAsync(async unit =>
        {
            foreach (var path in paths)
            {
                try
                {
                    await _evidence.AttachAsync(unit, _engagementId, _user.Id, DocumentId, path, cancellationToken);
                }
                catch (InvalidOperationException ex)
                {
                    problems.Add(ex.Message);
                }
            }
        }, cancellationToken);
        if (problems.Count > 0)
        {
            Error = string.Join(Environment.NewLine, problems);
        }
    }

    /// <summary>Removes the link. The file itself stays in the engagement's evidence and in the intake record.</summary>
    [RelayCommand]
    private Task RemoveEvidenceAsync(EvidenceItemViewModel? item, CancellationToken cancellationToken) =>
        item is null || !CanEdit ? Task.CompletedTask
            : RunAsync(unit => unit.Workspace(_engagementId, _user.Id).UnlinkEvidenceAsync(DocumentId, item.Hash, cancellationToken), cancellationToken);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task OpenEvidenceAsync(EvidenceItemViewModel? item, CancellationToken cancellationToken)
    {
        if (item is null)
        {
            return;
        }

        Error = null;
        try
        {
            await using var unit = await _units.OpenAsync(cancellationToken);
            await _evidence.OpenAsync(unit.Db, _engagementId, item.Hash, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
        }
    }

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
