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
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TimelineFactGenerationRequest(bool Regenerate = false, Guid? ExpectedGenerationId = null);
public sealed record TimelineFactInvalidationDto(Guid FactId, string Claim, string SourceQuote, string SourceKind, string Reason,
    Guid? InvalidatingAdditionId = null, int? InvalidatingAdditionIndex = null, string? InvalidatingClaim = null);
public sealed record TimelineFactProposalDto(Guid? ProposalId, string State, string Status, bool Ready,
    IReadOnlyList<TimelineFactCandidate> Additions, IReadOnlyList<TimelineFactInvalidationDto> Invalidations,
    IReadOnlyList<TimelineFactQuestion> Questions, TimelineFactCoverage Coverage, IReadOnlyList<string> Warnings,
    string? SourceFingerprint = null);
public sealed record TimelineFactDto(Guid Id, Guid EntryId, string Claim, string SourceQuote, string SourceKind,
    bool Valid, Guid? CreationReviewId, Guid? InvalidationReviewId, Guid? ExtractionJobId = null, Guid? InvalidatedByFactId = null);
public sealed record TimelineFactJobDto(Guid Id, string Kind, string State, int Attempts, DateTime? NextAttemptAtUtc, string? ErrorCode);
public sealed record TimelineFactSourceDto(Guid EntryId, Guid TimelineId, string Title, string Content, string Status, string Fingerprint);
public sealed record TimelineFactStateDto(IReadOnlyList<TimelineFactDto> Facts, IReadOnlyList<TimelineFactProposalDto> Proposals,
    IReadOnlyList<TimelineFactJobDto> Processing, TimelineFactCoverage Coverage, bool Truncated,
    string LifecycleState = "NotGenerated", Guid? GenerationId = null, bool CanEdit = false, bool CanGenerate = false, bool CanRegenerate = false,
    TimelineFactSourceDto? Source = null, bool RefreshRequired = false);

/// <summary>Only authenticated server identities enter here. Agent construction is lazy and follows write authorization.</summary>
public sealed class TimelineFactApplication(ProjectTimelineDbContext db, Func<ICustomAgent> createAgent,
    TimelineFactProcessing? processing = null)
{
    public async Task<TimelineFactStateDto> GenerateAsync(Guid entryId, Guid actor, bool admin,
        TimelineFactGenerationRequest request, CancellationToken ct = default)
    {
        var scope = await ScopeAsync(entryId, actor, admin, true, ct);
        var worker = processing ?? throw new TimelineFactProviderException("Unavailable");
        var jobId = await worker.RequestGenerationAsync(scope, actor, admin, request.Regenerate, request.ExpectedGenerationId, ct);
        await worker.ProcessAsync(jobId, "fact-request:" + Guid.NewGuid().ToString("N"), ct);
        // Request owns the await and worker contexts; no fire-and-forget scoped DbContext work.
        return await ReadAsync(entryId, actor, admin, ct);
    }

    public async Task<TimelineFactProposalDto> PlanAsync(Guid entryId, Guid actor, bool admin,
        TimelineFactPlanRequest request, CancellationToken ct = default)
    {
        var scope = await ScopeAsync(entryId, actor, admin, true, ct);
        if (await db.TimelineFactProposals.AnyAsync(x => x.EntryId == entryId && x.Status == "Accepted", ct)
            || await db.TimelineFacts.AnyAsync(x => x.EntryId == entryId, ct))
            throw new TimelineFactConflictException("Accepted reconciliation locks this entry; corrections require a new entry.");
        if (!await (from job in db.TimelineFactJobs
                    join draft in db.TimelineFactProposals on job.Id equals draft.Id
                    where job.EntryId == entryId && job.Kind == "Extraction" && job.State == "Completed"
                        && draft.EntryId == entryId && draft.Status == "Generated" && draft.SourceFingerprint == job.SourceFingerprint
                    select job.Id).AnyAsync(ct))
            throw new TimelineFactConflictException("Generate current candidates before reconciliation.");
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
        var before = await ReadSourceAsync(scope, ct);
        var revision = await db.Timelines.AsNoTracking().Where(x => x.Id == scope.TimelineId).Select(x => x.FactRevision).SingleAsync(ct);
        var coverage = await new TimelineFactSearch(db, scope, actor, admin).CoverageAsync(ct);
        var facts = await db.TimelineFacts.AsNoTracking().Where(x => x.EntryId == entryId).OrderBy(x => x.Id).Take(501)
            .Select(x => new TimelineFactDto(x.Id, x.EntryId, x.Claim, x.SourceQuote, x.SourceKind, x.Valid, x.CreationReviewId, x.InvalidationReviewId, x.ExtractionJobId, x.InvalidatedByFactId)).ToListAsync(ct);
        var proposals = await db.TimelineFactProposals.AsNoTracking().Where(x => x.EntryId == entryId).OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).Take(51).ToListAsync(ct);
        var jobs = await db.TimelineFactJobs.AsNoTracking().Where(x => x.EntryId == entryId).OrderBy(x => x.Id).Take(101)
            .Select(x => new TimelineFactJobDto(x.Id, x.Kind, x.State, x.Attempts, x.NextAttemptAtUtc,
                x.LastErrorCode == null ? null : x.LastErrorCode == "Timeout" || x.LastErrorCode == "RateLimited" || x.LastErrorCode == "InvalidResponse" || x.LastErrorCode == "Cancelled" ? x.LastErrorCode : "Unavailable")).ToListAsync(ct);
        var current = await db.TimelineFactJobs.AsNoTracking().SingleOrDefaultAsync(x => x.EntryId == entryId && x.Kind == "Extraction" && x.State != "Superseded", ct);
        // Reserve bounded slots for actionable evidence, even after many clarification
        // rounds. Never replace the current generation with a historical Generated draft.
        var generated = current is { State: "Completed", ExtractionResultHash: not null }
            ? await db.TimelineFactProposals.AsNoTracking().SingleOrDefaultAsync(x => x.Id == current.Id && x.EntryId == entryId
                && x.Status == "Generated" && x.SourceFingerprint == current.SourceFingerprint, ct) : null;
        var reconciliation = await db.TimelineFactProposals.AsNoTracking().Where(x => x.EntryId == entryId && (x.Status == "Pending" || x.Status == "Accepted"))
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        var pinned = new[] { generated, reconciliation }.OfType<TimelineFactProposal>();
        var bounded = pinned.Concat(proposals).DistinctBy(x => x.Id).Take(50)
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id);
        var dtos = new List<TimelineFactProposalDto>();
        foreach (var proposal in bounded) dtos.Add(await ProposalAsync(proposal, coverage, proposal.Status, ct));
        var locked = await db.TimelineFactProposals.AnyAsync(x => x.EntryId == entryId && x.Status == "Accepted", ct)
            || await db.TimelineFacts.AnyAsync(x => x.EntryId == entryId, ct);
        var empty = current is not null && !await db.TimelineFactAdditions.AnyAsync(x => x.ProposalId == current.Id, ct);
        var state = locked ? "Reconciled" : current?.State switch
        {
            "Running" => "Loading", "Pending" => "Loading", "Retry" => "Retry", "Failed" => "Failed",
            "Completed" => empty ? "Empty" : "Pending", _ => "NotGenerated"
        };
        var writable = await new ProjectAuthorization(db).CanWriteAsync(scope.ProjectId, actor, admin, ct);
        // This is a bounded multi-query read, not a transaction snapshot. Re-read the
        // authorized source and revision; mixed reads explicitly require a refresh.
        // The scope predicate prevents a concurrent cross-project move exposing text.
        var source = await ReadSourceAsync(scope, ct);
        var fingerprint = TimelineFactEvidence.Fingerprint(source);
        var finalRevision = await db.Timelines.AsNoTracking().Where(x => x.Id == scope.TimelineId).Select(x => x.FactRevision).SingleAsync(ct);
        var refresh = revision != finalRevision || TimelineFactEvidence.Fingerprint(before) != fingerprint || before.Status != source.Status
            || current is not null && current.SourceFingerprint != fingerprint
            || current?.State == "Completed" && generated is null;
        return new(facts.Take(500).ToArray(), dtos, jobs.Take(100).ToArray(), coverage, facts.Count > 500 || proposals.Count > 50 || jobs.Count > 100,
            state, current?.Id, writable && !locked, writable && !locked, writable && !locked && current is not null,
            new(source.Id, source.TimelineId, source.Title, source.Content, source.Status, fingerprint), refresh);
    }

    private async Task<TimelineEntry> ReadSourceAsync(TimelineFactScope scope, CancellationToken ct) =>
        await db.TimelineEntries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == scope.EntryId && x.TimelineId == scope.TimelineId
            && x.Timeline.ProjectId == scope.ProjectId, ct)
        ?? throw new TimelineFactConflictException("Source scope changed during read; refresh.");

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
            proposal.Status == "Pending" && !stale && questions.All(x => !string.IsNullOrWhiteSpace(x.Answer)),
            additions, invalidations, questions, coverage, Warnings(coverage), proposal.SourceFingerprint);
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
