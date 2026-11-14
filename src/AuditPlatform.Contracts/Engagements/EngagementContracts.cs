namespace AuditPlatform.Contracts.Engagements;

/// <summary>HTTP routes of the master's directory API: what a signed-in user needs before the first sync.</summary>
public static class DirectoryRoutes
{
    public const string Engagements = "/api/engagements";
    public const string Templates = "/api/templates";
}

/// <param name="Roles">Names of <c>EngagementRole</c> values.</param>
public sealed record EngagementMemberInfo(Guid UserId, string DisplayName, IReadOnlyList<string> Roles);

/// <summary>One engagement the caller is a member of, with everybody's roles so the client can name authors and reviewers.</summary>
/// <param name="Status">Name of an <c>EngagementStatus</c> value.</param>
/// <param name="IsReadOnly">True once the engagement is locked or archived: the master rejects every change.</param>
public sealed record EngagementInfo(
    Guid Id,
    string Code,
    string ClientName,
    string ContractReference,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string Status,
    bool IsReadOnly,
    IReadOnlyList<EngagementMemberInfo> Members);

/// <summary>
/// One approved version of a firm-standard procedure. A working paper pins code and version; the language only
/// selects the wording shown on the form.
/// </summary>
public sealed record ProcedureTemplateInfo(
    string Code,
    int Version,
    string Language,
    string AreaCode,
    string AreaName,
    string Title,
    string Procedure,
    IReadOnlyList<string> AuditContent,
    IReadOnlyList<string> EvidenceToCheck);

/// <summary>HTTP routes of the engagement lifecycle: moving through the phases and the final lock.</summary>
public static class LifecycleRoutes
{
    public static string Status(Guid engagementId) => $"/api/engagements/{engagementId:D}/status";

    public static string LockReadiness(Guid engagementId) => $"/api/engagements/{engagementId:D}/lock-readiness";

    public static string Lock(Guid engagementId) => $"/api/engagements/{engagementId:D}/lock";

    public const string StatusTemplate = "/api/engagements/{id:guid}/status";
    public const string LockReadinessTemplate = "/api/engagements/{id:guid}/lock-readiness";
    public const string LockTemplate = "/api/engagements/{id:guid}/lock";
}

/// <param name="Target">Name of an <c>EngagementStatus</c> value. Locking is not a status move; see <see cref="LockRequest"/>.</param>
public sealed record AdvanceRequest(string Target);

public sealed record AdvanceResponse(string Status);

/// <summary>One reason the engagement cannot be locked yet. <paramref name="WorkingPaperId"/> is null for engagement-wide reasons.</summary>
public sealed record LockBlocker(Guid? WorkingPaperId, string? TemplateCode, string Reason);

/// <summary>
/// What stands between the engagement and its lock, as the master sees it now. <paramref name="Digest"/> names exactly
/// this state (every working paper's content hash and the head of the change log): the lock grant is bought for it, so
/// what is locked is what the manager looked at.
/// </summary>
public sealed record LockReadiness(
    Guid EngagementId, string Status, bool Ready, int PaperCount, string Digest, long Head, IReadOnlyList<LockBlocker> Blockers);

/// <param name="StepUpToken">Single-use grant bought with a fresh authenticator code for this engagement and the digest from <see cref="LockReadiness"/>.</param>
public sealed record LockRequest(string StepUpToken);

public sealed record LockResponse(DateTimeOffset LockedAt, string Digest);
