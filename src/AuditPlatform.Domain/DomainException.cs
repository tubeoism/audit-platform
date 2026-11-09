namespace AuditPlatform.Domain;

/// <summary>Raised when an operation would violate a domain rule (lifecycle, lock, sign-off).</summary>
public class DomainException : Exception
{
    public DomainException() { }
    public DomainException(string message) : base(message) { }
    public DomainException(string message, Exception innerException) : base(message, innerException) { }
}
