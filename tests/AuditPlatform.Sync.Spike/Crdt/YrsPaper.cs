using AuditPlatform.Domain.WorkingPapers;
using YDotNet.Document;
using YDotNet.Document.Cells;
using YDotNet.Document.Transactions;
using YDotNet.Document.Types.Maps;
using YDotNet.Document.Types.Texts;

namespace AuditPlatform.Sync.Spike.Crdt;

/// <summary>
/// Candidate (b): one replica of one working paper as a Yrs document (YDotNet binding over the native Rust library).
/// Short fields are entries of a shared map, the conclusion is collaborative text, evidence links and review notes
/// are maps. Every replica accepts every edit locally; merging is automatic and never fails.
/// </summary>
public sealed class YrsPaper : IDisposable
{
    private readonly Doc _doc = new();
    private readonly Map _fields;
    private readonly Map _evidence;
    private readonly Map _notes;
    private readonly Text _conclusion;
    private readonly Guid _id;
    private readonly Guid _engagementId;

    public YrsPaper(Guid id, Guid engagementId)
    {
        _id = id;
        _engagementId = engagementId;
        _fields = _doc.Map("fields");
        _evidence = _doc.Map("evidence");
        _notes = _doc.Map("notes");
        _conclusion = _doc.Text("conclusion");
    }

    public void SetField(string key, string value) => Write(tx => _fields.Insert(tx, key, Input.String(value)));

    public string? Field(string key) => Read(tx => _fields.Get(tx, key)?.String);

    public IReadOnlyDictionary<string, string> Fields() => Read(tx => ToDictionary(_fields, tx));

    public void InsertConclusion(int index, string text) => Write(tx => _conclusion.Insert(tx, (uint)index, text));

    public void RemoveConclusion(int index, int length) => Write(tx => _conclusion.RemoveRange(tx, (uint)index, (uint)length));

    public string Conclusion() => Read(tx => _conclusion.String(tx));

    public void LinkEvidence(string hash) => Write(tx => _evidence.Insert(tx, hash, Input.Boolean(true)));

    public void UnlinkEvidence(string hash) => Write(tx => _evidence.Remove(tx, hash));

    public IReadOnlyCollection<string> Evidence() => Read(tx => ToDictionary(_evidence, tx, _ => "").Keys.Order(StringComparer.Ordinal).ToList());

    /// <summary>Review notes as flat entries "{noteId}/text", "{noteId}/response", "{noteId}/status".</summary>
    public void SetNote(Guid noteId, string part, string value) => Write(tx => _notes.Insert(tx, $"{noteId:N}/{part}", Input.String(value)));

    public string? Note(Guid noteId, string part) => Read(tx => _notes.Get(tx, $"{noteId:N}/{part}")?.String);

    public byte[] StateVector() => Read(tx => tx.StateVectorV1());

    /// <summary>Everything this replica has that a replica with <paramref name="remoteStateVector"/> lacks.</summary>
    public byte[] UpdateSince(byte[]? remoteStateVector) => Read(tx => tx.StateDiffV1(remoteStateVector!));

    public TransactionUpdateResult Apply(byte[] update)
    {
        using var tx = _doc.WriteTransaction();
        var result = tx.ApplyV1(update);
        tx.Commit();
        return result;
    }

    /// <summary>The same domain content hash as candidate (a), from the materialised state.</summary>
    public string ComputeContentHash()
    {
        var paper = new WorkingPaper(_id, _engagementId, "AUP-CSH-03", 1, Guid.Empty);
        foreach (var (key, value) in Fields())
        {
            paper.SetField(key, value);
        }

        paper.SetField("conclusion", Conclusion());
        foreach (var hash in Evidence())
        {
            paper.LinkEvidence(hash);
        }

        return paper.ComputeContentHash();
    }

    /// <summary>Content hash plus review notes; used to compare replicas.</summary>
    public string Fingerprint() =>
        ComputeContentHash() + "|" + string.Join(';', Read(tx => ToDictionary(_notes, tx)).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));

    public void Dispose() => _doc.Dispose();

    private static Dictionary<string, string> ToDictionary(Map map, Transaction tx, Func<Output, string>? value = null)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in map.Iterate(tx))
        {
            result[entry.Key] = (value ?? (o => o.String))(entry.Value);
        }

        return result;
    }

    private void Write(Action<Transaction> action)
    {
        using var tx = _doc.WriteTransaction();
        action(tx);
        tx.Commit();
    }

    private T Read<T>(Func<Transaction, T> read)
    {
        using var tx = _doc.ReadTransaction();
        var value = read(tx);
        tx.Commit();
        return value;
    }
}
