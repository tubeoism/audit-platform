namespace AuditPlatform.Contracts.Evidence;

public static class EvidenceRoutes
{
    public const string List = "/api/engagements/{engagementId:guid}/evidence";
    public const string File = "/api/engagements/{engagementId:guid}/evidence/{hash}";

    /// <summary>Query parameter of an upload carrying the file name as the auditor had it.</summary>
    public const string FileNameParameter = "name";

    /// <summary>95 MB: below the request limit of the proxy in front of the master.</summary>
    public const long DefaultMaxBytes = 95L * 1024 * 1024;

    public static string ListFor(Guid engagementId) => $"/api/engagements/{engagementId:D}/evidence";

    public static string FileFor(Guid engagementId, string hash) => $"/api/engagements/{engagementId:D}/evidence/{hash}";
}

/// <summary>
/// One evidence file of an engagement as the master holds it. <see cref="Hash"/> is the lowercase hex BLAKE3-256 of
/// the content and is what a working paper links to; the rest is the first intake of that content in the engagement.
/// </summary>
public sealed record EvidenceInfo(string Hash, long SizeBytes, string FileName, string? MediaType, Guid ReceivedBy, DateTimeOffset ReceivedAt);

public sealed record EvidenceError(string Code, string Message);
