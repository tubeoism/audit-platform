using AuditPlatform.Domain.Engagements;

namespace AuditPlatform.Domain.WorkingPapers;

/// <summary>
/// A reviewer's sign-off at one review level, bound to the content hash of the working paper at that moment.
/// A later edit changes the hash and breaks the seal (design doc 7.2, spike week 4).
/// </summary>
public sealed record SignOff(EngagementRole Level, Guid SignedBy, string ContentHash, DateTimeOffset SignedAt);
