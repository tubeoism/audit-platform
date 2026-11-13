using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AuditPlatform.Domain.Engagements;
using AuditPlatform.Domain.WorkingPapers;

namespace AuditPlatform.Sync.Spike.FieldVersioning;

public enum Decision
{
    Accepted = 0,
    Conflict = 1,
    Rejected = 2,
}

public readonly record struct ApplyResult(Decision Decision, string? Reason = null)
{
    public static ApplyResult Ok => new(Decision.Accepted);

    public static ApplyResult Reject(string reason) => new(Decision.Rejected, reason);
}

/// <summary>Current value of a field and the change that produced it. The change id is the field's version.</summary>
public sealed record FieldCell(string Value, Guid ChangeId, Guid Author);

/// <summary>One entry in a field's history: every accepted value, in master order, with its author.</summary>
public sealed record FieldRevision(string Key, string? Before, string After, Guid ChangeId, Guid Author);

public sealed record NoteResponse(Guid ChangeId, Guid Author, string Text);

public sealed class NoteState(Guid id, Guid raisedBy, string fieldKey, string text)
{
    public Guid Id { get; } = id;
    public Guid RaisedBy { get; } = raisedBy;
    public string FieldKey { get; } = fieldKey;
    public string Text { get; } = text;
    public List<NoteResponse> Responses { get; } = [];
    public Guid? ClearedBy { get; set; }
    public bool IsCleared => ClearedBy is not null;
}

public enum ConflictStatus
{
    Open = 0,
    Resolved = 1,
    Superseded = 2,
}

/// <summary>
/// A field edit that was based on a value the master no longer holds. Nothing is overwritten: the master keeps its
/// value, records the proposed one here, and a person chooses.
/// </summary>
public sealed class ConflictState(Guid id, string key, Guid author, string proposedValue, string? masterValue, Guid? masterChangeId)
{
    public Guid Id { get; } = id;
    public string Key { get; } = key;
    public Guid Author { get; } = author;
    public string ProposedValue { get; } = proposedValue;
    public string? MasterValue { get; } = masterValue;
    public Guid? MasterChangeId { get; } = masterChangeId;
    public ConflictStatus Status { get; set; }
    public Guid? ResolvedBy { get; set; }
}

/// <param name="BrokenByChangeId">The content change that broke this seal after it was given; null while the seal holds.</param>
public sealed record SignOffRecord(EngagementRole Level, Guid SignedBy, string ContentHash, Guid? BrokenByChangeId)
{
    public bool IsBroken => BrokenByChangeId is not null;
}

/// <summary>
/// State of one working paper as a deterministic fold over the master-ordered change log. The master and every
/// client run this same code over the same log, which is what makes replicas converge. No clocks are involved.
/// </summary>
public sealed class FieldDocument(Guid id, Guid engagementId)
{
    private readonly SortedDictionary<string, FieldCell> _fields = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _evidence = new(StringComparer.Ordinal);
    private readonly SortedDictionary<Guid, NoteState> _notes = [];
    private readonly SortedDictionary<Guid, ConflictState> _conflicts = [];
    private readonly List<FieldRevision> _history = [];
    private readonly List<SignOffRecord> _signOffs = [];

    public Guid Id { get; } = id;
    public Guid EngagementId { get; } = engagementId;
    public bool Exists { get; private set; }
    public string TemplateCode { get; private set; } = "";
    public int TemplateVersion { get; private set; }
    public Guid PreparerId { get; private set; }
    public WorkingPaperStatus Status { get; private set; }
    public IReadOnlyDictionary<string, FieldCell> Fields => _fields;
    public IReadOnlyCollection<string> Evidence => _evidence;
    public IReadOnlyDictionary<Guid, NoteState> Notes => _notes;
    public IReadOnlyDictionary<Guid, ConflictState> Conflicts => _conflicts;
    public IReadOnlyList<FieldRevision> History => _history;
    public IReadOnlyList<SignOffRecord> SignOffs => _signOffs;
    public bool IsSealed => Status >= WorkingPaperStatus.ManagerQaSignedOff;
    public IEnumerable<SignOffRecord> ValidSignOffs => _signOffs.Where(s => !s.IsBroken);
    public IEnumerable<ConflictState> OpenConflicts => _conflicts.Values.Where(c => c.Status == ConflictStatus.Open);

    public string? Value(string key) => _fields.TryGetValue(key, out var cell) ? cell.Value : null;

    /// <summary>Decides one change and, unless it is rejected, applies it. A rejected change leaves no trace in the state.</summary>
    public ApplyResult Apply(Guid changeId, Guid author, FieldOp op)
    {
        ArgumentNullException.ThrowIfNull(op);
        if (op is CreatePaper create)
        {
            if (Exists)
            {
                return ApplyResult.Reject("Working paper already exists.");
            }

            Exists = true;
            TemplateCode = create.TemplateCode;
            TemplateVersion = create.TemplateVersion;
            PreparerId = create.PreparerId;
            return ApplyResult.Ok;
        }

        if (!Exists)
        {
            return ApplyResult.Reject("Unknown working paper.");
        }

        return op switch
        {
            SetField set => ApplySet(changeId, author, set),
            ResolveConflict resolve => ApplyResolve(changeId, author, resolve),
            LinkEvidence link => ApplyContent(changeId, () => _evidence.Add(link.ContentHash)),
            UnlinkEvidence unlink => ApplyContent(changeId, () => _evidence.Remove(unlink.ContentHash)),
            RaiseNote raise => ApplyRaise(author, raise),
            RespondNote respond => ApplyRespond(changeId, author, respond),
            ClearNote clear => ApplyClear(author, clear),
            SubmitForReview => ApplySubmit(),
            SignOffPaper signOff => ApplySignOff(author, signOff),
            _ => ApplyResult.Reject($"Unsupported operation {op.GetType().Name}."),
        };
    }

    /// <summary>The existing domain hash (ADR-0003 / design doc 7.2), computed from the merged state.</summary>
    public string ComputeContentHash()
    {
        var paper = new WorkingPaper(Id, EngagementId, TemplateCode, TemplateVersion, PreparerId);
        foreach (var (key, cell) in _fields)
        {
            paper.SetField(key, cell.Value);
        }

        foreach (var hash in _evidence)
        {
            paper.LinkEvidence(hash);
        }

        return paper.ComputeContentHash();
    }

    /// <summary>Hash of the whole replicated state (content, versions, notes, conflicts, status); used to compare replicas.</summary>
    public string Fingerprint()
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{Id:N}|{Status}|{TemplateCode}|{TemplateVersion}|{PreparerId:N}\n");
        foreach (var (key, cell) in _fields)
        {
            sb.Append(CultureInfo.InvariantCulture, $"f|{key}|{cell.ChangeId:N}|{cell.Author:N}|{cell.Value}\n");
        }

        foreach (var hash in _evidence)
        {
            sb.Append(CultureInfo.InvariantCulture, $"e|{hash}\n");
        }

        foreach (var note in _notes.Values)
        {
            sb.Append(CultureInfo.InvariantCulture, $"n|{note.Id:N}|{note.FieldKey}|{note.Text}|{note.ClearedBy:N}|{string.Join(';', note.Responses.Select(r => r.ChangeId.ToString("N")))}\n");
        }

        foreach (var conflict in _conflicts.Values)
        {
            sb.Append(CultureInfo.InvariantCulture, $"c|{conflict.Id:N}|{conflict.Key}|{conflict.Status}|{conflict.ProposedValue}|{conflict.ResolvedBy:N}\n");
        }

        foreach (var signOff in _signOffs)
        {
            sb.Append(CultureInfo.InvariantCulture, $"s|{signOff.Level}|{signOff.SignedBy:N}|{signOff.ContentHash}|{signOff.BrokenByChangeId:N}\n");
        }

        sb.Append(CultureInfo.InvariantCulture, $"h|{_history.Count}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public const string SealBrokenReason = "Content changed after sign-off: the seal is broken and the working paper is back in review.";

    /// <summary>
    /// Runs a content change. Content stays editable after sign-off (sponsor decision 2026-11-13): if the change
    /// alters the signed content, every standing sign-off is marked broken by this change and the paper returns to
    /// review. Nothing is deleted: the broken sign-offs stay on record with the change that broke them.
    /// </summary>
    private ApplyResult ApplyContent(Guid changeId, Action mutate)
    {
        if (!IsSealed)
        {
            mutate();
            return ApplyResult.Ok;
        }

        var before = ComputeContentHash();
        mutate();
        if (string.Equals(before, ComputeContentHash(), StringComparison.Ordinal))
        {
            return ApplyResult.Ok;
        }

        for (var i = 0; i < _signOffs.Count; i++)
        {
            if (!_signOffs[i].IsBroken)
            {
                _signOffs[i] = _signOffs[i] with { BrokenByChangeId = changeId };
            }
        }

        Status = WorkingPaperStatus.ReadyForReview;
        return new ApplyResult(Decision.Accepted, SealBrokenReason);
    }

    private ApplyResult ApplySet(Guid changeId, Guid author, SetField set)
    {
        _fields.TryGetValue(set.Key, out var current);
        var basedOnCurrent = set.BaseChangeId == current?.ChangeId;
        var sameValue = current is not null && string.Equals(current.Value, set.Value, StringComparison.Ordinal);
        if (basedOnCurrent || sameValue)
        {
            return ApplyContent(changeId, () => Write(set.Key, set.Value, changeId, author));
        }

        // Stale base and a different value: keep the master's value, record the proposal, let a person choose.
        // The signed content is untouched, so a flagged conflict does not break a seal; resolving it may.
        foreach (var earlier in OpenConflicts.Where(c => c.Key == set.Key && c.Author == author).ToList())
        {
            earlier.Status = ConflictStatus.Superseded;
        }

        _conflicts[changeId] = new ConflictState(changeId, set.Key, author, set.Value, current?.Value, current?.ChangeId);
        return new ApplyResult(Decision.Conflict, $"Field '{set.Key}' was changed by someone else since you last synchronised.");
    }

    private ApplyResult ApplyResolve(Guid changeId, Guid author, ResolveConflict resolve)
    {
        if (!_conflicts.TryGetValue(resolve.ConflictId, out var conflict) || conflict.Status != ConflictStatus.Open)
        {
            return ApplyResult.Reject("Conflict is not open.");
        }

        _fields.TryGetValue(conflict.Key, out var current);
        if (resolve.BaseChangeId != current?.ChangeId)
        {
            return ApplyResult.Reject($"Field '{conflict.Key}' changed again; look at the current value before resolving.");
        }

        conflict.Status = ConflictStatus.Resolved;
        conflict.ResolvedBy = author;
        return ApplyContent(changeId, () => Write(conflict.Key, resolve.Value, changeId, author));
    }

    private void Write(string key, string value, Guid changeId, Guid author)
    {
        _history.Add(new FieldRevision(key, Value(key), value, changeId, author));
        _fields[key] = new FieldCell(value, changeId, author);
    }

    private ApplyResult ApplyRaise(Guid author, RaiseNote raise)
    {
        if (_notes.ContainsKey(raise.NoteId))
        {
            return ApplyResult.Reject("Review note already exists.");
        }

        _notes[raise.NoteId] = new NoteState(raise.NoteId, author, raise.FieldKey, raise.Text);
        return ApplyResult.Ok;
    }

    private ApplyResult ApplyRespond(Guid changeId, Guid author, RespondNote respond)
    {
        if (!_notes.TryGetValue(respond.NoteId, out var note))
        {
            return ApplyResult.Reject("Unknown review note.");
        }

        if (note.IsCleared)
        {
            return ApplyResult.Reject("Review note was already cleared; the reviewer has not seen this response.");
        }

        // Responses are appended, never replaced.
        note.Responses.Add(new NoteResponse(changeId, author, respond.Response));
        return ApplyResult.Ok;
    }

    private ApplyResult ApplyClear(Guid author, ClearNote clear)
    {
        if (!_notes.TryGetValue(clear.NoteId, out var note))
        {
            return ApplyResult.Reject("Unknown review note.");
        }

        if (note.RaisedBy != author)
        {
            return ApplyResult.Reject("Only the reviewer who raised a note can clear it.");
        }

        if (note.IsCleared)
        {
            return ApplyResult.Reject("Review note is already cleared.");
        }

        if (note.Responses.Count == 0 || note.Responses[^1].ChangeId != clear.LastSeenResponseChangeId)
        {
            return ApplyResult.Reject("There is a response the reviewer has not read; the note stays open.");
        }

        note.ClearedBy = author;
        return ApplyResult.Ok;
    }

    private ApplyResult ApplySubmit()
    {
        if (Status != WorkingPaperStatus.Draft)
        {
            return ApplyResult.Reject("Only a draft working paper can be submitted for review.");
        }

        Status = WorkingPaperStatus.ReadyForReview;
        return ApplyResult.Ok;
    }

    private ApplyResult ApplySignOff(Guid reviewer, SignOffPaper signOff)
    {
        if (!Enum.TryParse<EngagementRole>(signOff.Level, out var level))
        {
            return ApplyResult.Reject($"Unknown review level '{signOff.Level}'.");
        }

        var expected = level switch
        {
            EngagementRole.ManagerQa => WorkingPaperStatus.ReadyForReview,
            EngagementRole.JapanDesk => WorkingPaperStatus.ManagerQaSignedOff,
            _ => (WorkingPaperStatus?)null,
        };
        if (expected is null || Status != expected)
        {
            return ApplyResult.Reject($"Cannot sign off at {signOff.Level} while the working paper is {Status}.");
        }

        if (reviewer == PreparerId)
        {
            return ApplyResult.Reject("The preparer cannot review their own working paper.");
        }

        if (_notes.Values.Any(n => !n.IsCleared))
        {
            return ApplyResult.Reject("All review notes must be cleared before sign-off.");
        }

        if (OpenConflicts.Any())
        {
            return ApplyResult.Reject("All flagged conflicts must be resolved before sign-off.");
        }

        if (!string.Equals(signOff.ContentHash, ComputeContentHash(), StringComparison.Ordinal))
        {
            return ApplyResult.Reject("Signed content hash does not match the working paper.");
        }

        _signOffs.Add(new SignOffRecord(level, reviewer, signOff.ContentHash, BrokenByChangeId: null));
        Status = level == EngagementRole.ManagerQa ? WorkingPaperStatus.ManagerQaSignedOff : WorkingPaperStatus.JapanDeskSignedOff;
        return ApplyResult.Ok;
    }
}
