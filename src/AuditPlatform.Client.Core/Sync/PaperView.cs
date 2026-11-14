using AuditPlatform.Client.Core.LocalStore;
using AuditPlatform.Domain.WorkingPapers;
using AuditPlatform.Sync;

namespace AuditPlatform.Client.Core.Sync;

/// <summary>A change of this user that the master flagged or refused, readable by a form.</summary>
public sealed record AttentionItem(Guid ChangeId, OutboxState State, FieldOp Op, string? Reason);

/// <summary>
/// One working paper for display: the confirmed replica with the user's unsent changes laid over it. Only
/// <see cref="Confirmed"/> is fact; everything else is this laptop's expectation until the master answers.
/// </summary>
public sealed class PaperView
{
    private PaperView(FieldDocument confirmed)
    {
        Confirmed = confirmed;
    }

    /// <summary>What the master last confirmed. Sign-off, conflicts, notes and field versions are read from here.</summary>
    public FieldDocument Confirmed { get; }

    /// <summary>True when the paper exists on the master or its creation is waiting in the outbox.</summary>
    public bool Exists { get; private set; }

    /// <summary>True while the paper itself has not reached the master yet.</summary>
    public bool IsLocalOnly => Exists && !Confirmed.Exists;

    public string TemplateCode { get; private set; } = "";
    public int TemplateVersion { get; private set; }
    public Guid PreparerId { get; private set; }
    public WorkingPaperStatus Status { get; private set; }

    /// <summary>Last unsent value per field key.</summary>
    public IReadOnlyDictionary<string, string> PendingValues { get; private set; } = new Dictionary<string, string>();

    public int PendingCount { get; private set; }

    /// <summary>Hashes of the evidence linked to the paper: confirmed links with the user's unsent links and removals applied.</summary>
    public IReadOnlyList<string> Evidence { get; private set; } = [];

    /// <summary>Links of this user that the master has not confirmed yet.</summary>
    public IReadOnlySet<string> PendingEvidence { get; private set; } = new HashSet<string>();

    public IReadOnlyList<AttentionItem> Attention { get; private set; } = [];

    /// <summary>The value to show in a field: the user's unsent text if there is one, otherwise the confirmed value.</summary>
    public string Value(string key) => PendingValues.TryGetValue(key, out var mine) ? mine : Confirmed.Value(key) ?? "";

    public static PaperView Build(FieldDocument confirmed, IReadOnlyList<OutboxChangeRow> outbox)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        ArgumentNullException.ThrowIfNull(outbox);
        var view = new PaperView(confirmed)
        {
            Exists = confirmed.Exists, TemplateCode = confirmed.TemplateCode, TemplateVersion = confirmed.TemplateVersion,
            PreparerId = confirmed.PreparerId, Status = confirmed.Status,
        };
        var pending = new Dictionary<string, string>(StringComparer.Ordinal);
        var attention = new List<AttentionItem>();
        var evidence = new SortedSet<string>(confirmed.Evidence, StringComparer.Ordinal);
        var pendingEvidence = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in outbox)
        {
            FieldOp op;
            try
            {
                op = FieldPayload.Decode(row.Payload);
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }

            if (row.State != OutboxState.Pending)
            {
                attention.Add(new AttentionItem(row.ChangeId, row.State, op, row.LastError));
                continue;
            }

            view.PendingCount++;
            switch (op)
            {
                case CreatePaper create when !view.Exists:
                    view.Exists = true;
                    view.TemplateCode = create.TemplateCode;
                    view.TemplateVersion = create.TemplateVersion;
                    view.PreparerId = create.PreparerId;
                    break;
                case SetField set:
                    pending[set.Key] = set.Value;
                    break;
                case LinkEvidence link:
                    evidence.Add(link.ContentHash);
                    pendingEvidence.Add(link.ContentHash);
                    break;
                case UnlinkEvidence unlink:
                    evidence.Remove(unlink.ContentHash);
                    pendingEvidence.Remove(unlink.ContentHash);
                    break;
                case SubmitForReview when view.Status == WorkingPaperStatus.Draft:
                    view.Status = WorkingPaperStatus.ReadyForReview;
                    break;
            }
        }

        view.PendingValues = pending;
        view.Attention = attention;
        view.Evidence = [.. evidence];
        view.PendingEvidence = pendingEvidence;
        return view;
    }
}
