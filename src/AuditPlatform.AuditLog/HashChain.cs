using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AuditPlatform.AuditLog;

public static class HashChain
{
    /// <summary>Previous-hash value of the first entry in a chain.</summary>
    public static readonly string GenesisHash = new('0', 64);

    public static AuditEntry Seal(long sequence, string previousHash, AuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(previousHash);
        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "Sequence starts at 1.");
        }

        // Truncate to whole microseconds: PostgreSQL timestamptz stores microseconds, .NET ticks are 100 ns.
        // Without this, an entry read back from the database would no longer match its own hash.
        var utc = auditEvent.OccurredAt.ToUniversalTime();
        var occurredAt = new DateTimeOffset(utc.UtcTicks - (utc.UtcTicks % 10), TimeSpan.Zero);
        var hash = ComputeHash(sequence, previousHash, occurredAt, auditEvent.ActorUserId, auditEvent.Action,
            auditEvent.EntityType, auditEvent.EntityId, auditEvent.EngagementId, auditEvent.PayloadJson);

        return new AuditEntry(sequence, occurredAt, auditEvent.ActorUserId, auditEvent.Action, auditEvent.EntityType,
            auditEvent.EntityId, auditEvent.EngagementId, auditEvent.PayloadJson, previousHash, hash);
    }

    public static string ComputeHash(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return ComputeHash(entry.Sequence, entry.PreviousHash, entry.OccurredAt.ToUniversalTime(), entry.ActorUserId,
            entry.Action, entry.EntityType, entry.EntityId, entry.EngagementId, entry.PayloadJson);
    }

    /// <summary>Verifies an ordered chain from genesis. Returns the first broken entry, or null if the chain is intact.</summary>
    public static ChainBreak? Verify(IEnumerable<AuditEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var expectedPrevious = GenesisHash;
        long expectedSequence = 1;

        foreach (var entry in entries)
        {
            if (entry.Sequence != expectedSequence)
            {
                return new ChainBreak(entry.Sequence, $"Expected sequence {expectedSequence}.");
            }

            if (!string.Equals(entry.PreviousHash, expectedPrevious, StringComparison.Ordinal))
            {
                return new ChainBreak(entry.Sequence, "Previous hash does not match the preceding entry.");
            }

            if (!string.Equals(ComputeHash(entry), entry.Hash, StringComparison.Ordinal))
            {
                return new ChainBreak(entry.Sequence, "Entry content does not match its hash.");
            }

            expectedPrevious = entry.Hash;
            expectedSequence++;
        }

        return null;
    }

    private static string ComputeHash(long sequence, string previousHash, DateTimeOffset occurredAtUtc, Guid actor,
        string action, string entityType, string entityId, Guid? engagementId, string payloadJson)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "audit/v1");
        Append(hash, sequence.ToString(CultureInfo.InvariantCulture));
        Append(hash, previousHash);
        Append(hash, occurredAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture));
        Append(hash, actor.ToString("N"));
        Append(hash, action);
        Append(hash, entityType);
        Append(hash, entityId);
        Append(hash, engagementId?.ToString("N") ?? string.Empty);
        Append(hash, payloadJson);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}

public sealed record ChainBreak(long Sequence, string Reason);
