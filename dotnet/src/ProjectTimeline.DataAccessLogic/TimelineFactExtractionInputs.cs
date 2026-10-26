using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace ProjectTimeline.DataAccessLogic;

public sealed record TimelineExtractionSource(Guid Id, Guid TimelineId, int EntryNumber, Guid AuthorId, string Title, string Content)
{
    public static TimelineExtractionSource From(TimelineEntry entry) => new(entry.Id, entry.TimelineId, entry.EntryNumber, entry.AuthorId, entry.Title, entry.Content);
    public TimelineEntry ToEntry() => new() { Id = Id, TimelineId = TimelineId, EntryNumber = EntryNumber, AuthorId = AuthorId, Title = Title, Content = Content };
}
public sealed record TimelineExtractionSnapshot(TimelineExtractionSource Source, TimelineEntryExtractionInput Input, Guid? PredecessorId);
public sealed record TimelineExtractionSpan(string Id, string Source, int Start, int Length, string Text, string SourceKind = "Entry", Guid? AnswerId = null);
public sealed record TimelineExtractionSupport(int CandidateIndex, string PrimarySpanId, IReadOnlyList<TimelineExtractionSpan> Spans, Guid? AdditionId = null);
public sealed record TimelineExtractionReviewReference(Guid? GenerationId, Guid? AnswerId, string Question, string? Answer);

// Shared literal codec. Neither the database guards nor the provider trust model-written quotes or IDs.
public static class TimelineExtractionEvidence
{
    public static IReadOnlyList<TimelineExtractionSpan> Create(string title, string content, Guid entryId, TimelineEntryExtractionInput? input)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(title.Length);
            foreach (var character in title) writer.Write((ushort)character);
            writer.Write(content.Length);
            foreach (var character in content) writer.Write((ushort)character);
        }
        var fingerprint = Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
        if (input is not null) fingerprint += $":{entryId:N}:{input.JobId:N}:{input.EffectiveFingerprint}";
        var spans = new List<TimelineExtractionSpan>();
        Add(title, "Title", "T"); Add(content, "Content", "C");
        if (input is not null)
            foreach (var answer in input.Answers) Add(answer.Answer, "Answer", $"A:{answer.Id:N}", answer.Id);
        return spans.AsReadOnly();
        void Add(string text, string field, string prefix, Guid? answerId = null)
        {
            for (var start = 0; start < text.Length;)
            {
                var length = Math.Min(256, text.Length - start);
                if (start + length < text.Length && char.IsHighSurrogate(text[start + length - 1]) && char.IsLowSurrogate(text[start + length])) length--;
                spans.Add(new($"{fingerprint}:{prefix}:{start.ToString(CultureInfo.InvariantCulture)}", field, start, length,
                    text.Substring(start, length), answerId is null ? "Entry" : "Clarification", answerId));
                start += length;
            }
        }
    }
    public static TimelineExtractionSpan Primary(IEnumerable<TimelineExtractionSpan> spans) => spans
        .OrderBy(x => x.SourceKind == "Clarification" ? 0 : 1).ThenBy(x => x.Source, StringComparer.Ordinal)
        .ThenBy(x => x.AnswerId).ThenBy(x => x.Start).First();
    public static string Reconstruct(string title, string content, TimelineEntryExtractionInput? input,
        IReadOnlyList<TimelineExtractionSpan> spans, IReadOnlyList<string> ids)
    {
        if (ids is null || ids.Count is < 1 or > 81 || ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
            throw new TimelineFactConflictException("Invalid supporting evidence.");
        var lookup = spans.ToDictionary(x => x.Id, StringComparer.Ordinal);
        if (ids.Any(id => !lookup.ContainsKey(id))) throw new TimelineFactConflictException("Unknown supporting evidence.");
        var selected = ids.Select(id => lookup[id]).ToArray();
        if (input?.Answers.Count is not > 0 && selected.Any(x => x.Source != selected[0].Source))
            throw new TimelineFactConflictException("Mixed legacy evidence.");
        var primary = Primary(selected);
        var original = primary.Source switch
        {
            "Title" => title, "Content" => content,
            "Answer" => input!.Answers.Single(x => x.Id == primary.AnswerId).Answer,
            _ => throw new TimelineFactConflictException("Invalid source field.")
        };
        selected = selected.Where(x => x.Source == primary.Source && x.AnswerId == primary.AnswerId).ToArray();
        var start = selected.Min(x => x.Start);
        return original.Substring(start, selected.Max(x => x.Start + x.Length) - start);
    }

    public static void Validate(TimelineEntry source, TimelineEntryExtractionInput input, IReadOnlyList<TimelineFact> candidates,
        IReadOnlyList<TimelineExtractionSupport> evidence)
    {
        if (evidence is null || evidence.Count > 200 || evidence.Any(x => x is null)
            || evidence.Select(x => x.CandidateIndex).Distinct().Count() != evidence.Count
            || evidence.Any(x => x.CandidateIndex < 0 || x.CandidateIndex >= candidates.Count)
            || input.Answers.Count > 0 && evidence.Count != candidates.Count)
            throw new TimelineFactConflictException("Invalid candidate support envelope.");
        var spans = Create(source.Title, source.Content, source.Id, input).ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (var support in evidence)
        {
            if (support.Spans is null || support.Spans.Count is < 1 or > 81 || support.Spans.Any(x => x is null
                || string.IsNullOrWhiteSpace(x.Id) || !spans.TryGetValue(x.Id, out var actual) || actual != x))
                throw new TimelineFactConflictException("Changed supporting evidence.");
            var quote = Reconstruct(source.Title, source.Content, input, spans.Values.ToArray(), support.Spans.Select(x => x.Id).ToArray());
            var primary = Primary(support.Spans);
            var fact = candidates[support.CandidateIndex];
            if (support.PrimarySpanId != primary.Id || fact.SourceKind != primary.SourceKind || fact.SourceQuote != quote)
                throw new TimelineFactConflictException("Candidate literal provenance changed.");
        }
        foreach (var fact in candidates)
            if (!TimelineEntryClarificationEvidence.Text(fact.Claim, 4000) || !TimelineEntryClarificationEvidence.Text(fact.EntityKey, 300)
                || !TimelineEntryClarificationEvidence.Text(fact.EntityType, 100) || !TimelineEntryClarificationEvidence.Text(fact.SourceQuote, 20000)
                || fact.SourceKind is not ("Entry" or "Clarification")
                || fact.SourceKind == "Entry" && !(source.Title.Contains(fact.SourceQuote, StringComparison.Ordinal) || source.Content.Contains(fact.SourceQuote, StringComparison.Ordinal))
                || fact.SourceKind == "Clarification" && !input.Answers.Any(x => x.Answer.Contains(fact.SourceQuote, StringComparison.Ordinal)))
                throw new TimelineFactConflictException("Invalid grounded candidate.");
    }
}

public sealed partial class ProjectTimelineDbContext
{
    private readonly HashSet<Guid> stagedExtractionInputs = [];

    // Reconciliation reads the complete durable ledger, never prompt order or client-supplied support.
    public async Task<(TimelineExtractionSnapshot? Snapshot, List<TimelineFactAddition> Additions)> ReadReconciliationEvidenceAsync(
        TimelineEntry source, Guid generationId, CancellationToken ct = default)
    {
        var fingerprint = TimelineFactEvidence.Fingerprint(source);
        var proposal = await TimelineFactProposals.AsNoTracking().SingleOrDefaultAsync(x => x.Id == generationId, ct);
        var job = await TimelineFactJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == generationId, ct);
        if (proposal is null || proposal.EntryId != source.Id || proposal.Status != "Generated" || proposal.SourceFingerprint != fingerprint
            || job is null || job.EntryId != source.Id || job.Kind != "Extraction" || job.State != "Completed"
            || !TimelineFactEvidence.ExtractionKey(job) || job.FactId is not null || job.SourceFingerprint != fingerprint
            || job.ExtractionRevision is null || job.ExtractionResultHash is null || job.LeaseOwner is not null || job.LeaseExpiresAtUtc is not null
            || proposal.ExpectedRevision != job.ExtractionRevision + 1 || proposal.QuestionsJson != "[]" || proposal.ClarificationEvidence != ""
            || await TimelineFactJobs.AnyAsync(x => x.EntryId == source.Id && x.Kind == "Extraction" && x.Id != job.Id && x.State != "Superseded", ct)
            || await TimelineFactInvalidations.AnyAsync(x => x.ProposalId == proposal.Id, ct))
            throw InvalidReconciliation();
        var additions = await TimelineFactAdditions.AsNoTracking().Where(x => x.ProposalId == proposal.Id).OrderBy(x => x.Id).ToListAsync(ct);
        var facts = additions.Select(TimelineFactEvidence.CandidateEvidence).ToArray();
        if (additions.Count > 200 || facts.Select(TimelineFactEvidence.CandidateKey).Distinct().Count() != additions.Count
            || TimelineFactEvidence.ResultHash(facts) != job.ExtractionResultHash) throw InvalidReconciliation();
        if (job.ExtractionInputJson is null && (job.EffectiveInputFingerprint is not null || job.ExtractionSupportJson is not null))
            throw InvalidReconciliation();
        var snapshot = job.ExtractionInputJson is null ? null : await ReadExtractionInputAsync(job, source, ct);
        if (snapshot?.Input.Answers.Count > 0)
        {
            TimelineExtractionSupport[] support;
            try { support = JsonSerializer.Deserialize<TimelineExtractionSupport[]>(job.ExtractionSupportJson ?? "null") ?? throw new JsonException(); }
            catch (JsonException) { throw InvalidReconciliation(); }
            if (support.Length != additions.Count || support.Any(x => x is null || x.AdditionId is null)
                || support.Select(x => x.AdditionId).Distinct().Count() != support.Length
                || !support.Select(x => x.AdditionId!.Value).ToHashSet().SetEquals(additions.Select(x => x.Id))
                || !support.Select(x => x.CandidateIndex).Order().SequenceEqual(Enumerable.Range(0, additions.Count)))
                throw InvalidReconciliation();
            // CandidateIndex is local to the original envelope. Resolve durable AdditionId first.
            var byId = additions.ToDictionary(x => x.Id);
            var ordered = support.OrderBy(x => x.CandidateIndex).ToArray();
            TimelineExtractionEvidence.Validate(source, snapshot.Input,
                ordered.Select(x => TimelineFactEvidence.CandidateEvidence(byId[x.AdditionId!.Value])).ToArray(), ordered);
        }
        else if (additions.Any(x => x.SourceKind != "Entry")) throw InvalidReconciliation();
        return (snapshot, additions);
    }

    public async Task ValidateExtractionReviewReferencesAsync(TimelineEntry source,
        IEnumerable<TimelineExtractionReviewReference> references, CancellationToken ct = default)
    {
        var imported = references.Where(x => x.GenerationId is not null || x.AnswerId is not null).ToArray();
        if (imported.Any(x => x.GenerationId is null || x.AnswerId is null)
            || imported.Select(x => x.AnswerId).Distinct().Count() != imported.Length
            || imported.Select(x => x.GenerationId).Distinct().Count() > 1) throw InvalidReconciliation();
        foreach (var group in imported.GroupBy(x => x.GenerationId!.Value))
        {
            var (snapshot, _) = await ReadReconciliationEvidenceAsync(source, group.Key, ct);
            if (snapshot is null) throw InvalidReconciliation();
            foreach (var reference in group)
                if (!snapshot.Input.Answers.Any(x => x.Id == reference.AnswerId && x.Question == reference.Question && x.Answer == reference.Answer))
                    throw InvalidReconciliation();
        }
    }

    private static TimelineFactConflictException InvalidReconciliation() => new("Generated extraction evidence is invalid.");

    // Server-only: no replacement input/answers accepted. Capture BEFORE predecessor supersession.
    public async Task StageExtractionInputAsync(TimelineFactJob job, TimelineEntry source, Guid? predecessorId = null, CancellationToken ct = default)
    {
        if (job.ExtractionInputJson is not null || job.EffectiveInputFingerprint is not null || job.Kind != "Extraction" || job.EntryId != source.Id
            || predecessorId is null && await TimelineEntryClarifications.AnyAsync(x => x.GenerationId == job.Id, ct))
            throw new TimelineFactConflictException("Input is already frozen or foreign.");
        var answers = new List<TimelineEntryAnswer>();
        if (predecessorId is { } id)
        {
            var predecessor = await TimelineFactJobs.SingleAsync(x => x.Id == id, ct);
            if (predecessor.EntryId != source.Id || predecessor.State == "Superseded" || predecessor.SourceFingerprint != TimelineFactEvidence.Fingerprint(source))
                throw new TimelineFactConflictException("Predecessor is no longer applicable.");
            if (predecessor.ExtractionInputJson is not null)
                answers.AddRange((await ReadExtractionInputAsync(predecessor, source, ct)).Input.Answers);
            var questions = await TimelineEntryClarifications.AsNoTracking().Where(x => x.GenerationId == id).ToListAsync(ct);
            if (questions.Any(x => x.Answer is null) || predecessor.State == "AwaitingClarification" && questions.Count == 0)
                throw new TimelineFactConflictException("Required extraction questions are unanswered.");
            foreach (var row in questions) answers.Add(ActualAnswer(row));
        }
        var input = TimelineEntryClarificationEvidence.BindToJob(job, source, answers);
        job.ExtractionInputJson = JsonSerializer.Serialize(new TimelineExtractionSnapshot(TimelineExtractionSource.From(source), input, predecessorId));
        job.EffectiveInputFingerprint = input.EffectiveFingerprint;
        stagedExtractionInputs.Add(job.Id);
    }

    private static TimelineExtractionSnapshot DecodeExtractionInput(TimelineFactJob job, TimelineEntry source)
    {
        TimelineExtractionSnapshot snapshot;
        try
        {
            if (job.ExtractionInputJson is null || job.ExtractionInputJson.Length > 1_000_000) throw new JsonException();
            snapshot = JsonSerializer.Deserialize<TimelineExtractionSnapshot>(job.ExtractionInputJson) ?? throw new JsonException();
            if (snapshot.Source is null || snapshot.Input is null || snapshot.Input.Answers is null
                || snapshot.Source != TimelineExtractionSource.From(source) || snapshot.Input.JobId != job.Id)
                throw new JsonException();
            var answers = TimelineEntryClarificationEvidence.Snapshot(source, snapshot.Input.Answers);
            var effective = TimelineEntryClarificationEvidence.EffectiveFingerprint(source, answers);
            if (job.Kind != "Extraction" || job.EntryId != source.Id || job.SourceFingerprint != TimelineFactEvidence.Fingerprint(source)
                || job.SourceFingerprint != snapshot.Input.SourceFingerprint || effective != snapshot.Input.EffectiveFingerprint
                || job.EffectiveInputFingerprint != effective || !snapshot.Input.Answers.SequenceEqual(answers)
                || JsonSerializer.Serialize(snapshot) != job.ExtractionInputJson)
                throw new JsonException();
        }
        catch (JsonException) { throw new TimelineFactConflictException("Invalid durable extraction input."); }
        return snapshot;
    }

    public async Task<TimelineExtractionSnapshot> ReadExtractionInputAsync(TimelineFactJob job, TimelineEntry source, CancellationToken ct = default)
    {
        if (job.State == "Superseded") throw new TimelineFactConflictException("Input was revoked.");
        var snapshot = DecodeExtractionInput(job, source);
        if (snapshot.PredecessorId is { } predecessorId)
        {
            var predecessor = await TimelineFactJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == predecessorId, ct);
            if (predecessor is null || predecessor.Id == job.Id || predecessor.Kind != "Extraction"
                || predecessor.EntryId != source.Id || predecessor.SourceFingerprint != job.SourceFingerprint
                || predecessor.ExtractionInputJson is null && predecessor.EffectiveInputFingerprint is not null)
                throw new TimelineFactConflictException("Invalid predecessor identity.");
            if (predecessor.ExtractionInputJson is not null) DecodeExtractionInput(predecessor, source);
        }
        foreach (var answer in snapshot.Input.Answers)
        {
            var row = await TimelineEntryClarifications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == answer.Id, ct);
            if (row is null || ActualAnswer(row) != answer || !await ClarificationWriterAsync(
                await Timelines.Where(x => x.Id == source.TimelineId).Select(x => x.ProjectId).SingleAsync(ct), answer.AnsweredById, ct))
                throw new TimelineFactConflictException("Actual answer provenance changed.");
            // Every carry edge was captured while its predecessor was current. Never use a hash match to revive history.
            var next = snapshot.PredecessorId;
            var seen = new HashSet<Guid> { job.Id };
            while (true)
            {
                if (next is not { } ancestorId || !seen.Add(ancestorId)) throw new TimelineFactConflictException("Missing answer lineage.");
                var ancestor = await TimelineFactJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == ancestorId, ct);
                if (ancestor is null || ancestor.Kind != "Extraction" || ancestor.EntryId != source.Id || ancestor.SourceFingerprint != job.SourceFingerprint)
                    throw new TimelineFactConflictException("Foreign answer lineage.");
                var carried = ancestor.ExtractionInputJson is null ? null : DecodeExtractionInput(ancestor, source);
                if (ancestorId == answer.GenerationId)
                {
                    if (ancestor.State is not ("AwaitingClarification" or "Superseded"))
                        throw new TimelineFactConflictException("Answer generation has no waiting provenance.");
                    break;
                }
                if (carried is null || !carried.Input.Answers.Contains(answer))
                    throw new TimelineFactConflictException("Answer was not carried by predecessor.");
                next = carried.PredecessorId;
            }
        }
        return snapshot;
    }
    private static TimelineEntryAnswer ActualAnswer(TimelineEntryClarification row)
    {
        if (row.Origin != "Extraction" || row.Answer is null || row.AnsweredById is null || row.AnsweredAtUtc is null)
            throw new TimelineFactConflictException("Actual answer provenance missing.");
        return new(row.Id, row.EntryId, row.GenerationId, row.SourceFingerprint, row.QuestionKey, row.Question,
            row.Answer, row.AnsweredById.Value, row.AnsweredAtUtc.Value);
    }
}
