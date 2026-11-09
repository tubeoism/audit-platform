using System.Security.Cryptography;
using System.Text;
using AuditPlatform.Domain.Engagements;

namespace AuditPlatform.Domain.WorkingPapers;

/// <summary>
/// An instance of a procedure template within an engagement: structured result fields, conclusion, evidence links,
/// review notes and sign-offs. Field values are plain strings at this stage; typed fields come with the template designer.
/// </summary>
public sealed class WorkingPaper
{
    private readonly SortedDictionary<string, string> _fields = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _evidenceHashes = new(StringComparer.Ordinal);
    private readonly List<ReviewNote> _reviewNotes = [];
    private readonly List<SignOff> _signOffs = [];

    public WorkingPaper(Guid id, Guid engagementId, string templateCode, int templateVersion, Guid preparerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateCode);
        Id = id;
        EngagementId = engagementId;
        TemplateCode = templateCode;
        TemplateVersion = templateVersion;
        PreparerId = preparerId;
    }

    public Guid Id { get; }
    public Guid EngagementId { get; }
    public string TemplateCode { get; }
    public int TemplateVersion { get; }
    public Guid PreparerId { get; }
    public WorkingPaperStatus Status { get; private set; }
    public IReadOnlyDictionary<string, string> Fields => _fields;
    public IReadOnlyCollection<string> EvidenceHashes => _evidenceHashes;
    public IReadOnlyList<ReviewNote> ReviewNotes => _reviewNotes;
    public IReadOnlyList<SignOff> SignOffs => _signOffs;

    public void SetField(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        _fields[key] = value;
    }

    public void LinkEvidence(string contentHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        _evidenceHashes.Add(contentHash);
    }

    public ReviewNote RaiseReviewNote(Guid reviewer, string fieldKey, string text, DateTimeOffset at)
    {
        var note = new ReviewNote(Guid.NewGuid(), reviewer, fieldKey, text, at);
        _reviewNotes.Add(note);
        return note;
    }

    public void SubmitForReview()
    {
        if (Status != WorkingPaperStatus.Draft)
        {
            throw new DomainException("Only a draft working paper can be submitted for review.");
        }

        Status = WorkingPaperStatus.ReadyForReview;
    }

    /// <summary>Signs off at the given review level. Levels are sequential: Manager QA first, then Japan Desk.</summary>
    public SignOff SignOff(EngagementRole level, Guid reviewer, DateTimeOffset at)
    {
        var expected = level switch
        {
            EngagementRole.ManagerQa => WorkingPaperStatus.ReadyForReview,
            EngagementRole.JapanDesk => WorkingPaperStatus.ManagerQaSignedOff,
            _ => throw new DomainException($"{level} is not a review level."),
        };

        if (Status != expected)
        {
            throw new DomainException($"Cannot sign off at {level} while the working paper is {Status}.");
        }

        if (reviewer == PreparerId)
        {
            throw new DomainException("The preparer cannot review their own working paper.");
        }

        if (_reviewNotes.Any(n => n.Status != ReviewNoteStatus.Cleared))
        {
            throw new DomainException("All review notes must be cleared before sign-off.");
        }

        var signOff = new SignOff(level, reviewer, ComputeContentHash(), at);
        _signOffs.Add(signOff);
        Status = level == EngagementRole.ManagerQa ? WorkingPaperStatus.ManagerQaSignedOff : WorkingPaperStatus.JapanDeskSignedOff;
        return signOff;
    }

    /// <summary>True when the content has changed since any sign-off was given.</summary>
    public bool IsSealBroken()
    {
        if (_signOffs.Count == 0)
        {
            return false;
        }

        var hash = ComputeContentHash();
        return _signOffs.Any(s => !string.Equals(s.ContentHash, hash, StringComparison.Ordinal));
    }

    /// <summary>
    /// SHA-256 over a canonical, length-prefixed encoding of the signable content
    /// (identity, template version, fields in ordinal key order, evidence hashes). Review notes and sign-offs are excluded.
    /// </summary>
    public string ComputeContentHash()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "wp/v1");
        Append(hash, Id.ToString("N"));
        Append(hash, EngagementId.ToString("N"));
        Append(hash, TemplateCode);
        Append(hash, TemplateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var (key, value) in _fields)
        {
            Append(hash, key);
            Append(hash, value);
        }

        Append(hash, "evidence");
        foreach (var evidence in _evidenceHashes)
        {
            Append(hash, evidence);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
