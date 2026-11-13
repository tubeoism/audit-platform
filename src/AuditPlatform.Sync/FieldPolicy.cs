using AuditPlatform.Domain.Engagements;

namespace AuditPlatform.Sync;

/// <summary>
/// Who may do what on a working paper (ADR-0004, "Authorisation"). The master applies it when it admits a change;
/// the client applies the same function to decide what to offer, so a button that is shown is a change that will be
/// admitted. It is not part of the reducer: a replica replays what the master admitted and never judges roles again.
/// </summary>
/// <remarks>
/// Preparation and review are kept apart. Content is written by preparers and the engagement manager; reviewers
/// (Manager QA, Japan Desk) raise notes and sign off and do not write the content they review. A flagged conflict is
/// decided by the person accountable for the paper, its preparer, or by the engagement manager.
/// </remarks>
public static class FieldPolicy
{
    public const string NotAnEditor = "Only a preparer or the engagement manager can change a working paper.";
    public const string NotAReviewer = "Only a reviewer (Manager QA, Japan Desk) or the engagement manager can raise or clear a review note.";
    public const string NotTheOwner = "Only the preparer of this working paper or the engagement manager can decide a conflict.";
    public const string NotYourPaper = "A working paper is created by its own preparer, or assigned by the engagement manager.";

    public static bool CanEdit(IReadOnlySet<EngagementRole> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return roles.Contains(EngagementRole.Preparer) || roles.Contains(EngagementRole.EngagementManager);
    }

    public static bool CanReview(IReadOnlySet<EngagementRole> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return roles.Contains(EngagementRole.ManagerQa) || roles.Contains(EngagementRole.JapanDesk)
            || roles.Contains(EngagementRole.EngagementManager);
    }

    public static bool CanResolve(IReadOnlySet<EngagementRole> roles, FieldDocument document, Guid user)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(document);
        return roles.Contains(EngagementRole.EngagementManager)
            || (roles.Contains(EngagementRole.Preparer) && document.PreparerId == user);
    }

    /// <summary>Why this member may not make this change, or null if they may. Sign-off has its own checks on the master.</summary>
    public static string? WhyRefused(FieldOp op, IReadOnlySet<EngagementRole> roles, FieldDocument document, Guid author)
    {
        ArgumentNullException.ThrowIfNull(op);
        return op switch
        {
            CreatePaper when !CanEdit(roles) => NotAnEditor,
            CreatePaper create when create.PreparerId != author && !roles.Contains(EngagementRole.EngagementManager) => NotYourPaper,
            SetField or LinkEvidence or UnlinkEvidence or SubmitForReview or RespondNote when !CanEdit(roles) => NotAnEditor,
            ResolveConflict when !CanResolve(roles, document, author) => NotTheOwner,
            RaiseNote or ClearNote when !CanReview(roles) => NotAReviewer,
            _ => null,
        };
    }
}
