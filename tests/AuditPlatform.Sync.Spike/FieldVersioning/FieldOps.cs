using System.Text.Json;
using System.Text.Json.Serialization;

namespace AuditPlatform.Sync.Spike.FieldVersioning;

/// <summary>
/// One intent on a working paper. Client operations travel in a <c>ChangeEnvelope</c> with payload kind
/// <see cref="FieldPayload.Kind"/>; master operations are produced only by the master's online API.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(CreatePaper), "create")]
[JsonDerivedType(typeof(SetField), "set")]
[JsonDerivedType(typeof(ResolveConflict), "resolve")]
[JsonDerivedType(typeof(LinkEvidence), "link")]
[JsonDerivedType(typeof(UnlinkEvidence), "unlink")]
[JsonDerivedType(typeof(RaiseNote), "note-raise")]
[JsonDerivedType(typeof(RespondNote), "note-respond")]
[JsonDerivedType(typeof(ClearNote), "note-clear")]
[JsonDerivedType(typeof(SubmitForReview), "submit")]
[JsonDerivedType(typeof(SignOffPaper), "sign-off")]
public abstract record FieldOp
{
    /// <summary>True for operations that only the master may originate (they need an online, stepped-up session).</summary>
    [JsonIgnore]
    public virtual bool MasterOnly => false;
}

public sealed record CreatePaper(string TemplateCode, int TemplateVersion, Guid PreparerId) : FieldOp;

/// <param name="Key">Field key from the procedure template.</param>
/// <param name="Value">The whole new value of the field.</param>
/// <param name="BaseChangeId">
/// The change that produced the value the author was looking at when editing (null when the field was empty).
/// The master accepts the edit only if that is still the current value; otherwise the edit becomes a flagged conflict.
/// </param>
public sealed record SetField(string Key, string Value, Guid? BaseChangeId) : FieldOp;

/// <summary>Closes a flagged conflict by choosing the value to keep. Must be based on the current field value.</summary>
public sealed record ResolveConflict(Guid ConflictId, string Value, Guid? BaseChangeId) : FieldOp;

public sealed record LinkEvidence(string ContentHash) : FieldOp;

public sealed record UnlinkEvidence(string ContentHash) : FieldOp;

public sealed record RaiseNote(Guid NoteId, string FieldKey, string Text) : FieldOp;

public sealed record RespondNote(Guid NoteId, string Response) : FieldOp;

/// <param name="NoteId">The note to clear.</param>
/// <param name="LastSeenResponseChangeId">The latest response the reviewer had read; clearing fails if a newer one exists.</param>
public sealed record ClearNote(Guid NoteId, Guid LastSeenResponseChangeId) : FieldOp;

public sealed record SubmitForReview : FieldOp;

public sealed record SignOffPaper(string Level, string ContentHash) : FieldOp
{
    public override bool MasterOnly => true;
}

public static class FieldPayload
{
    public const string Kind = "field-v1";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static byte[] Encode(FieldOp op) => JsonSerializer.SerializeToUtf8Bytes(op, Options);

    public static FieldOp Decode(byte[] payload) =>
        JsonSerializer.Deserialize<FieldOp>(payload, Options) ?? throw new JsonException("Empty field-v1 payload.");
}
