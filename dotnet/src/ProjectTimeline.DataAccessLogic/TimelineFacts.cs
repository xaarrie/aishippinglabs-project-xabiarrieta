using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pgvector;
using Microsoft.EntityFrameworkCore;

namespace ProjectTimeline.DataAccessLogic;

public static class TimelineFactEvidence
{
    public static string Fingerprint(TimelineEntry entry) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { entry.Id, entry.TimelineId, entry.EntryNumber, entry.AuthorId, entry.Title, entry.Content }))));

    public static string CandidateKey(TimelineFact fact) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { fact.Claim, fact.EntityKey, fact.EntityType, fact.SourceQuote, fact.SourceKind }))));

    public static string ResultHash(IEnumerable<TimelineFact> facts) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join("\n", facts.Select(CandidateKey).Order(StringComparer.Ordinal)))));

    public static TimelineFact CandidateEvidence(TimelineFactAddition addition) => new()
    {
        Claim = addition.Claim, EntityKey = addition.EntityKey, EntityType = addition.EntityType,
        SourceQuote = addition.SourceQuote, SourceKind = addition.SourceKind
    };

    public static bool ExtractionKey(TimelineFactJob job) => job.WorkKey == $"extraction:{job.EntryId:N}"
        || job.WorkKey == $"extraction:{job.EntryId:N}:{job.Id:N}";

    // One translatable eligibility contract for chronology, search, review and embedding workers.
    public static IQueryable<TimelineFact> Effective(ProjectTimelineDbContext db, Guid timelineId, int? through = null) =>
        from fact in db.TimelineFacts
        join entry in db.TimelineEntries on fact.EntryId equals entry.Id
        where fact.Valid && entry.TimelineId == timelineId && (through == null || entry.EntryNumber <= through)
            && ((fact.ExtractionJobId != null && fact.CreationReviewId == null && fact.AdditionId == null && fact.CandidateKey != null
                && db.TimelineFactJobs.Any(j => j.Id == fact.ExtractionJobId && j.Kind == "Extraction" && j.State == "Completed"
                    && j.EntryId == fact.EntryId && j.SourceFingerprint == fact.SourceFingerprint && j.ExtractionResultHash != null))
                || (fact.ExtractionJobId == null && fact.CandidateKey == null && fact.AdditionId != null && db.TimelineFactProposals.Any(r =>
                    r.Id == fact.CreationReviewId && r.Status == "Accepted" && db.TimelineEntries.Any(e => e.Id == r.EntryId
                        && e.TimelineId == timelineId && (through == null || e.EntryNumber <= through)))))
        select fact;
}

// Effective facts exist after grounded extraction or review acceptance. Content/provenance are immutable; corrections create new facts.
public sealed class TimelineFact : AuditedEntity
{
    public Guid EntryId { get; set; }
    public Guid? CreationReviewId { get; set; }
    public Guid? AdditionId { get; set; }
    public Guid? ExtractionJobId { get; set; }
    public string? CandidateKey { get; set; }
    public string Claim { get; set; } = "";
    public string EntityKey { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string SourceQuote { get; set; } = "";
    public string SourceKind { get; set; } = "Entry";
    public string SourceFingerprint { get; set; } = "";
    public string ClarificationEvidence { get; set; } = "";
    public bool Valid { get; set; } = true;
    public DateTime? InvalidatedAtUtc { get; set; }
    public Guid? InvalidationReviewId { get; set; }
    // Null only for history predating causal invalidations. Never follows later corrections transitively.
    public Guid? InvalidatedByFactId { get; set; }
    public Vector? Embedding { get; set; }
    public string EmbeddingModel { get; set; } = "text-embedding-3-small";
    public DateTime? EmbeddedAtUtc { get; set; }
}

public sealed class TimelineFactProposal : AuditedEntity
{
    public Guid EntryId { get; set; }
    public long ExpectedRevision { get; set; }
    public string SourceFingerprint { get; set; } = "";
    public string QuestionsJson { get; set; } = "[]";
    public string ClarificationEvidence { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public Guid? AcceptedById { get; set; }
    public DateTime? AcceptedAtUtc { get; set; }
    public ICollection<TimelineFactAddition> Additions { get; set; } = [];
    public ICollection<TimelineFactInvalidation> Invalidations { get; set; } = [];
}

public sealed class TimelineFactAddition : AuditedEntity
{
    public Guid ProposalId { get; set; }
    public string Claim { get; set; } = "";
    public string EntityKey { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string SourceQuote { get; set; } = "";
    public string SourceKind { get; set; } = "Entry";
}

public sealed class TimelineFactInvalidation : AuditedEntity
{
    public Guid ProposalId { get; set; }
    public Guid FactId { get; set; }
    public Guid? InvalidatingAdditionId { get; set; }
    public string Reason { get; set; } = "";
}

// T2 owns workers, lease acquisition/renewal and retries. No automatic backfill or paid work in T1.
public sealed class TimelineFactJob : AuditedEntity
{
    public Guid EntryId { get; set; }
    public Guid? FactId { get; set; }
    public string WorkKey { get; set; } = "";
    public string Kind { get; set; } = "Extraction";
    public string SourceFingerprint { get; set; } = "";
    public string State { get; set; } = "Pending";
    public string Model { get; set; } = "text-embedding-3-small";
    public int Attempts { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseExpiresAtUtc { get; set; }
    public string? LastErrorCode { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public long? ExtractionRevision { get; set; }
    public string? ExtractionResultHash { get; set; }
    public string? ExtractionInputJson { get; set; }
    public string? EffectiveInputFingerprint { get; set; }
    public string? ExtractionSupportJson { get; set; }
}
