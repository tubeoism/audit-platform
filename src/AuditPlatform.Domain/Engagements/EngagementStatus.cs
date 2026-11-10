namespace AuditPlatform.Domain.Engagements;

/// <summary>Engagement lifecycle (design doc 7.2). Only the master may move an engagement to Locked or Archived.</summary>
public enum EngagementStatus
{
    Planning = 0,
    Fieldwork = 1,
    Review = 2,
    Reporting = 3,
    Locked = 4,
    Archived = 5,
}
