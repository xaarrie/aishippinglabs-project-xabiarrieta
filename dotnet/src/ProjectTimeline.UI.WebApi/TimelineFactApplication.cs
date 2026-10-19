using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.DataAccessLogic;

namespace ProjectTimeline.UI.WebApi;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TimelineFactPlanRequest(Guid? PreviousProposalId, IReadOnlyList<TimelineFactAnswer>? Answers);
public sealed record TimelineFactInvalidationDto(Guid FactId, string Claim, string SourceQuote, string SourceKind, string Reason,
    Guid? InvalidatingAdditionId = null, int? InvalidatingAdditionIndex = null, string? InvalidatingClaim = null);
public sealed record TimelineFactProposalDto(Guid? ProposalId, string State, string Status, bool Ready,
    IReadOnlyList<TimelineFactCandidate> Additions, IReadOnlyList<TimelineFactInvalidationDto> Invalidations,
    IReadOnlyList<TimelineFactQuestion> Questions, TimelineFactCoverage Coverage, IReadOnlyList<string> Warnings);
public sealed record TimelineFactDto(Guid Id, Guid EntryId, string Claim, string SourceQuote, string SourceKind,
    bool Valid, Guid? CreationReviewId, Guid? InvalidationReviewId, Guid? ExtractionJobId = null, Guid? InvalidatedByFactId = null);
public sealed record TimelineFactJobDto(Guid Id, string Kind, string State, int Attempts, DateTime? NextAttemptAtUtc, string? ErrorCode);
public sealed record TimelineFactStateDto(IReadOnlyList<TimelineFactDto> Facts, IReadOnlyList<TimelineFactProposalDto> Proposals,
    IReadOnlyList<TimelineFactJobDto> Processing, TimelineFactCoverage Coverage, bool Truncated);

/// <summary>Only authenticated server identities enter here. Agent construction is lazy and follows write authorization.</summary>
public sealed class TimelineFactApplication(ProjectTimelineDbContext db, Func<ICustomAgent> createAgent)
{
    public async Task<TimelineFactProposalDto> PlanAsync(Guid entryId, Guid actor, bool admin,
        TimelineFactPlanRequest request, CancellationToken ct = default)
    {
        var scope = await ScopeAsync(entryId, actor, admin, true, ct);
        if (request.Answers is { Count: > 100 } || request.Answers?.Any(x => x is null || string.IsNullOrWhiteSpace(x.Answer) || x.Answer.Length > 4000) == true)
            throw new ArgumentException("At most 100 bounded, nonempty answers are allowed.");
        TimelineFactReviewPlan plan;
        try
        {
            plan = await new TimelineFactReviewPlanner(db, scope, actor, admin,
                new TimelineFactOptimizationAgent(CreateAgentSafely())).PlanAsync(request.PreviousProposalId, request.Answers, ct);
        }
        catch (Exception ex) when (ex is not (UnauthorizedAccessException or KeyNotFoundException or TimelineFactConflictException or OperationCanceledException))
        { throw new TimelineFactProviderException("Unavailable"); }
        var source = await db.TimelineEntries.AsNoTracking().SingleAsync(x => x.Id == entryId, ct);
        if (TimelineFactEvidence.Fingerprint(source) != plan.SourceFingerprint)
            throw new TimelineFactConflictException("Source changed; regenerate.");
        if (plan.Draft.Additions.Count + plan.Draft.Invalidations.Count + plan.Draft.Questions.Count == 0)
            return new(null, "NoChanges", plan.Status, false, [], [], [], plan.Coverage, Warnings(plan.Coverage));
        var proposal = await new TimelineFactService(db).PrepareAsync(scope, actor, admin, plan.ExpectedRevision, plan.Draft, ct);
        return await ProposalAsync(proposal, plan.Coverage, plan.Status, ct);
    }

    public async Task<TimelineFactProposalDto> AcceptAsync(Guid entryId, Guid proposalId, Guid actor, bool admin, CancellationToken ct = default)
    {
        var scope = await ScopeAsync(entryId, actor, admin, true, ct);
        var accepted = await new TimelineFactService(db).AcceptAsync(scope, proposalId, actor, admin, ct);
        return await ProposalAsync(accepted, await new TimelineFactSearch(db, scope, actor, admin).CoverageAsync(ct), "Accepted", ct);
    }

    public async Task<TimelineFactStateDto> ReadAsync(Guid entryId, Guid actor, bool admin, CancellationToken ct = default)
    {
        var scope = await ScopeAsync(entryId, actor, admin, false, ct);
        var coverage = await new TimelineFactSearch(db, scope, actor, admin).CoverageAsync(ct);
        var facts = await db.TimelineFacts.AsNoTracking().Where(x => x.EntryId == entryId).OrderBy(x => x.Id).Take(501)
            .Select(x => new TimelineFactDto(x.Id, x.EntryId, x.Claim, x.SourceQuote, x.SourceKind, x.Valid, x.CreationReviewId, x.InvalidationReviewId, x.ExtractionJobId, x.InvalidatedByFactId)).ToListAsync(ct);
        var proposals = await db.TimelineFactProposals.AsNoTracking().Where(x => x.EntryId == entryId).OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).Take(51).ToListAsync(ct);
        var jobs = await db.TimelineFactJobs.AsNoTracking().Where(x => x.EntryId == entryId).OrderBy(x => x.Id).Take(101)
            .Select(x => new TimelineFactJobDto(x.Id, x.Kind, x.State, x.Attempts, x.NextAttemptAtUtc,
                x.LastErrorCode == null ? null : x.LastErrorCode == "Timeout" || x.LastErrorCode == "RateLimited" || x.LastErrorCode == "InvalidResponse" || x.LastErrorCode == "Cancelled" ? x.LastErrorCode : "Unavailable")).ToListAsync(ct);
        var dtos = new List<TimelineFactProposalDto>();
        foreach (var proposal in proposals.Take(50)) dtos.Add(await ProposalAsync(proposal, coverage, proposal.Status, ct));
        return new(facts.Take(500).ToArray(), dtos, jobs.Take(100).ToArray(), coverage, facts.Count > 500 || proposals.Count > 50 || jobs.Count > 100);
    }

    public async Task<TimelineFactScope> ScopeAsync(Guid entryId, Guid actor, bool admin, bool write, CancellationToken ct = default)
    {
        if (!await db.Users.AnyAsync(x => x.Id == actor && x.IsActive, ct)) throw new UnauthorizedAccessException();
        var entry = await db.TimelineEntries.AsNoTracking().Where(x => x.Id == entryId)
            .Select(x => new { x.TimelineId, x.Timeline.ProjectId }).SingleOrDefaultAsync(ct) ?? throw new KeyNotFoundException();
        var auth = new ProjectAuthorization(db);
        if (!(write ? await auth.CanWriteAsync(entry.ProjectId, actor, admin, ct) : await auth.CanReadAsync(entry.ProjectId, actor, admin, ct)))
            throw new UnauthorizedAccessException();
        return new(entry.ProjectId, entry.TimelineId, entryId);
    }

    private async Task<TimelineFactProposalDto> ProposalAsync(TimelineFactProposal proposal, TimelineFactCoverage coverage, string status, CancellationToken ct)
    {
        var storedAdditions = await db.TimelineFactAdditions.AsNoTracking().Where(x => x.ProposalId == proposal.Id).OrderBy(x => x.Id).ToListAsync(ct);
        var additions = storedAdditions.Select(x => new TimelineFactCandidate(x.Claim, x.EntityKey, x.EntityType, x.SourceQuote, x.SourceKind)).ToArray();
        var storedInvalidations = await (from i in db.TimelineFactInvalidations.AsNoTracking()
                                        join f in db.TimelineFacts on i.FactId equals f.Id
                                        where i.ProposalId == proposal.Id orderby i.Id
                                        select new { f.Id, f.Claim, f.SourceQuote, f.SourceKind, i.Reason, i.InvalidatingAdditionId }).ToListAsync(ct);
        var invalidations = storedInvalidations.Select(x =>
        {
            // As in planner clarification remapping, resolve the durable ID against the returned
            // addition order. The original draft index is not the persisted/ordered index.
            var index = x.InvalidatingAdditionId is Guid id ? storedAdditions.FindIndex(a => a.Id == id) : -1;
            return new TimelineFactInvalidationDto(x.Id, x.Claim, x.SourceQuote, x.SourceKind, x.Reason,
                x.InvalidatingAdditionId, index < 0 ? null : index, index < 0 ? null : storedAdditions[index].Claim);
        }).ToArray();
        var questions = JsonSerializer.Deserialize<TimelineFactQuestion[]>(proposal.QuestionsJson) ?? [];
        var currentRevision = await db.TimelineEntries.Where(x => x.Id == proposal.EntryId).Select(x => x.Timeline.FactRevision).SingleAsync(ct);
        var stale = proposal.Status == "Pending" && proposal.ExpectedRevision != currentRevision;
        return new(proposal.Id, proposal.Status, stale ? "Stale:Regenerate" : status,
            proposal.Status == "Pending" && !stale && additions.Length + invalidations.Length > 0 && questions.All(x => !string.IsNullOrWhiteSpace(x.Answer)),
            additions, invalidations, questions, coverage, Warnings(coverage));
    }

    private ICustomAgent CreateAgentSafely()
    {
        try { return createAgent(); }
        catch { throw new TimelineFactProviderException("Unavailable"); }
    }

    private static string[] Warnings(TimelineFactCoverage coverage) =>
        [.. (coverage.EntriesWithoutCompletedExtraction > 0 || coverage.CurrentExtractionState != "Completed"
            ? new[] { "Incomplete history: extraction/backfill is absent, pending or failed. No claim of exhaustive conflict detection." } : Array.Empty<string>()),
         "Search is bounded lexical/entity recall, not exhaustive; candidate and result limits may truncate context."];
}
