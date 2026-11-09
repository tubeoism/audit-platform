namespace AuditPlatform.Domain.Engagements;

public sealed record EngagementMember(Guid UserId, EngagementRole Role);
