namespace AuditPlatform.Domain.Engagements;

/// <summary>
/// One client engagement under an AUP contract. Aggregate root for working papers.
/// </summary>
public sealed class Engagement
{
    private static readonly Dictionary<EngagementStatus, EngagementStatus[]> AllowedTransitions = new()
    {
        [EngagementStatus.Planning] = [EngagementStatus.Fieldwork],
        [EngagementStatus.Fieldwork] = [EngagementStatus.Review],
        [EngagementStatus.Review] = [EngagementStatus.Fieldwork, EngagementStatus.Reporting],
        [EngagementStatus.Reporting] = [EngagementStatus.Review, EngagementStatus.Locked],
        [EngagementStatus.Locked] = [EngagementStatus.Archived],
        [EngagementStatus.Archived] = [],
    };

    private readonly List<EngagementMember> _members = [];

    public Engagement(Guid id, string code, string clientName, string contractReference, DateOnly periodStart, DateOnly periodEnd)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contractReference);
        if (periodEnd < periodStart)
        {
            throw new DomainException("Engagement period end precedes period start.");
        }

        Id = id;
        Code = code;
        ClientName = clientName;
        ContractReference = contractReference;
        PeriodStart = periodStart;
        PeriodEnd = periodEnd;
        Status = EngagementStatus.Planning;
    }

    public Guid Id { get; }
    public string Code { get; }
    public string ClientName { get; }
    public string ContractReference { get; }
    public DateOnly PeriodStart { get; }
    public DateOnly PeriodEnd { get; }
    public EngagementStatus Status { get; private set; }
    public DateTimeOffset? LockedAt { get; private set; }
    public Guid? LockedBy { get; private set; }
    public IReadOnlyList<EngagementMember> Members => _members;

    /// <summary>True once the master has frozen the file; all writes must be rejected.</summary>
    public bool IsReadOnly => Status is EngagementStatus.Locked or EngagementStatus.Archived;

    public void AddMember(Guid userId, EngagementRole role)
    {
        EnsureWritable();
        if (_members.Any(m => m.UserId == userId && m.Role == role))
        {
            return;
        }

        _members.Add(new EngagementMember(userId, role));
    }

    public bool IsMember(Guid userId) => _members.Any(m => m.UserId == userId);

    public bool HasRole(Guid userId, EngagementRole role) => _members.Any(m => m.UserId == userId && m.Role == role);

    public void MoveTo(EngagementStatus target)
    {
        if (target is EngagementStatus.Locked)
        {
            throw new DomainException("Use Lock() to lock an engagement.");
        }

        Transition(target);
    }

    /// <summary>Freezes the engagement. Called by the master only, after all sign-offs are complete.</summary>
    public void Lock(Guid lockedBy, DateTimeOffset at)
    {
        if (!HasRole(lockedBy, EngagementRole.EngagementManager))
        {
            throw new DomainException("Only the engagement manager can lock an engagement.");
        }

        Transition(EngagementStatus.Locked);
        LockedAt = at;
        LockedBy = lockedBy;
    }

    public void EnsureWritable()
    {
        if (IsReadOnly)
        {
            throw new DomainException($"Engagement {Code} is {Status} and read-only.");
        }
    }

    private void Transition(EngagementStatus target)
    {
        if (!AllowedTransitions[Status].Contains(target))
        {
            throw new DomainException($"Engagement {Code} cannot move from {Status} to {target}.");
        }

        Status = target;
    }
}
