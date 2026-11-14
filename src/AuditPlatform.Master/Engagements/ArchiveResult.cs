namespace AuditPlatform.Master.Engagements;

/// <summary>An engagement has been marked archived: when, which package (by digest) and where it is stored.</summary>
public sealed record ArchiveResult(DateTimeOffset ArchivedAt, string PackageDigest, string Location);
