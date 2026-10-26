using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.AgentModels;
using ProjectTimeline.BusinessLogic.Agents.Tools;
using ProjectTimeline.DataAccessLogic;

namespace ProjectTimeline.BusinessLogic;

public sealed record TimelineFactAnswer(int QuestionIndex, string Answer);
public sealed record TimelineFactReviewPlan(TimelineFactDraft Draft, long ExpectedRevision, string SourceFingerprint,
    TimelineFactCoverage Coverage, string Status, Guid? PreviousProposalId);

/// <summary>
/// T3 constructs this with a server-authorized scope/identity and a scoped clean DbContext. PlanAsync never saves.
/// Clarification input is a persisted proposal ID plus actual user answers indexed into its stored QuestionsJson;
/// never accept a client-supplied previous draft. Persist only this validated Draft via TimelineFactService.PrepareAsync
/// with ExpectedRevision, after checking SourceFingerprint still matches the persisted entry. PrepareAsync reauthorizes
/// writes and reserves a new revision. No automatic full-history fallback, extraction, embeddings or backfill occurs.
/// </summary>
public sealed class TimelineFactReviewPlanner(ProjectTimelineDbContext db, TimelineFactScope scope, Guid actorId,
    bool administrator, TimelineFactOptimizationAgent? optimizationAgent = null)
{
    private readonly TimelineFactSearch search = new(db, scope, actorId, administrator);
    private readonly TimelineFactOptimizationAgent agent = optimizationAgent ?? new();

    public async Task<TimelineFactReviewPlan> PlanAsync(Guid? previousProposalId = null,
        IReadOnlyList<TimelineFactAnswer>? answers = null, CancellationToken ct = default)
    {
        if (db.ChangeTracker.HasChanges()) throw new InvalidOperationException("Planning requires a clean context.");
        var source = await search.AuthorizeAsync(ct);
        var fingerprint = TimelineFactEvidence.Fingerprint(source);
        var revision = await RevisionAsync(ct);
        var coverage = await search.CoverageAsync(ct);
        var (previous, questions) = await PreviousAsync(previousProposalId, answers ?? [], fingerprint, revision, ct);
        var (candidates, extractionQuestions) = await VerifiedCandidatesAsync(source, fingerprint, ct);
        foreach (var question in extractionQuestions)
        {
            var existing = questions.SingleOrDefault(x => x.ExtractionAnswerId == question.ExtractionAnswerId);
            if (existing is not null && existing != question) throw InvalidGeneration();
            if (existing is null) questions.Add(question);
        }
        await TimelineFactReconciliationEvidence.ValidateAsync(db, source, questions, ct);
        var terms = Seed(source, candidates);
        var seeds = await search.SearchAsync(terms, 50, ct);
        var tools = new TimelineFactReviewTools(search, ct, db);
        var contexts = await tools.fetch_fact_contexts(seeds.Select(x => x.Id).ToArray());
        var result = await agent.RunAsync(new
        {
            original_entry = new { source.Id, source.Title, source.Content },
            coverage,
            pending_extraction_candidates = candidates.Take(200),
            pending_candidates_truncated = candidates.Count > 200,
            previous_proposal = previous,
            user_answers = answers ?? [],
            clarification_evidence = questions,
            seed_terms = terms,
            seed_facts = seeds,
            seed_contexts = contexts
        }, tools, ct);
        // Bind assessments to the evidence the model actually selected, not to a later canonical citation.
        var originalAdditions = result.Additions?.ToArray() ?? [];
        ReuseCanonicalQuotes(result, candidates);
        var draft = await ValidateAsync(result, originalAdditions, source, questions, tools.DeliveredContexts, ct);
        var current = await search.AuthorizeAsync(ct);
        await VerifiedCandidatesAsync(current, fingerprint, ct);
        await TimelineFactReconciliationEvidence.ValidateAsync(db, current, draft.Questions, ct);
        if (await RevisionAsync(ct) != revision || TimelineFactEvidence.Fingerprint(current) != fingerprint)
            throw new TimelineFactConflictException("Timeline/source changed during planning; regenerate.");
        var status = draft.Questions.Any(x => string.IsNullOrWhiteSpace(x.Answer)) ? "NeedsClarification"
            : draft.Additions.Count + draft.Invalidations.Count == 0 ? "NoChangesProposed" : "DraftForReview";
        if (coverage.EntriesWithoutCompletedExtraction > 0 || coverage.CurrentExtractionState != "Completed")
            status = "IncompleteHistory:" + status;
        return new(draft, revision, fingerprint, coverage, status, previousProposalId);
    }

    private async Task<(List<TimelineFactCandidate> Candidates, List<TimelineFactQuestion> Questions)> VerifiedCandidatesAsync(TimelineEntry source, string fingerprint,
        CancellationToken ct)
    {
        var proposals = await db.TimelineFactProposals.AsNoTracking().Where(x => x.EntryId == source.Id
            && x.Status == "Generated" && x.SourceFingerprint == fingerprint).ToListAsync(ct);
        // Missing/stale extraction must not impose a new generation prerequisite on ordinary reviews.
        if (proposals.Count == 0) return ([], []);
        if (proposals.Count != 1) throw InvalidGeneration();
        var (snapshot, additions) = await db.ReadReconciliationEvidenceAsync(source, proposals[0].Id, ct);
        var questions = snapshot?.Input.Answers.Select(x => new TimelineFactQuestion(x.Question, x.Answer,
            snapshot.Input.JobId, x.Id)).ToList() ?? [];
        return (additions.Select(x => new TimelineFactCandidate(x.Claim, x.EntityKey, x.EntityType,
            x.SourceQuote, x.SourceKind)).ToList(), questions);
    }

    private static TimelineFactConflictException InvalidGeneration() => new("Generated extraction evidence is invalid.");

    private static void ReuseCanonicalQuotes(TimelineFactReviewResult result, IReadOnlyList<TimelineFactCandidate> candidates)
    {
        if (result.Additions is null || result.Additions.Count > 200) return; // Ordinary schema validation still owns rejection.
        for (var i = 0; i < result.Additions.Count; i++)
        {
            var addition = result.Additions[i];
            if (addition is null || addition.SourceKind is not ("Entry" or "Clarification")) continue;
            var matches = candidates.Where(x => x.SourceKind == addition.SourceKind
                && string.Equals(x.Claim, addition.Claim, StringComparison.Ordinal)
                && string.Equals(x.EntityKey, addition.EntityKey, StringComparison.Ordinal)
                && string.Equals(x.EntityType, addition.EntityType, StringComparison.Ordinal)).ToArray();
            if (matches.Length == 1) result.Additions[i] = addition with { SourceQuote = matches[0].SourceQuote };
        }
    }

    private Task<long> RevisionAsync(CancellationToken ct) => db.Timelines.AsNoTracking()
        .Where(x => x.Id == scope.TimelineId && x.ProjectId == scope.ProjectId).Select(x => x.FactRevision).SingleAsync(ct);

    private async Task<(object? Previous, List<TimelineFactQuestion> Questions)> PreviousAsync(Guid? id,
        IReadOnlyList<TimelineFactAnswer> answers, string fingerprint, long revision, CancellationToken ct)
    {
        if (answers.Count > 100 || (id is null && answers.Count != 0))
            throw new TimelineFactConflictException("Answers require a stored proposal.");
        if (id is null) return (null, []);
        var previous = await db.TimelineFactProposals.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id
            && x.EntryId == scope.EntryId && x.Status == "Pending", ct)
            ?? throw new TimelineFactConflictException("Previous pending proposal not found in this scope.");
        if (previous.SourceFingerprint != fingerprint || previous.ExpectedRevision != revision)
            throw new TimelineFactConflictException("Previous proposal is stale; regenerate.");
        var questions = (JsonSerializer.Deserialize<TimelineFactQuestion[]>(previous.QuestionsJson)
            ?? throw new TimelineFactConflictException("Stored questions missing.")).ToList();
        if (questions.Count > 100 || previous.QuestionsJson.Length > 20000
            || questions.Any(x => x is null)
            || questions.Where(x => x.ExtractionAnswerId is null).Select(x => x.Question).Distinct(StringComparer.Ordinal).Count()
                != questions.Count(x => x.ExtractionAnswerId is null))
            throw new TimelineFactConflictException("Stored questions are invalid or ambiguous.");
        await TimelineFactReconciliationEvidence.ValidateAsync(db,
            await search.AuthorizeAsync(ct), questions, ct);
        foreach (var question in questions)
        {
            Require(question.Question, 2000);
            if (question.Answer is not null) Require(question.Answer, 4000);
        }
        if (answers.Select(x => x.QuestionIndex).Distinct().Count() != answers.Count)
            throw new TimelineFactConflictException("Duplicate answer index.");
        foreach (var answer in answers)
        {
            if (answer.QuestionIndex < 0 || answer.QuestionIndex >= questions.Count)
                throw new TimelineFactConflictException("Unknown question index.");
            Require(answer.Answer, 4000);
            var old = questions[answer.QuestionIndex];
            if (old.Answer is not null && old.Answer != answer.Answer)
                throw new TimelineFactConflictException("Cannot replace stored clarification evidence.");
            questions[answer.QuestionIndex] = old with { Answer = answer.Answer };
        }
        var storedAdditions = await db.TimelineFactAdditions.AsNoTracking().Where(x => x.ProposalId == id)
            .OrderBy(x => x.Id).Take(201).ToListAsync(ct);
        var storedInvalidations = await db.TimelineFactInvalidations.AsNoTracking().Where(x => x.ProposalId == id)
            .OrderBy(x => x.Id).Take(201).ToListAsync(ct);
        if (storedAdditions.Count > 200 || storedInvalidations.Count > 200)
            throw new TimelineFactConflictException("Stored proposal exceeds bounds.");
        var additions = storedAdditions.Select(x => new TimelineFactCandidate(x.Claim, x.EntityKey, x.EntityType, x.SourceQuote, x.SourceKind)).ToArray();
        var invalidations = storedInvalidations.Select(x => new TimelineFactRetraction(x.FactId, x.Reason,
            x.InvalidatingAdditionId is null ? null : storedAdditions.FindIndex(a => a.Id == x.InvalidatingAdditionId))).ToArray();
        if (invalidations.Any(x => x.InvalidatingAdditionIndex == -1))
            throw new TimelineFactConflictException("Stored invalidation cause is outside its proposal.");
        return (new { previous.Id, additions, invalidations, questions }, questions);
    }

    private static string Seed(TimelineEntry source, IReadOnlyList<TimelineFactCandidate> candidates)
    {
        var text = string.Join(' ', candidates.Select(x => x.EntityKey).Take(12)) + " " + source.Title + " " + source.Content;
        var terms = string.Join(' ', Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}_-]{2,}")
            .Select(x => x.Value).Distinct().Take(12));
        return terms.Length <= 300 ? terms : terms[..300];
    }

    private async Task<TimelineFactDraft> ValidateAsync(TimelineFactReviewResult result,
        IReadOnlyList<TimelineFactCandidate> originalAdditions, TimelineEntry source,
        List<TimelineFactQuestion> questions, IReadOnlyDictionary<Guid, TimelineFactSourceContext> contexts, CancellationToken ct)
    {
        if (result.InvalidationAssessments is null || result.InvalidationAssessments.Count > 50
            || result.InvalidationAssessments.Any(x => x is null)
            || result.InvalidationAssessments.Count != result.Invalidations?.Count
            || result.InvalidationAssessments.Select(x => x.FactId).Distinct().Count() != result.InvalidationAssessments.Count)
            throw new TimelineFactConflictException("Each invalidation requires exactly one semantic assessment.");
        if (result.Additions is null || result.Invalidations is null || result.Questions is null
            || result.Additions.Count > 200 || result.Invalidations.Count > 50 || result.Questions.Count > 100)
            throw new TimelineFactConflictException("Fact review exceeds bounded schema.");
        var merged = questions.ToList(); // Stored questions and actual answers cannot be removed by the model.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var question in result.Questions)
        {
            if (question is null) throw new TimelineFactConflictException("Missing question.");
            Require(question.Question, 2000);
            if (!seen.Add(question.Question)) throw new TimelineFactConflictException("Duplicate question.");
            var trusted = questions.Where(x => x.Question == question.Question).ToArray();
            if ((question.ExtractionGenerationId is not null || question.ExtractionAnswerId is not null)
                && !trusted.Contains(question))
                throw new TimelineFactConflictException("Model supplied extraction provenance.");
            if (question.Answer is not null && !trusted.Any(x => x.Answer == question.Answer))
                throw new TimelineFactConflictException("Model supplied an answer without actual user evidence.");
            if (trusted.Length == 0) merged.Add(new(question.Question, null));
        }
        if (merged.Count > 100 || JsonSerializer.Serialize(merged).Length > 20000)
            throw new TimelineFactConflictException("Clarification evidence is too large.");
        foreach (var addition in result.Additions)
        {
            if (addition is null) throw new TimelineFactConflictException("Missing addition.");
            Require(addition.Claim, 4000); Require(addition.EntityKey, 300);
            Require(addition.EntityType, 100); Require(addition.SourceQuote, 20000);
            if (!QuoteSupported(addition, source, questions)) throw new TimelineFactConflictException("Addition quote is not in source or actual user answers.");
        }
        foreach (var invalidation in result.Invalidations)
        {
            if (invalidation is null) throw new TimelineFactConflictException("Missing invalidation.");
            Require(invalidation.Reason, 4000);
            if (invalidation.InvalidatingAdditionIndex is not int index || index < 0 || index >= result.Additions.Count)
                throw new TimelineFactConflictException("Invalidation requires an explicit replacement addition index in this response.");
        }
        var ids = result.Invalidations.Select(x => x.FactId).ToArray();
        if (ids.Distinct().Count() != ids.Length) throw new TimelineFactConflictException("Duplicate invalidation IDs.");
        var facts = await search.FetchAsync(ids, ct);
        if (facts.Count != ids.Length) throw new TimelineFactConflictException("Invalidation IDs are not in authorized effective scope.");
        var sourceIds = facts.Select(x => x.EntryId).Distinct().ToArray();
        var evidence = await db.TimelineEntries.AsNoTracking().Where(x => sourceIds.Contains(x.Id)).ToListAsync(ct);
        if (facts.Any(x => evidence.SingleOrDefault(e => e.Id == x.EntryId) is not { } entry
            || TimelineFactEvidence.Fingerprint(entry) != x.SourceFingerprint))
            throw new TimelineFactConflictException("Invalidation source fingerprint changed.");
        foreach (var invalidation in result.Invalidations)
        {
            var fact = facts.Single(x => x.Id == invalidation.FactId);
            var assessment = result.InvalidationAssessments.SingleOrDefault(x => x.FactId == fact.Id);
            var addition = originalAdditions[invalidation.InvalidatingAdditionIndex!.Value];
            Require(addition.SourceQuote, 20000);
            if (!QuoteSupported(addition, source, questions))
                throw new TimelineFactConflictException("Original replacement quote is not in source or actual user answers.");
            if (!contexts.TryGetValue(fact.Id, out var context) || context.Fact != fact
                || assessment is null || assessment.InvalidatingAdditionIndex != invalidation.InvalidatingAdditionIndex
                || assessment.HistoricalClaim != fact.Claim || assessment.HistoricalSourceFingerprint != fact.SourceFingerprint
                || assessment.ReplacementClaim != addition.Claim || assessment.ReplacementQuote != addition.SourceQuote
                || !assessment.SameScope || assessment.Relationship is not ("Contradiction" or "ExplicitSupersession"))
                throw new TimelineFactConflictException("Invalidation lacks a bound same-scope semantic assessment and delivered context.");
            Require(assessment.SharedScope, 2000); Require(assessment.Explanation, 4000);
            Require(assessment.HistoricalQuote, 20000);
            // Entry evidence and clarification evidence are distinct sources; never substitute one for the other.
            var historicalSupported = fact.SourceKind switch
            {
                "Entry" => context.Title.Contains(assessment.HistoricalQuote, StringComparison.Ordinal)
                    || context.Content.Contains(assessment.HistoricalQuote, StringComparison.Ordinal),
                "Clarification" => HistoricalAnswerContains(fact.ClarificationEvidence, assessment.HistoricalQuote),
                _ => false
            };
            if (!historicalSupported)
                throw new TimelineFactConflictException("Historical comparison quote is not in original context.");
        }
        return new(result.Additions.ToArray(), result.Invalidations.ToArray(), merged.ToArray());
    }

    private static bool QuoteSupported(TimelineFactCandidate addition, TimelineEntry source,
        IReadOnlyList<TimelineFactQuestion> questions) => addition.SourceKind switch
    {
        "Entry" => source.Title.Contains(addition.SourceQuote, StringComparison.Ordinal)
            || source.Content.Contains(addition.SourceQuote, StringComparison.Ordinal),
        "Clarification" => questions.Any(x => x.Answer?.Contains(addition.SourceQuote, StringComparison.Ordinal) == true),
        _ => false
    };

    private static bool HistoricalAnswerContains(string evidence, string quote)
    {
        try
        {
            return JsonSerializer.Deserialize<TimelineFactQuestion[]>(evidence)?
                .Any(x => x?.Answer?.Contains(quote, StringComparison.Ordinal) == true) == true;
        }
        catch (JsonException) { return false; }
    }

    private static void Require(string value, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max)
            throw new TimelineFactConflictException("Invalid fact review text.");
    }
}
