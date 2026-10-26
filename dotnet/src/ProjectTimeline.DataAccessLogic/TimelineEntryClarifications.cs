using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProjectTimeline.DataAccessLogic;

// Entry evidence is separate from review-owned ClarificationEvidence. All text is untrusted data.
public sealed class TimelineEntryClarification : AuditedEntity
{
    public Guid EntryId { get; set; }
    public Guid GenerationId { get; set; }
    public string SourceFingerprint { get; set; } = "";
    public string QuestionKey { get; set; } = "";
    public string Question { get; set; } = "";
    public string Origin { get; set; } = "Extraction";
    public string? Answer { get; set; }
    public Guid? AnsweredById { get; set; }
    public DateTime? AnsweredAtUtc { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
}

public sealed record TimelineEntryQuestion(string Key, string Text);
public sealed record TimelineEntryAnswer(Guid Id, Guid EntryId, Guid GenerationId, string SourceFingerprint,
    string QuestionKey, string Question, string Answer, Guid AnsweredById, DateTime AnsweredAtUtc);

public sealed record TimelineEntryExtractionInput(Guid JobId, string SourceFingerprint,
    string EffectiveFingerprint, IReadOnlyList<TimelineEntryAnswer> Answers);

public static class TimelineEntryClarificationEvidence
{
    public const int MaxQuestions = 20;
    public static bool Text(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;

    public static IReadOnlyList<TimelineEntryAnswer> Snapshot(TimelineEntry entry, IEnumerable<TimelineEntryAnswer> answers)
    {
        var bounded = answers.Take(MaxQuestions + 1).ToArray();
        if (bounded.Any(x => x is null)) throw new TimelineFactConflictException("Null clarification evidence.");
        var values = bounded.OrderBy(x => x.QuestionKey, StringComparer.Ordinal).ThenBy(x => x.Id).ToArray();
        if (values.Length > MaxQuestions || values.Select(x => x.Id).Distinct().Count() != values.Length
            || values.Select(x => x.QuestionKey).Distinct(StringComparer.Ordinal).Count() != values.Length
            || values.Any(x => x.EntryId != entry.Id || x.GenerationId == Guid.Empty || x.Id == Guid.Empty
                || x.SourceFingerprint != TimelineFactEvidence.Fingerprint(entry) || !Text(x.QuestionKey, 100)
                || !Text(x.Question, 2000) || !Text(x.Answer, 4000) || x.AnsweredById == Guid.Empty
                || x.AnsweredAtUtc.Kind != DateTimeKind.Utc || x.AnsweredAtUtc == default))
            throw new TimelineFactConflictException("Malformed or foreign clarification snapshot.");
        return Array.AsReadOnly(values);
    }

    // Freeze once per lease; C2 must persist this input identity and use the same answers for extraction AND verification.
    public static TimelineEntryExtractionInput BindToJob(TimelineFactJob job, TimelineEntry entry, IEnumerable<TimelineEntryAnswer> answers)
    {
        if (job.EntryId != entry.Id || job.Kind != "Extraction" || job.Id == Guid.Empty || job.State == "Superseded"
            || job.SourceFingerprint != TimelineFactEvidence.Fingerprint(entry))
            throw new TimelineFactConflictException("Extraction input does not belong to this job/source.");
        var snapshot = Snapshot(entry, answers);
        return new(job.Id, job.SourceFingerprint, EffectiveFingerprint(entry, snapshot), snapshot);
    }

    public static string EffectiveFingerprint(TimelineEntry entry, IEnumerable<TimelineEntryAnswer> answers)
    {
        var snapshot = Snapshot(entry, answers);
        var source = TimelineFactEvidence.Fingerprint(entry);
        return snapshot.Count == 0 ? source : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { Source = source, Answers = snapshot }))));
    }
}
