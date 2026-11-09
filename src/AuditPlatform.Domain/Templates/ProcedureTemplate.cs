namespace AuditPlatform.Domain.Templates;

/// <summary>
/// A firm-standard procedure from the methodology library (source: tubeoism/ia-aup-procedures).
/// Templates are versioned; a working paper pins the exact template version it was created from.
/// </summary>
public sealed record ProcedureTemplate(
    string Code,
    string AreaCode,
    string AreaName,
    string Title,
    string Procedure,
    IReadOnlyList<string> AuditContent,
    IReadOnlyList<string> EvidenceToCheck,
    string Language,
    int Version);

/// <summary>An audit area (one methodology file), e.g. "Cash Management" with code prefix CSH.</summary>
public sealed record AuditAreaTemplate(
    string AreaName,
    IReadOnlyList<string> Objectives,
    IReadOnlyList<ProcedureTemplate> Procedures);
