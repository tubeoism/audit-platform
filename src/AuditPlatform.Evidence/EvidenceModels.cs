namespace AuditPlatform.Evidence;

/// <summary>Reference to a stored evidence object. <see cref="Hash"/> is the lowercase hex BLAKE3-256 of the content.</summary>
public sealed record EvidenceRef(string Hash, long SizeBytes);

/// <summary>Chain-of-custody record written each time a file is received, even when the content already exists.</summary>
public sealed record EvidenceIntake(
    string Hash,
    long SizeBytes,
    string OriginalFileName,
    string? MediaType,
    Guid ReceivedBy,
    DateTimeOffset ReceivedAt,
    string Source);

public sealed class EvidenceIntegrityException : Exception
{
    public EvidenceIntegrityException() { }
    public EvidenceIntegrityException(string message) : base(message) { }
    public EvidenceIntegrityException(string message, Exception innerException) : base(message, innerException) { }
}
