using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProjectTimeline.DataAccessLogic;

namespace ProjectTimeline.BusinessLogic;

public sealed record TimelineFactScope(Guid ProjectId, Guid TimelineId, Guid EntryId);
public sealed record TimelineFactCandidate(string Claim, string EntityKey, string EntityType, string SourceQuote, string SourceKind = "Entry");
public sealed record TimelineFactRetraction(Guid FactId, string Reason, int? InvalidatingAdditionIndex = null);
public sealed record TimelineFactQuestion(string Question, string? Answer);
public sealed record TimelineFactDraft(IReadOnlyList<TimelineFactCandidate> Additions,
    IReadOnlyList<TimelineFactRetraction> Invalidations, IReadOnlyList<TimelineFactQuestion> Questions);

/// <summary>
/// T2: call PrepareAsync with the revision used to build the draft; only trusted extraction/planning code creates drafts/questions.
/// T3: expose proposal IDs and server-stored evidence; AcceptAsync takes no client replacement facts/readiness flag.
/// Clarifications require a replacement proposal. Scope and authorization are rechecked on every call, including retries.
/// Use a scoped DbContext with no unsaved changes; dispose it after a failed write (especially concurrency failures).
/// No ingestion, extraction, embedding calls, API wiring or automatic backfill are performed here.
/// </summary>
public sealed class TimelineFactService(ProjectTimelineDbContext db)
{
    public async Task<TimelineFactProposal> PrepareAsync(TimelineFactScope scope, Guid actorId, bool administrator,
        long expectedRevision, TimelineFactDraft draft, CancellationToken ct = default)
    {
        EnsureClean();
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        var (timeline, source) = await LoadScopeAsync(scope, actorId, administrator, ct);
        await db.RequireUnreconciledFactSourceAsync(source.Id, ct);
        if (timeline.FactRevision != expectedRevision) throw Conflict("Timeline changed; regenerate the proposal.");
        ValidateDraft(draft, source);
        await LoadInvalidationsAsync(draft.Invalidations.Select(x => x.FactId).ToArray(), source, ct);
        await db.SupersedeFactProposalsAsync(source.Id, includeGeneration: false, ct);
        var proposal = new TimelineFactProposal
        {
            EntryId = source.Id,
            ExpectedRevision = checked(expectedRevision + 1), // The save also reserves immutable source evidence by CASing the timeline.
            SourceFingerprint = TimelineFactEvidence.Fingerprint(source),
            QuestionsJson = JsonSerializer.Serialize(draft.Questions),
            ClarificationEvidence = JsonSerializer.Serialize(draft.Questions.Where(x => !string.IsNullOrWhiteSpace(x.Answer)))
        };
        proposal.Additions = draft.Additions.Select(x => new TimelineFactAddition
        {
            ProposalId = proposal.Id, Claim = x.Claim, EntityKey = x.EntityKey, EntityType = x.EntityType,
            SourceQuote = x.SourceQuote, SourceKind = x.SourceKind
        }).ToList();
        proposal.Invalidations = draft.Invalidations.Select(x => new TimelineFactInvalidation
        {
            ProposalId = proposal.Id, FactId = x.FactId, Reason = x.Reason,
            InvalidatingAdditionId = proposal.Additions.ElementAt(x.InvalidatingAdditionIndex!.Value).Id
        }).ToList();
        db.TimelineFactProposals.Add(proposal);
        await SaveAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return proposal;
    }

    public async Task<TimelineFactProposal> AcceptAsync(TimelineFactScope scope, Guid proposalId, Guid actorId,
        bool administrator, CancellationToken ct = default)
    {
        EnsureClean();
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        var (timeline, source) = await LoadScopeAsync(scope, actorId, administrator, ct);
        var proposal = await db.TimelineFactProposals.SingleOrDefaultAsync(x => x.Id == proposalId && x.EntryId == source.Id, ct)
            ?? throw new KeyNotFoundException("Proposal not found in this scope.");
        await db.Entry(proposal).ReloadAsync(ct);
        if (TimelineFactEvidence.Fingerprint(source) != proposal.SourceFingerprint) throw Conflict("Proposal source changed.");
        if (proposal.Status == "Accepted") return proposal; // Idempotency key is the persisted proposal ID, not supplied content.
        await db.RequireUnreconciledFactSourceAsync(source.Id, ct);
        if (proposal.Status != "Pending" || await db.TimelineFactJobs.AnyAsync(x => x.Id == proposal.Id && x.Kind == "Extraction", ct))
            throw Conflict("Only a reconciled pending proposal can be accepted.");
        if (timeline.FactRevision != proposal.ExpectedRevision) throw Conflict("Timeline changed; regenerate the proposal.");
        var additions = await db.TimelineFactAdditions.AsNoTracking().Where(x => x.ProposalId == proposalId).ToListAsync(ct);
        var invalidations = await db.TimelineFactInvalidations.AsNoTracking().Where(x => x.ProposalId == proposalId).ToListAsync(ct);
        var questions = JsonSerializer.Deserialize<TimelineFactQuestion[]>(proposal.QuestionsJson) ?? throw Conflict("Questions missing.");
        ValidateDraft(new(additions.Select(x => new TimelineFactCandidate(x.Claim, x.EntityKey, x.EntityType, x.SourceQuote, x.SourceKind)).ToArray(),
            invalidations.Select(x => new TimelineFactRetraction(x.FactId, x.Reason,
                x.InvalidatingAdditionId is null ? null : additions.FindIndex(a => a.Id == x.InvalidatingAdditionId))).ToArray(), questions), source, accepting: true);
        if (questions.Any(x => string.IsNullOrWhiteSpace(x.Answer))) throw Conflict("Clarification questions must be answered before acceptance.");
        var facts = await LoadInvalidationsAsync(invalidations.Select(x => x.FactId).ToArray(), source, ct);
        var now = DateTime.UtcNow;
        // Addition IDs are durable proposal identities and also identify their new facts. Resolve every
        // cause before changing targets; neither collection enumeration nor arbitrary first additions select it.
        foreach (var addition in additions)
        {
            var fact = new TimelineFact
            {
                Id = addition.Id, EntryId = source.Id, CreationReviewId = proposal.Id, AdditionId = addition.Id,
                Claim = addition.Claim, EntityKey = addition.EntityKey, EntityType = addition.EntityType,
                SourceQuote = addition.SourceQuote, SourceKind = addition.SourceKind,
                SourceFingerprint = proposal.SourceFingerprint, ClarificationEvidence = proposal.ClarificationEvidence
            };
            db.TimelineFacts.Add(fact);
            db.TimelineFactJobs.Add(new TimelineFactJob
            {
                EntryId = source.Id, FactId = fact.Id, Kind = "Embedding", WorkKey = $"embedding:{fact.Id:N}",
                SourceFingerprint = proposal.SourceFingerprint
            });
        }
        foreach (var fact in facts)
        {
            fact.Valid = false;
            fact.InvalidatedAtUtc = now;
            fact.InvalidationReviewId = proposal.Id;
            fact.InvalidatedByFactId = invalidations.Single(x => x.FactId == fact.Id).InvalidatingAdditionId;
        }
        proposal.Status = "Accepted";
        proposal.AcceptedAtUtc = now;
        proposal.AcceptedById = actorId;
        await SaveAsync(ct);
        if (tx is not null)
        {
            try { await tx.CommitAsync(ct); }
            catch (PostgresException ex) when (ex.SqlState is "40001" or "40P01") { throw Conflict("Concurrent timeline change; regenerate the proposal."); }
        }
        return proposal;
    }

    // Read-authorized effective state includes direct extraction and accepted-review origins.
    public async Task<IReadOnlyList<TimelineFact>> EffectiveAsync(TimelineFactScope scope, Guid actorId, bool administrator,
        CancellationToken ct = default)
    {
        if (!await new ProjectAuthorization(db).CanReadAsync(scope.ProjectId, actorId, administrator, ct)
            || !await db.Users.AnyAsync(x => x.Id == actorId && x.IsActive, ct)) throw new UnauthorizedAccessException();
        var source = await db.TimelineEntries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == scope.EntryId
            && x.TimelineId == scope.TimelineId && x.Timeline.ProjectId == scope.ProjectId, ct)
            ?? throw new KeyNotFoundException("Entry not found in this scope.");
        return await (from fact in TimelineFactEvidence.Effective(db, scope.TimelineId, source.EntryNumber).AsNoTracking()
                      join entry in db.TimelineEntries on fact.EntryId equals entry.Id
                      orderby entry.EntryNumber, fact.Id
                      select fact).ToListAsync(ct);
    }

    private async Task<(Timeline, TimelineEntry)> LoadScopeAsync(TimelineFactScope scope, Guid actorId, bool administrator, CancellationToken ct)
    {
        if (!await new ProjectAuthorization(db).CanWriteAsync(scope.ProjectId, actorId, administrator, ct)
            || !await db.Users.AnyAsync(x => x.Id == actorId && x.IsActive, ct)) throw new UnauthorizedAccessException();
        var timeline = await db.Timelines.SingleOrDefaultAsync(x => x.Id == scope.TimelineId && x.ProjectId == scope.ProjectId, ct)
            ?? throw new KeyNotFoundException("Timeline not found in this scope.");
        await db.Entry(timeline).ReloadAsync(ct);
        var source = await db.TimelineEntries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == scope.EntryId && x.TimelineId == timeline.Id, ct)
            ?? throw new KeyNotFoundException("Entry not found in this scope.");
        return (timeline, source);
    }

    private async Task<List<TimelineFact>> LoadInvalidationsAsync(Guid[] ids, TimelineEntry source, CancellationToken ct)
    {
        if (ids.Distinct().Count() != ids.Length) throw Conflict("Duplicate invalidation IDs.");
        var facts = await TimelineFactEvidence.Effective(db, source.TimelineId, source.EntryNumber)
            .Where(fact => ids.Contains(fact.Id)).ToListAsync(ct);
        foreach (var fact in facts)
        {
            await db.Entry(fact).ReloadAsync(ct);
            var evidence = await db.TimelineEntries.AsNoTracking().SingleAsync(x => x.Id == fact.EntryId, ct);
            if (TimelineFactEvidence.Fingerprint(evidence) != fact.SourceFingerprint) throw Conflict("Invalidated fact source changed.");
        }
        if (facts.Count != ids.Length || facts.Any(x => !x.Valid)) throw Conflict("Invalidation contains unknown, foreign, future, or invalid facts.");
        return facts;
    }

    // Worker owns the serializable transaction. Generated is a non-acceptible extraction draft;
    // its primary key is the durable extraction job ID. No facts/embeddings exist before reconciliation.
    internal async Task CompleteExtractionAsync(TimelineFactJob job, TimelineFactDraft draft, CancellationToken ct)
    {
        var source = await db.TimelineEntries.AsNoTracking().SingleAsync(x => x.Id == job.EntryId, ct);
        if (TimelineFactEvidence.Fingerprint(source) != job.SourceFingerprint) throw Conflict("Extraction source changed.");
        if (draft.Invalidations.Count != 0 || draft.Questions.Count != 0) throw Conflict("Extraction cannot propose review changes.");
        TimelineFactProcessing.ValidateCandidates(draft.Additions, new(source.Title, source.Content));
        var proposal = new TimelineFactProposal
        {
            Id = job.Id, EntryId = source.Id, Status = "Generated", SourceFingerprint = job.SourceFingerprint,
            ExpectedRevision = checked(job.ExtractionRevision!.Value + 1)
        };
        proposal.Additions = draft.Additions.Select(x => new TimelineFactAddition
        {
            ProposalId = proposal.Id, Claim = x.Claim, EntityKey = x.EntityKey, EntityType = x.EntityType,
            SourceQuote = x.SourceQuote, SourceKind = x.SourceKind
        }).ToList();
        db.TimelineFactProposals.Add(proposal);
        job.ExtractionResultHash = TimelineFactEvidence.ResultHash(proposal.Additions.Select(TimelineFactEvidence.CandidateEvidence));
        job.State = "Completed";
        job.LeaseOwner = null;
        job.LeaseExpiresAtUtc = null;
        job.LastErrorCode = null;
        await SaveAsync(ct);
    }

    private static void ValidateDraft(TimelineFactDraft draft, TimelineEntry source, bool accepting = false)
    {
        if (draft.Additions.Count > 200 || draft.Invalidations.Count > 200 || draft.Questions.Count > 100) throw Conflict("Proposal is too large.");
        foreach (var question in draft.Questions)
        {
            Require(question.Question, 2000);
            if (question.Answer is not null) Require(question.Answer, 4000);
        }
        foreach (var addition in draft.Additions)
        {
            Require(addition.Claim, 4000); Require(addition.EntityKey, 300); Require(addition.EntityType, 100); Require(addition.SourceQuote, 20000);
            var supported = addition.SourceKind switch
            {
                "Entry" => source.Content.Contains(addition.SourceQuote, StringComparison.Ordinal) || source.Title.Contains(addition.SourceQuote, StringComparison.Ordinal),
                "Clarification" => draft.Questions.Any(x => x.Answer?.Contains(addition.SourceQuote, StringComparison.Ordinal) == true),
                _ => false
            };
            if (!supported) throw Conflict("Claim quote is not present in its evidence.");
        }
        foreach (var invalidation in draft.Invalidations)
        {
            Require(invalidation.Reason, 4000);
            if (invalidation.InvalidatingAdditionIndex is not int index || index < 0 || index >= draft.Additions.Count)
                throw Conflict("Invalidation requires an explicit replacement addition index in this proposal.");
        }
        if (JsonSerializer.Serialize(draft.Questions).Length > 20000) throw Conflict("Clarification evidence is too large.");
    }
    private static void Require(string value, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max) throw Conflict("Invalid fact/proposal text.");
    }
    private void EnsureClean()
    {
        if (db.ChangeTracker.HasChanges()) throw new InvalidOperationException("Fact operations require a context without unsaved changes.");
    }
    private async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw Conflict("Concurrent timeline change; regenerate the proposal."); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState is "40001" or "40P01")
        { throw Conflict("Concurrent timeline change; regenerate the proposal."); }
    }
    private static TimelineFactConflictException Conflict(string message) => new(message);
}
