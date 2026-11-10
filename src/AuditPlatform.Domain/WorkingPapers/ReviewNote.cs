namespace AuditPlatform.Domain.WorkingPapers;

public enum ReviewNoteStatus
{
    Open = 0,
    Responded = 1,
    Cleared = 2,
}

/// <summary>Raised by a reviewer, answered by the preparer, cleared by the reviewer. Open notes block sign-off.</summary>
public sealed class ReviewNote
{
    public ReviewNote(Guid id, Guid raisedBy, string fieldKey, string text, DateTimeOffset raisedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        Id = id;
        RaisedBy = raisedBy;
        FieldKey = fieldKey;
        Text = text;
        RaisedAt = raisedAt;
    }

    public Guid Id { get; }
    public Guid RaisedBy { get; }
    public string FieldKey { get; }
    public string Text { get; }
    public DateTimeOffset RaisedAt { get; }
    public ReviewNoteStatus Status { get; private set; }
    public string? Response { get; private set; }
    public Guid? RespondedBy { get; private set; }
    public Guid? ClearedBy { get; private set; }

    public void Respond(Guid preparer, string response)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(response);
        if (Status == ReviewNoteStatus.Cleared)
        {
            throw new DomainException("A cleared review note cannot be answered.");
        }

        Response = response;
        RespondedBy = preparer;
        Status = ReviewNoteStatus.Responded;
    }

    public void Clear(Guid reviewer)
    {
        if (reviewer != RaisedBy)
        {
            throw new DomainException("Only the reviewer who raised a note can clear it.");
        }

        if (Status != ReviewNoteStatus.Responded)
        {
            throw new DomainException("A review note must be answered before it is cleared.");
        }

        ClearedBy = reviewer;
        Status = ReviewNoteStatus.Cleared;
    }
}
