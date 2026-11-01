using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace ProjectTimeline.DataAccessLogic;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TimelineExtractionFinding(int CandidateIndex, string Claim, string EntityKey, string EntityType,
    string SourceQuote, bool Supported, double SupportScore, IReadOnlyList<string> Issues, IReadOnlyList<string> EvidenceIds);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TimelineExtractionFeedbackQuestion(string Key, string Text, string Reason, IReadOnlyList<string> EvidenceIds);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TimelineExtractionFeedback(int Version, Guid GenerationId, Guid EntryId, string SourceFingerprint,
    string EffectiveInputFingerprint, string Disposition, IReadOnlyList<TimelineExtractionFinding> Findings,
    IReadOnlyList<TimelineExtractionFeedbackQuestion> Questions);

// Bounded canonical output, independent of frozen input, completion support and result identity.
public static class TimelineExtractionFeedbackCodec
{
    public const int MaxJsonLength = 512 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    { MaxDepth = 16, AllowDuplicateProperties = false, RespectNullableAnnotations = true };
    public static string Encode(TimelineExtractionFeedback feedback)
    {
        var json = JsonSerializer.Serialize(feedback, Json);
        if (json.Length > MaxJsonLength) throw Invalid();
        return json;
    }
    public static TimelineExtractionFeedback Decode(string json)
    {
        try
        {
            if (json.Length > MaxJsonLength) throw Invalid();
            var value = JsonSerializer.Deserialize<TimelineExtractionFeedback>(json, Json) ?? throw Invalid();
            if (Encode(value) != json) throw Invalid();
            return value;
        }
        catch (JsonException) { throw Invalid(); }
    }
    public static void Validate(TimelineExtractionFeedback feedback, TimelineFactJob job,
        TimelineEntry source, TimelineEntryExtractionInput input)
    {
        if (feedback.Version != 1 || feedback.GenerationId != job.Id || feedback.EntryId != source.Id
            || job.Kind != "Extraction" || job.EntryId != source.Id || input.JobId != job.Id
            || feedback.SourceFingerprint != job.SourceFingerprint || feedback.SourceFingerprint != TimelineFactEvidence.Fingerprint(source)
            || feedback.EffectiveInputFingerprint != job.EffectiveInputFingerprint
            || feedback.EffectiveInputFingerprint != input.EffectiveFingerprint
            || feedback.Findings is null || feedback.Findings.Count is < 1 or >= 200
            || feedback.Questions is null || feedback.Questions.Count + input.Answers.Count > 20)
            throw Invalid();
        var spans = TimelineExtractionEvidence.Create(source.Title, source.Content, source.Id, input);
        var indices = new HashSet<int>();
        foreach (var finding in feedback.Findings)
        {
            if (finding is null || !indices.Add(finding.CandidateIndex) || finding.CandidateIndex < 0 || finding.CandidateIndex >= feedback.Findings.Count
                || !TimelineEntryClarificationEvidence.Text(finding.Claim, 4000) || !TimelineEntryClarificationEvidence.Text(finding.EntityKey, 300)
                || !TimelineEntryClarificationEvidence.Text(finding.EntityType, 100) || !TimelineEntryClarificationEvidence.Text(finding.SourceQuote, 20000)
                || !double.IsFinite(finding.SupportScore) || finding.SupportScore is < 0 or > 1
                || finding.Issues is null || finding.Issues.Count > 5 || finding.Issues.Distinct().Count() != finding.Issues.Count
                || finding.Issues.Any(x => x is not ("Numeric" or "Date" or "Actor" or "Negation" or "Unsupported"))
                || finding.EvidenceIds is null) throw Invalid();
            if (finding.Supported || finding.EvidenceIds.Count > 0)
                TimelineExtractionEvidence.Reconstruct(source.Title, source.Content, input, spans, finding.EvidenceIds);
        }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var question in feedback.Questions)
        {
            if (question is null || !TimelineEntryClarificationEvidence.Text(question.Key, 100) || !keys.Add(question.Key)
                || !TimelineEntryClarificationEvidence.Text(question.Text, 2000) || !question.Text.EndsWith('?')
                || question.Reason is not ("MissingInformation" or "AmbiguousReference")) throw Invalid();
            TimelineExtractionEvidence.Reconstruct(source.Title, source.Content, input, spans, question.EvidenceIds);
        }
        var disposition = feedback.Questions.Count > 0 ? "NeedsClarification"
            : feedback.Findings.All(x => x.Supported && x.Issues.Count == 0) ? "Approved" : "Rejected";
        if (feedback.Disposition != disposition || disposition == "Approved") throw Invalid();
        Encode(feedback);
    }
    public static string QuestionKey(Guid generationId, string key) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{generationId:N}:{key}")));
    private static TimelineFactConflictException Invalid() => new("Invalid extraction feedback.");
}

public sealed partial class ProjectTimelineDbContext
{
    private readonly Dictionary<Guid, string> stagedExtractionFeedback = [];

    // Worker-only seam. No save here: feedback and questions/terminal failure commit together.
    public async Task StageExtractionFeedbackForLeaseAsync(Guid generationId, string owner, Guid version,
        TimelineExtractionFeedback feedback, CancellationToken ct = default)
    {
        var job = await TimelineFactJobs.SingleAsync(x => x.Id == generationId, ct);
        if (job.State != "Running" || job.Version != version || job.LeaseOwner != owner || job.ExtractionFeedbackJson is not null)
            throw new TimelineFactConflictException("Feedback requires the current extraction lease.");
        var source = await TimelineEntries.AsNoTracking().SingleAsync(x => x.Id == job.EntryId, ct);
        var snapshot = await ReadExtractionInputAsync(job, source, ct);
        TimelineExtractionFeedbackCodec.Validate(feedback, job, source, snapshot.Input);
        var json = TimelineExtractionFeedbackCodec.Encode(feedback);
        if (feedback.Disposition == "NeedsClarification")
            await StageEntryQuestionsForLeaseAsync(job.Id, owner, version, feedback.Questions.Select(x =>
                new TimelineEntryQuestion(TimelineExtractionFeedbackCodec.QuestionKey(job.Id, x.Key), x.Text)).ToArray(), ct);
        else
        {
            job.State = "Failed"; job.LeaseOwner = null; job.LeaseExpiresAtUtc = null;
        }
        job.ExtractionFeedbackJson = json;
        job.LastErrorCode = null; job.NextAttemptAtUtc = null;
        stagedExtractionFeedback.Add(job.Id, json);
    }

    private async Task ValidateExtractionFeedbackAsync(CancellationToken ct)
    {
        foreach (var change in ChangeTracker.Entries<TimelineFactJob>().Where(x => x.State is EntityState.Added or EntityState.Modified).ToList())
        {
            var job = change.Entity;
            if (change.State == EntityState.Added && job.ExtractionFeedbackJson is not null
                || job.Kind != "Extraction" && job.ExtractionFeedbackJson is not null)
                throw new TimelineFactConflictException("New or embedding jobs cannot forge feedback.");
            if (change.State != EntityState.Modified) continue;
            var original = change.Property(x => x.ExtractionFeedbackJson).OriginalValue;
            if (job.State == "Completed" && job.ExtractionFeedbackJson is not null
                || original is not null && job.State is not ("Failed" or "AwaitingClarification" or "Superseded"))
                throw new TimelineFactConflictException("Nonapproved feedback cannot complete or retry.");
            if (original is not null && job.State != "Superseded"
                && (job.State != change.Property(x => x.State).OriginalValue || change.Properties.Any(p => p.IsModified
                    && p.Metadata.Name is not ("Version" or "UpdatedAtUtc"))))
                throw new TimelineFactConflictException("Feedback generations require an explicit successor.");
            if (!change.Property(x => x.ExtractionFeedbackJson).IsModified) continue;
            if (original is not null || job.ExtractionFeedbackJson is null
                || !stagedExtractionFeedback.TryGetValue(job.Id, out var staged) || staged != job.ExtractionFeedbackJson)
                throw new TimelineFactConflictException("Feedback is immutable and server staged.");
            var stored = await TimelineFactJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id, ct);
            var source = await TimelineEntries.AsNoTracking().SingleAsync(x => x.Id == job.EntryId, ct);
            var timeline = await Timelines.FindAsync([source.TimelineId], ct) ?? throw new TimelineFactConflictException("Timeline missing.");
            if (stored.State != "Running" || stored.LeaseOwner is null || stored.LeaseExpiresAtUtc is null || stored.LeaseExpiresAtUtc <= DateTime.UtcNow
                || stored.Version != change.Property(x => x.Version).OriginalValue
                || stored.LeaseOwner != change.Property(x => x.LeaseOwner).OriginalValue
                || stored.ExtractionRevision != Entry(timeline).Property(x => x.FactRevision).OriginalValue
                || job.LeaseOwner is not null || job.LeaseExpiresAtUtc is not null || !TimelineFactEvidence.ExtractionKey(stored)
                || stored.FactId is not null || !await ClarificationWriterAsync(timeline.ProjectId, source.AuthorId, ct)
                || ChangeTracker.Entries<TimelineEntry>().Any(x => x.Entity.Id == source.Id && x.State != EntityState.Unchanged)
                || ChangeTracker.Entries<TimelineFact>().Any(x => x.State != EntityState.Unchanged)
                || ChangeTracker.Entries<TimelineFactProposal>().Any(x => x.State != EntityState.Unchanged)
                || ChangeTracker.Entries<TimelineFactAddition>().Any(x => x.State != EntityState.Unchanged)
                || ChangeTracker.Entries<TimelineFactInvalidation>().Any(x => x.State != EntityState.Unchanged)
                || change.Properties.Any(p => p.IsModified && p.Metadata.Name is not
                    ("State" or "LeaseOwner" or "LeaseExpiresAtUtc" or "ExtractionFeedbackJson" or "LastErrorCode" or "NextAttemptAtUtc" or "UpdatedAtUtc"))
                || await TimelineFacts.AnyAsync(x => x.EntryId == source.Id, ct)
                || await TimelineFactProposals.AnyAsync(x => x.EntryId == source.Id && x.Status != "Superseded", ct)
                || await TimelineFactJobs.AnyAsync(x => x.EntryId == source.Id && x.Kind == "Extraction" && x.Id != job.Id && x.State != "Superseded", ct))
                throw new TimelineFactConflictException("Feedback lease, source, revision or author changed.");
            var snapshot = await ReadExtractionInputAsync(stored, source, ct);
            var feedback = TimelineExtractionFeedbackCodec.Decode(staged);
            TimelineExtractionFeedbackCodec.Validate(feedback, stored, source, snapshot.Input);
            if (job.State != (feedback.Disposition == "NeedsClarification" ? "AwaitingClarification" : "Failed"))
                throw new TimelineFactConflictException("Feedback disposition changed.");
            var questions = ChangeTracker.Entries<TimelineEntryClarification>().Where(x => x.State == EntityState.Added).Select(x => x.Entity).ToArray();
            if (questions.Length != feedback.Questions.Count || feedback.Questions.Any(q => !questions.Any(x => x.GenerationId == job.Id
                && x.QuestionKey == TimelineExtractionFeedbackCodec.QuestionKey(job.Id, q.Key) && x.Question == q.Text)))
                throw new TimelineFactConflictException("Feedback questions must commit atomically.");
            timeline.FactRevision = checked(Entry(timeline).Property(x => x.FactRevision).OriginalValue + 1);
        }
    }
}
