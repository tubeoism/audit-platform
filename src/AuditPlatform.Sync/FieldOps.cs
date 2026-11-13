using System.Text.Json;
using System.Text.Json.Serialization;

namespace AuditPlatform.Sync;

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
    /// <summary>Stable short name of the operation; the discriminator on the wire and in the audit log.</summary>
    [JsonIgnore]
    public abstract string Name { get; }

    /// <summary>True for operations that only the master may originate (they need an online, stepped-up session).</summary>
    [JsonIgnore]
    public virtual bool MasterOnly => false;
}

public sealed record CreatePaper(string TemplateCode, int TemplateVersion, Guid PreparerId) : FieldOp
{
    public override string Name => "create";
}

/// <param name="Key">Field key from the procedure template.</param>
/// <param name="Value">The whole new value of the field. Long text is replaced as one value, never merged.</param>
/// <param name="BaseChangeId">
/// The change that produced the value the author was looking at when editing (null when the field was empty).
/// The master accepts the edit only if that is still the current value; otherwise the edit becomes a flagged conflict.
/// </param>
public sealed record SetField(string Key, string Value, Guid? BaseChangeId) : FieldOp
{
    public override string Name => "set";
}

/// <summary>Closes a flagged conflict by choosing the value to keep. Must be based on the current field value.</summary>
public sealed record ResolveConflict(Guid ConflictId, string Value, Guid? BaseChangeId) : FieldOp
{
    public override string Name => "resolve";
}

public sealed record LinkEvidence(string ContentHash) : FieldOp
{
    public override string Name => "link";
}

public sealed record UnlinkEvidence(string ContentHash) : FieldOp
{
    public override string Name => "unlink";
}

public sealed record RaiseNote(Guid NoteId, string FieldKey, string Text) : FieldOp
{
    public override string Name => "note-raise";
}

public sealed record RespondNote(Guid NoteId, string Response) : FieldOp
{
    public override string Name => "note-respond";
}

/// <param name="NoteId">The note to clear.</param>
/// <param name="LastSeenResponseChangeId">The latest response the reviewer had read; clearing fails if a newer one exists.</param>
public sealed record ClearNote(Guid NoteId, Guid LastSeenResponseChangeId) : FieldOp
{
    public override string Name => "note-clear";
}

public sealed record SubmitForReview : FieldOp
{
    public override string Name => "submit";
}

/// <param name="Level">Review level, the name of an <c>EngagementRole</c>: ManagerQa or JapanDesk.</param>
/// <param name="ContentHash">The content hash the master sealed; every replica recomputes it on replay.</param>
public sealed record SignOffPaper(string Level, string ContentHash) : FieldOp
{
    public override string Name => "sign-off";

    public override bool MasterOnly => true;
}

public static class FieldPayload
{
    public const string Kind = "field-v1";

    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static byte[] Encode(FieldOp op) => JsonSerializer.SerializeToUtf8Bytes(op, Options);

    /// <summary>Throws <see cref="JsonException"/> for a payload that is not a known field-v1 operation.</summary>
    public static FieldOp Decode(byte[] payload)
    {
        try
        {
            return JsonSerializer.Deserialize<FieldOp>(payload, Options) ?? throw new JsonException("Empty field-v1 payload.");
        }
        catch (NotSupportedException e)
        {
            // Missing or unknown "op" discriminator.
            throw new JsonException("Unknown field-v1 operation.", e);
        }
    }

    /// <summary>The operation as a JSON element, discriminator included; used for the audit trail.</summary>
    public static JsonElement Describe(FieldOp op) => JsonSerializer.SerializeToElement(op, Options);
}
