namespace AuditPlatform.Contracts.Evidence;

public static class EvidenceRoutes
{
    public const string List = "/api/engagements/{engagementId:guid}/evidence";
    public const string File = "/api/engagements/{engagementId:guid}/evidence/{hash}";

    /// <summary>Query parameter of an upload carrying the file name as the auditor had it.</summary>
    public const string FileNameParameter = "name";

    /// <summary>95 MB: below the request limit of the proxy in front of the master.</summary>
    public const long DefaultMaxBytes = 95L * 1024 * 1024;

    /// <summary>A file larger than <see cref="DefaultMaxBytes"/> travels in parts of this size (the master states the size it wants).</summary>
    public const long DefaultPartBytes = 32L * 1024 * 1024;

    /// <summary>The largest file the master takes in parts: 1 GiB.</summary>
    public const long DefaultMaxPartedBytes = 1024L * 1024 * 1024;

    /// <summary>GET: which parts of this file the master already holds. Query parameter <see cref="TotalParameter"/>.</summary>
    public const string Parts = "/api/engagements/{engagementId:guid}/evidence/{hash}/parts";

    /// <summary>PUT: one part, numbered from 1. Query parameter <see cref="TotalParameter"/>.</summary>
    public const string Part = "/api/engagements/{engagementId:guid}/evidence/{hash}/parts/{number:int}";

    /// <summary>POST: all parts are in; the master assembles the file and checks it against its hash. Query parameters <see cref="TotalParameter"/> and <see cref="FileNameParameter"/>.</summary>
    public const string Complete = "/api/engagements/{engagementId:guid}/evidence/{hash}/complete";

    /// <summary>Query parameter of a parted upload carrying the size of the whole file.</summary>
    public const string TotalParameter = "total";

    public static string PartsFor(Guid engagementId, string hash, long total) => $"/api/engagements/{engagementId:D}/evidence/{hash}/parts?{TotalParameter}={total}";

    public static string PartFor(Guid engagementId, string hash, int number, long total) =>
        $"/api/engagements/{engagementId:D}/evidence/{hash}/parts/{number}?{TotalParameter}={total}";

    public static string CompleteFor(Guid engagementId, string hash, long total, string fileName) =>
        $"/api/engagements/{engagementId:D}/evidence/{hash}/complete?{TotalParameter}={total}&{FileNameParameter}={Uri.EscapeDataString(fileName)}";

    public static string ListFor(Guid engagementId) => $"/api/engagements/{engagementId:D}/evidence";

    public static string FileFor(Guid engagementId, string hash) => $"/api/engagements/{engagementId:D}/evidence/{hash}";
}

/// <summary>
/// One evidence file of an engagement as the master holds it. <see cref="Hash"/> is the lowercase hex BLAKE3-256 of
/// the content and is what a working paper links to; the rest is the first intake of that content in the engagement.
/// </summary>
public sealed record EvidenceInfo(string Hash, long SizeBytes, string FileName, string? MediaType, Guid ReceivedBy, DateTimeOffset ReceivedAt);

public sealed record EvidenceError(string Code, string Message);

/// <summary>
/// How a large file is sent: in parts of <paramref name="PartBytes"/> (the last one shorter), of which the master already
/// holds <paramref name="Received"/>. <paramref name="MaxBytes"/> is the largest whole file it takes this way.
/// </summary>
public sealed record EvidenceParts(long PartBytes, long MaxBytes, IReadOnlyList<int> Received);
