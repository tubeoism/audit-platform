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
