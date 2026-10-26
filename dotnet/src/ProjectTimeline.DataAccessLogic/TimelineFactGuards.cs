using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace ProjectTimeline.DataAccessLogic;

public sealed class TimelineFactConflictException(string message) : InvalidOperationException(message);

public sealed partial class ProjectTimelineDbContext
{
    public DbSet<TimelineFact> TimelineFacts => Set<TimelineFact>();
    public DbSet<TimelineFactProposal> TimelineFactProposals => Set<TimelineFactProposal>();
    public DbSet<TimelineFactAddition> TimelineFactAdditions => Set<TimelineFactAddition>();
    public DbSet<TimelineFactInvalidation> TimelineFactInvalidations => Set<TimelineFactInvalidation>();
    public DbSet<TimelineFactJob> TimelineFactJobs => Set<TimelineFactJob>();

    private void RequireNewProposal(Guid id)
    {
        if (!ChangeTracker.Entries<TimelineFactProposal>().Any(x => x.Entity.Id == id && x.State == EntityState.Added && x.Entity.Status is "Pending" or "Generated"))
            throw new TimelineFactConflictException("Proposal changes must be persisted with a new pending proposal.");
    }

    public async Task RequireUnreconciledFactSourceAsync(Guid entryId, CancellationToken ct = default)
    {
        if (await TimelineFactProposals.AnyAsync(x => x.EntryId == entryId && x.Status == "Accepted", ct)
            || await TimelineFacts.AnyAsync(x => x.EntryId == entryId, ct))
            throw new TimelineFactConflictException("Accepted reconciliation locks this source; corrections require a new entry.");
    }

    private sealed record AcceptanceQuestion(string Question, string? Answer,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? ExtractionGenerationId = null,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? ExtractionAnswerId = null);

    private async Task ValidateReviewQuestionsAsync(TimelineEntry source, string json, CancellationToken ct)
    {
        AcceptanceQuestion[] questions;
        try { questions = JsonSerializer.Deserialize<AcceptanceQuestion[]>(json) ?? throw new JsonException(); }
        catch (JsonException) { throw new TimelineFactConflictException("Invalid clarification evidence."); }
        if (questions.Any(x => x is null)) throw new TimelineFactConflictException("Invalid clarification evidence.");
        await ValidateExtractionReviewReferencesAsync(source, questions.Select(x => new TimelineExtractionReviewReference(
            x.ExtractionGenerationId, x.ExtractionAnswerId, x.Question, x.Answer)), ct);
    }

    // Validate the whole persisted proposal, not merely whichever changed facts the caller happened to attach.
    private async Task ValidateAcceptanceAsync(TimelineFactProposal proposal, CancellationToken ct)
    {
        var stored = await TimelineFactProposals.AsNoTracking().SingleAsync(x => x.Id == proposal.Id, ct);
        if (await TimelineFactJobs.AnyAsync(x => x.Id == proposal.Id && x.Kind == "Extraction", ct)
            || stored.Status != "Pending" || stored.EntryId != proposal.EntryId || stored.ExpectedRevision != proposal.ExpectedRevision
            || stored.SourceFingerprint != proposal.SourceFingerprint || stored.QuestionsJson != proposal.QuestionsJson
            || stored.ClarificationEvidence != proposal.ClarificationEvidence)
            throw new TimelineFactConflictException("Acceptance must use the unchanged pending proposal.");
        var source = await TimelineEntries.AsNoTracking().SingleAsync(x => x.Id == stored.EntryId, ct);
        await Timelines.FindAsync([source.TimelineId], ct); // Reserve CAS before checking the accepted-entry lock.
        await RequireUnreconciledFactSourceAsync(source.Id, ct);
        if (TimelineFactEvidence.Fingerprint(source) != stored.SourceFingerprint)
            throw new TimelineFactConflictException("Acceptance source changed.");
        var timeline = await Timelines.AsNoTracking().SingleAsync(x => x.Id == source.TimelineId, ct);
        var trackedTimeline = ChangeTracker.Entries<Timeline>().SingleOrDefault(x => x.Entity.Id == timeline.Id);
        if (timeline.FactRevision != stored.ExpectedRevision || (trackedTimeline is not null
            && trackedTimeline.Property(x => x.FactRevision).OriginalValue != stored.ExpectedRevision))
            throw new TimelineFactConflictException("Acceptance scope is stale.");
        if (ChangeTracker.Entries<TimelineEntry>().Any(x => x.Entity.TimelineId == timeline.Id
            && x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            || ChangeTracker.Entries<TimelineFactProposal>().Any(x => x.State == EntityState.Added))
            throw new TimelineFactConflictException("Acceptance cannot include unrelated source or proposal changes.");
        if (!await Users.AnyAsync(x => x.Id == proposal.AcceptedById && x.IsActive, ct)
            || !await Projects.AnyAsync(x => x.Id == timeline.ProjectId && (x.OwnerId == proposal.AcceptedById
                || x.Participants.Any(p => p.UserId == proposal.AcceptedById && (p.ProjectRoleName == "Creator" || p.ProjectRoleName == "Contributor"))
                || Users.Any(u => u.Id == proposal.AcceptedById && u.UserRole.RoleName == "Admin")), ct))
            throw new TimelineFactConflictException("Acceptance actor cannot write this project.");

        AcceptanceQuestion[] questions;
        try { questions = JsonSerializer.Deserialize<AcceptanceQuestion[]>(stored.QuestionsJson) ?? throw new JsonException(); }
        catch (JsonException) { throw new TimelineFactConflictException("Invalid clarification evidence."); }
        if (questions.Length > 100 || questions.Any(x => x is null || !FactText(x.Question, 2000) || !FactText(x.Answer, 4000))
            || stored.QuestionsJson.Length > 20000 || stored.ClarificationEvidence != JsonSerializer.Serialize(questions))
            throw new TimelineFactConflictException("Acceptance requires complete, unchanged clarification evidence.");

        await ValidateReviewQuestionsAsync(source, stored.QuestionsJson, ct);
        var additions = await TimelineFactAdditions.AsNoTracking().Where(x => x.ProposalId == stored.Id).ToListAsync(ct);
        var invalidations = await TimelineFactInvalidations.AsNoTracking().Where(x => x.ProposalId == stored.Id).ToListAsync(ct);
        if (additions.Count > 200 || invalidations.Count > 200
            || invalidations.Select(x => x.FactId).Distinct().Count() != invalidations.Count)
            throw new TimelineFactConflictException("Invalid acceptance changeset.");
        var added = ChangeTracker.Entries<TimelineFact>().Where(x => x.State == EntityState.Added && x.Entity.CreationReviewId == stored.Id)
            .Select(x => x.Entity).ToList();
        if (added.Count != additions.Count || added.Select(x => x.AdditionId).Distinct().Count() != added.Count
            || !added.Select(x => x.AdditionId).ToHashSet().SetEquals(additions.Select(x => (Guid?)x.Id)))
            throw new TimelineFactConflictException("Acceptance must apply every addition exactly once, with no unrelated additions.");
        foreach (var addition in additions)
        {
            var fact = added.Single(x => x.AdditionId == addition.Id);
            var quoteSupported = FactText(addition.SourceQuote, 20000) && (addition.SourceKind switch
            {
                "Entry" => source.Title.Contains(addition.SourceQuote, StringComparison.Ordinal) || source.Content.Contains(addition.SourceQuote, StringComparison.Ordinal),
                "Clarification" => questions.Any(x => x.Answer!.Contains(addition.SourceQuote, StringComparison.Ordinal)),
                _ => false
            });
            if (!FactText(addition.Claim, 4000) || !FactText(addition.EntityKey, 300) || !FactText(addition.EntityType, 100)
                || !FactText(addition.SourceQuote, 20000) || !quoteSupported || fact.EntryId != source.Id
                || fact.Claim != addition.Claim || fact.EntityKey != addition.EntityKey || fact.EntityType != addition.EntityType
                || fact.Id != addition.Id || fact.SourceQuote != addition.SourceQuote || fact.SourceKind != addition.SourceKind
                || fact.SourceFingerprint != stored.SourceFingerprint || fact.ClarificationEvidence != stored.ClarificationEvidence)
                throw new TimelineFactConflictException("Acceptance addition provenance does not match its evidence.");
        }
        var queued = ChangeTracker.Entries<TimelineFactJob>().Where(x => x.State == EntityState.Added).Select(x => x.Entity).ToList();
        if (queued.Count != added.Count || added.Any(f => queued.Count(j => j.FactId == f.Id && j.EntryId == source.Id
            && j.Kind == "Embedding" && j.State == "Pending" && j.WorkKey == $"embedding:{f.Id:N}"
            && j.SourceFingerprint == stored.SourceFingerprint && j.Model == "text-embedding-3-small") != 1))
            throw new TimelineFactConflictException("Acceptance must queue each new fact embedding atomically.");
        var invalidated = ChangeTracker.Entries<TimelineFact>().Where(x => x.State == EntityState.Modified
            && x.Entity.InvalidationReviewId == stored.Id && x.Properties.Any(p => p.IsModified
                && p.Metadata.Name is "Valid" or "InvalidatedAtUtc" or "InvalidationReviewId" or "InvalidatedByFactId")).ToList();
        if (invalidated.Count != invalidations.Count || !invalidated.Select(x => x.Entity.Id).ToHashSet().SetEquals(invalidations.Select(x => x.FactId)))
            throw new TimelineFactConflictException("Acceptance must apply every invalidation, with no unrelated invalidations.");
        foreach (var invalidation in invalidations)
        {
            var target = await TimelineFacts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == invalidation.FactId && x.Valid, ct)
                ?? throw new TimelineFactConflictException("Invalidation target is no longer valid.");
            var targetSource = await TimelineEntries.AsNoTracking().SingleAsync(x => x.Id == target.EntryId, ct);
            var effective = await TimelineFactEvidence.Effective(this, source.TimelineId, source.EntryNumber).AnyAsync(x => x.Id == target.Id, ct);
            var change = invalidated.Single(x => x.Entity.Id == target.Id);
            if (!FactText(invalidation.Reason, 4000) || targetSource.TimelineId != source.TimelineId || targetSource.EntryNumber > source.EntryNumber
                || !effective
                || TimelineFactEvidence.Fingerprint(targetSource) != target.SourceFingerprint
                || invalidation.InvalidatingAdditionId is null
                || !additions.Any(x => x.Id == invalidation.InvalidatingAdditionId && x.ProposalId == stored.Id)
                || !added.Any(x => x.Id == change.Entity.InvalidatedByFactId && x.AdditionId == invalidation.InvalidatingAdditionId)
                || change.Entity.InvalidatedByFactId == target.Id || target.InvalidatedByFactId is not null
                || change.Property(x => x.InvalidatedByFactId).OriginalValue is not null
                || !change.Property(x => x.Valid).OriginalValue || change.Entity.Valid || change.Entity.InvalidatedAtUtc != proposal.AcceptedAtUtc)
                throw new TimelineFactConflictException("Invalidation provenance or scope is invalid.");
        }
    }

    // Revocation is persisted, not inferred from fingerprints: restoring the same source never revives old work.
    public async Task SupersedeFactProposalsAsync(Guid entryId, bool includeGeneration, CancellationToken ct = default)
    {
        var proposals = await TimelineFactProposals.Where(x => x.EntryId == entryId
            && (x.Status == "Pending" || includeGeneration && x.Status == "Generated")).ToListAsync(ct);
        foreach (var proposal in proposals) proposal.Status = "Superseded";
        if (!includeGeneration) return;
        var jobs = await TimelineFactJobs.Where(x => x.EntryId == entryId && x.Kind == "Extraction" && x.State != "Superseded").ToListAsync(ct);
        foreach (var job in jobs)
        {
            if (await TimelineFacts.AnyAsync(x => x.ExtractionJobId == job.Id, ct))
                throw new TimelineFactConflictException("Historical extraction evidence is immutable.");
            job.State = "Superseded"; job.LeaseOwner = null; job.LeaseExpiresAtUtc = null;
        }
    }

    private async Task ValidateExtractionCompletionAsync(TimelineFactJob job, CancellationToken ct)
    {
        var stored = await TimelineFactJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == job.Id, ct)
            ?? throw new TimelineFactConflictException("Extraction requires a durable job.");
        var change = Entry(job);
        var source = await TimelineEntries.AsNoTracking().SingleAsync(x => x.Id == stored.EntryId, ct);
        var timeline = await Timelines.AsNoTracking().SingleAsync(x => x.Id == source.TimelineId, ct);
        if (stored.Kind != "Extraction" || stored.FactId is not null || !TimelineFactEvidence.ExtractionKey(stored)
            || stored.State != "Running" || stored.LeaseOwner is null || stored.LeaseExpiresAtUtc is null || stored.LeaseExpiresAtUtc <= DateTime.UtcNow
            || stored.Version != change.Property(x => x.Version).OriginalValue
            || stored.LeaseOwner != change.Property(x => x.LeaseOwner).OriginalValue
            || job.LeaseOwner is not null || job.LeaseExpiresAtUtc is not null
            || stored.SourceFingerprint != TimelineFactEvidence.Fingerprint(source)
            || stored.ExtractionRevision != timeline.FactRevision || job.ExtractionRevision != stored.ExtractionRevision
            || !FactText(stored.Model, 100) || stored.Model == "unassigned")
            throw new TimelineFactConflictException("Extraction lease, revision or source changed.");
        if (await TimelineFacts.AnyAsync(x => x.EntryId == source.Id, ct)
            || await TimelineFactProposals.AnyAsync(x => x.EntryId == source.Id && x.Status != "Superseded", ct)
            || await TimelineFactJobs.AnyAsync(x => x.EntryId == source.Id && x.Id != job.Id && x.State != "Superseded", ct))
            throw new TimelineFactConflictException("Extraction cannot replace existing fact or review evidence.");
        if (ChangeTracker.Entries<TimelineFactJob>().Any(x => x.State == EntityState.Modified && x.Entity.Id != job.Id)
            || ChangeTracker.Entries().Any(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                && x.Entity is not (TimelineFactProposal or TimelineFactAddition or TimelineFactJob or Timeline))
            || ChangeTracker.Entries<Timeline>().Any(x => x.State != EntityState.Unchanged
                && (x.Entity.Id != timeline.Id || x.Properties.Any(p => p.IsModified && p.Metadata.Name != "FactRevision")))
            || ChangeTracker.Entries<TimelineEntry>().Any(x => x.State != EntityState.Unchanged)
            || ChangeTracker.Entries<TimelineFactProposal>().Any(x => x.State != EntityState.Unchanged
                && (x.State != EntityState.Added || x.Entity.Id != job.Id || x.Entity.Status != "Generated"))
            || ChangeTracker.Entries<TimelineFact>().Any(x => x.State != EntityState.Unchanged))
            throw new TimelineFactConflictException("Extraction cannot include unrelated evidence changes.");
        if (!await Users.AnyAsync(x => x.Id == source.AuthorId && x.IsActive, ct)
            || !await Projects.AnyAsync(x => x.Id == timeline.ProjectId && (x.OwnerId == source.AuthorId
                || x.Participants.Any(p => p.UserId == source.AuthorId && (p.ProjectRoleName == "Creator" || p.ProjectRoleName == "Contributor"))
                || Users.Any(u => u.Id == source.AuthorId && u.UserRole.RoleName == "Admin")), ct))
            throw new TimelineFactConflictException("Extraction author cannot write this project.");
        if (change.Properties.Any(p => p.IsModified && p.Metadata.Name is not
            ("State" or "LeaseOwner" or "LeaseExpiresAtUtc" or "LastErrorCode" or "ExtractionResultHash" or "ExtractionSupportJson" or "UpdatedAtUtc")))
            throw new TimelineFactConflictException("Extraction completion cannot rewrite its lease evidence.");
        var proposal = ChangeTracker.Entries<TimelineFactProposal>().SingleOrDefault(x => x.State == EntityState.Added && x.Entity.Id == job.Id)?.Entity;
        if (proposal is null || proposal.EntryId != source.Id || proposal.Status != "Generated"
            || proposal.SourceFingerprint != stored.SourceFingerprint || proposal.ExpectedRevision != stored.ExtractionRevision + 1
            || proposal.QuestionsJson != "[]" || proposal.ClarificationEvidence != ""
            || ChangeTracker.Entries<TimelineFactJob>().Any(x => x.State == EntityState.Added)
            || ChangeTracker.Entries<TimelineFactInvalidation>().Any(x => x.State != EntityState.Unchanged))
            throw new TimelineFactConflictException("Extraction requires only its generated draft, with no effective facts or embeddings.");
        var additions = ChangeTracker.Entries<TimelineFactAddition>().Where(x => x.State == EntityState.Added).Select(x => x.Entity).ToList();
        var facts = additions.Select(TimelineFactEvidence.CandidateEvidence).ToList();
        if (additions.Any(x => x.ProposalId != job.Id) || facts.Count > 200
            || facts.Select(TimelineFactEvidence.CandidateKey).Distinct().Count() != facts.Count
            || job.ExtractionResultHash != TimelineFactEvidence.ResultHash(facts))
            throw new TimelineFactConflictException("Extraction result identity is invalid.");
        if (stored.ExtractionInputJson is not null)
        {
            var snapshot = await ReadExtractionInputAsync(stored, source, ct);
            IReadOnlyList<TimelineExtractionSupport> support;
            try { support = JsonSerializer.Deserialize<TimelineExtractionSupport[]>(job.ExtractionSupportJson ?? "[]") ?? throw new JsonException(); }
            catch (JsonException) { throw new TimelineFactConflictException("Invalid extraction support."); }
            TimelineExtractionEvidence.Validate(source, snapshot.Input, facts, support);
            if (support.Any(x => x.AdditionId != additions[x.CandidateIndex].Id))
                throw new TimelineFactConflictException("Supporting evidence addition identity changed.");
        }
        else if (job.ExtractionSupportJson is not null)
            throw new TimelineFactConflictException("Legacy completion cannot forge support.");
        foreach (var fact in facts)
            if ((stored.ExtractionInputJson is null && fact.SourceKind != "Entry") || !FactText(fact.Claim, 4000) || !FactText(fact.EntityKey, 300)
                || !FactText(fact.EntityType, 100) || !FactText(fact.SourceQuote, 20000)
                || (fact.SourceKind == "Entry" && !(source.Title.Contains(fact.SourceQuote, StringComparison.Ordinal) || source.Content.Contains(fact.SourceQuote, StringComparison.Ordinal))))
                throw new TimelineFactConflictException("Extraction candidate does not match grounded provenance.");
        var tracked = await Timelines.FindAsync([timeline.Id], ct);
        if (Entry(tracked!).Property(x => x.FactRevision).OriginalValue != stored.ExtractionRevision)
            throw new TimelineFactConflictException("Extraction scope is stale.");
    }

    private static bool FactText(string? text, int max) => !string.IsNullOrWhiteSpace(text) && text.Length <= max;

    private async Task ValidateFactsAsync(CancellationToken ct)
    {
        ChangeTracker.DetectChanges();
        await ValidateEntryClarificationsAsync(ct);
        foreach (var completion in ChangeTracker.Entries<TimelineFactJob>().Where(x => x.State == EntityState.Modified
            && x.Entity.Kind == "Extraction" && x.Entity.State == "Completed").ToList())
            await ValidateExtractionCompletionAsync(completion.Entity, ct);
        var changedEntries = ChangeTracker.Entries<TimelineEntry>()
            .Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList();
        var timelineIds = new HashSet<Guid>();
        foreach (var change in changedEntries)
        {
            var sourceChanged = change.State == EntityState.Deleted || new[] { "Title", "Content", "TimelineId", "EntryNumber", "AuthorId", "Status", "CreatedAtUtc" }
                .Any(p => change.Property(p).IsModified);
            if (change.State != EntityState.Added && sourceChanged)
            {
                // A move must reserve BOTH baselines before a jobless zero-addition acceptance
                // can commit on the original timeline without changing any source concurrency column.
                foreach (var id in new[] { change.Property(x => x.TimelineId).OriginalValue, change.Entity.TimelineId }.Distinct().Order())
                    await Timelines.FindAsync([id], ct);
                if (await TimelineFacts.AnyAsync(x => x.EntryId == change.Entity.Id, ct)
                    || await TimelineFactProposals.AnyAsync(x => x.EntryId == change.Entity.Id && x.Status == "Accepted", ct)
                    || ChangeTracker.Entries<TimelineFactProposal>().Any(x => x.Entity.EntryId == change.Entity.Id && x.Entity.Status == "Accepted"))
                    throw new TimelineFactConflictException("Accepted reconciliation locks the source; create a new entry for corrections.");
                if (change.State == EntityState.Deleted && (await TimelineFactJobs.AnyAsync(x => x.EntryId == change.Entity.Id, ct)
                    || await TimelineFactProposals.AnyAsync(x => x.EntryId == change.Entity.Id, ct)))
                    throw new TimelineFactConflictException("Processing history cannot be deleted with its source.");
                await SupersedeFactProposalsAsync(change.Entity.Id, includeGeneration: true, ct);
            }
            if (sourceChanged || change.State == EntityState.Added)
            {
                timelineIds.Add(change.Entity.TimelineId);
                if (change.State != EntityState.Added) timelineIds.Add(change.Property(x => x.TimelineId).OriginalValue);
            }
        }
        foreach (var change in ChangeTracker.Entries<Timeline>().ToList())
            if (change.State == EntityState.Modified && change.Property(x => x.ProjectId).IsModified
                && await TimelineEntries.AnyAsync(x => x.TimelineId == change.Entity.Id, ct))
                throw new TimelineFactConflictException("A populated timeline cannot change projects.");

        foreach (var change in ChangeTracker.Entries<TimelineFactAddition>().ToList())
        {
            if (change.State is EntityState.Modified or EntityState.Deleted)
                throw new TimelineFactConflictException("Proposal additions are immutable; create a replacement proposal.");
            if (change.State == EntityState.Added) RequireNewProposal(change.Entity.ProposalId);
        }
        foreach (var change in ChangeTracker.Entries<TimelineFactInvalidation>().ToList())
        {
            if (change.State is EntityState.Modified or EntityState.Deleted)
                throw new TimelineFactConflictException("Proposal invalidations are immutable.");
            if (change.State == EntityState.Added)
            {
                RequireNewProposal(change.Entity.ProposalId);
                if (change.Entity.InvalidatingAdditionId is null || change.Entity.InvalidatingAdditionId == change.Entity.FactId
                    || !ChangeTracker.Entries<TimelineFactAddition>().Any(x => x.State == EntityState.Added
                        && x.Entity.Id == change.Entity.InvalidatingAdditionId && x.Entity.ProposalId == change.Entity.ProposalId))
                    throw new TimelineFactConflictException("New invalidations require a replacement addition from the same new proposal.");
            }
        }
        var accepting = ChangeTracker.Entries<TimelineFactProposal>().Where(x => x.State == EntityState.Modified && x.Entity.Status == "Accepted").ToList();
        if (accepting.Count > 1) throw new TimelineFactConflictException("Accept one proposal per save.");
        foreach (var change in ChangeTracker.Entries<TimelineFactProposal>().ToList())
        {
            if (change.State == EntityState.Added && (change.Entity.Status is not ("Pending" or "Generated") || change.Entity.AcceptedAtUtc is not null || change.Entity.AcceptedById is not null))
                throw new TimelineFactConflictException("New proposals must remain pending.");
            if (change.State == EntityState.Added && change.Entity.Status == "Generated"
                && !ChangeTracker.Entries<TimelineFactJob>().Any(x => x.State == EntityState.Modified
                    && x.Entity.Id == change.Entity.Id && x.Entity.Kind == "Extraction" && x.Entity.State == "Completed"))
                throw new TimelineFactConflictException("Generated candidates require their leased job completion.");
            if (change.State == EntityState.Added && change.Entity.Status == "Pending"
                && (await TimelineFactJobs.AnyAsync(x => x.Id == change.Entity.Id, ct)
                    || ChangeTracker.Entries<TimelineFactJob>().Any(x => x.Entity.Id == change.Entity.Id)))
                throw new TimelineFactConflictException("Extraction drafts cannot masquerade as reconciled proposals.");
            if (change.State == EntityState.Deleted || (change.State == EntityState.Modified && change.Properties.Any(p => p.IsModified
                && p.Metadata.Name is not ("Status" or "AcceptedAtUtc" or "AcceptedById" or "UpdatedAtUtc"))))
                throw new TimelineFactConflictException("Proposal evidence is immutable.");
            if (change.State == EntityState.Modified && change.Entity.Status == "Superseded")
            {
                if (change.Property(x => x.Status).OriginalValue is not ("Pending" or "Generated")
                    || change.Entity.AcceptedAtUtc is not null || change.Entity.AcceptedById is not null
                    || change.Properties.Any(p => p.IsModified && p.Metadata.Name is not ("Status" or "UpdatedAtUtc")))
                    throw new TimelineFactConflictException("Only pending evidence may be superseded.");
                var source = await TimelineEntries.FindAsync([change.Entity.EntryId], ct)
                    ?? throw new TimelineFactConflictException("Proposal source missing.");
                timelineIds.Add(source.TimelineId);
                continue; // Evidence is immutable, but may intentionally refer to the previous source.
            }
            if (change.State == EntityState.Modified && (change.Property(x => x.Status).OriginalValue != "Pending"
                || change.Entity.Status != "Accepted" || change.Entity.AcceptedAtUtc is null || change.Entity.AcceptedById is null))
                throw new TimelineFactConflictException("Invalid proposal transition.");
            if (change.State is EntityState.Added or EntityState.Modified)
            {
                var source = await TimelineEntries.FindAsync([change.Entity.EntryId], ct)
                    ?? throw new TimelineFactConflictException("Proposal source missing.");
                await Timelines.FindAsync([source.TimelineId], ct);
                if (change.State == EntityState.Added) await RequireUnreconciledFactSourceAsync(source.Id, ct);
                if (change.State == EntityState.Added && change.Entity.Status == "Pending")
                    await ValidateReviewQuestionsAsync(source, change.Entity.QuestionsJson, ct);
                if (TimelineFactEvidence.Fingerprint(source) != change.Entity.SourceFingerprint)
                    throw new TimelineFactConflictException("Proposal source changed.");
                if (change.State == EntityState.Modified) await ValidateAcceptanceAsync(change.Entity, ct);
                timelineIds.Add(source.TimelineId);
            }
        }
        foreach (var change in ChangeTracker.Entries<TimelineFact>().ToList())
        {
            if (change.State == EntityState.Deleted || (change.State == EntityState.Modified && change.Properties.Any(p => p.IsModified
                && p.Metadata.Name is not ("Valid" or "InvalidatedAtUtc" or "InvalidationReviewId" or "InvalidatedByFactId" or "Embedding" or "EmbeddingModel" or "EmbeddedAtUtc" or "UpdatedAtUtc"))))
                throw new TimelineFactConflictException("Accepted fact content is immutable.");
            if (change.State == EntityState.Modified && change.Properties.Any(p => p.IsModified
                && p.Metadata.Name is "Valid" or "InvalidatedAtUtc" or "InvalidationReviewId" or "InvalidatedByFactId"))
            {
                if (!change.Property(x => x.Valid).OriginalValue || change.Entity.Valid || change.Entity.InvalidatedAtUtc is null
                    || change.Entity.InvalidationReviewId is null || change.Entity.InvalidatedByFactId is null
                    || change.Property(x => x.InvalidatedByFactId).OriginalValue is not null
                    || change.Entity.InvalidatedByFactId == change.Entity.Id)
                    throw new TimelineFactConflictException("Invalid fact invalidation.");
                var review = ChangeTracker.Entries<TimelineFactProposal>().SingleOrDefault(x => x.Entity.Id == change.Entity.InvalidationReviewId);
                if (review is null || review.State != EntityState.Modified || review.Entity.Status != "Accepted"
                    || review.Entity.AcceptedAtUtc != change.Entity.InvalidatedAtUtc
                    || !await TimelineFactInvalidations.AnyAsync(x => x.ProposalId == review.Entity.Id && x.FactId == change.Entity.Id, ct))
                    throw new TimelineFactConflictException("Invalidation requires its proposed review acceptance in the same save.");
            }
            if (change.State == EntityState.Added && change.Entity.ExtractionJobId is not null)
                throw new TimelineFactConflictException("New extraction results must remain candidates until reconciliation acceptance.");
            else if (change.State == EntityState.Added)
            {
                if (change.Entity.CandidateKey is not null || change.Entity.CreationReviewId is null || change.Entity.AdditionId is null)
                    throw new TimelineFactConflictException("Review facts require review and addition provenance.");
                var review = ChangeTracker.Entries<TimelineFactProposal>().SingleOrDefault(x => x.Entity.Id == change.Entity.CreationReviewId);
                var addition = await TimelineFactAdditions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == change.Entity.AdditionId, ct);
                if (review is null || review.State != EntityState.Modified || review.Entity.Status != "Accepted"
                    || addition is null || addition.ProposalId != review.Entity.Id || change.Entity.EntryId != review.Entity.EntryId
                    || !change.Entity.Valid || change.Entity.InvalidatedAtUtc is not null || change.Entity.InvalidationReviewId is not null
                    || change.Entity.InvalidatedByFactId is not null
                    || change.Entity.Claim != addition.Claim || change.Entity.EntityKey != addition.EntityKey
                    || change.Entity.EntityType != addition.EntityType || change.Entity.SourceQuote != addition.SourceQuote
                    || change.Entity.SourceKind != addition.SourceKind || change.Entity.ClarificationEvidence != review.Entity.ClarificationEvidence)
                    throw new TimelineFactConflictException("Facts require their unchanged accepted addition.");
            }
            if (change.Entity.Embedding is not null && (change.Entity.Embedding.ToArray().Length != 1536
                || change.Entity.Embedding.ToArray().Any(x => !float.IsFinite(x))
                || !change.Entity.Embedding.ToArray().Any(x => x != 0)
                || change.Entity.EmbeddedAtUtc is null
                || change.Entity.EmbeddingModel != "text-embedding-3-small"))
                throw new TimelineFactConflictException("Embeddings require 1536 finite nonzero dimensions from text-embedding-3-small.");
            if (change.State == EntityState.Modified && change.Metadata.FindProperty(nameof(TimelineFact.Embedding)) is not null
                && change.Property(x => x.Embedding).IsModified
                && change.Entity.Embedding is not null && (!change.Entity.Valid
                    || !await TimelineFactEvidence.Effective(this,
                        (await TimelineEntries.AsNoTracking().SingleAsync(x => x.Id == change.Entity.EntryId, ct)).TimelineId)
                        .AnyAsync(x => x.Id == change.Entity.Id, ct)))
                throw new TimelineFactConflictException("Only valid accepted facts can receive embeddings.");
            if (change.State is EntityState.Added or EntityState.Modified)
            {
                var source = await TimelineEntries.FindAsync([change.Entity.EntryId], ct)
                    ?? throw new TimelineFactConflictException("Fact source missing.");
                if (TimelineFactEvidence.Fingerprint(source) != change.Entity.SourceFingerprint)
                    throw new TimelineFactConflictException("Fact source changed.");
                if (change.State == EntityState.Added || change.Property(x => x.Valid).IsModified) timelineIds.Add(source.TimelineId);
            }
        }
        foreach (var change in ChangeTracker.Entries<TimelineFactJob>().ToList())
        {
            if (change.State == EntityState.Deleted) throw new TimelineFactConflictException("Processing evidence cannot be deleted.");
            if (change.State is not (EntityState.Added or EntityState.Modified)) continue;
            if (change.Entity.Kind == "Extraction")
            {
                var inputChanged = change.State == EntityState.Added || change.Property(x => x.ExtractionInputJson).IsModified
                    || change.Property(x => x.EffectiveInputFingerprint).IsModified;
                if (inputChanged)
                {
                    var source = await TimelineEntries.FindAsync([change.Entity.EntryId], ct)
                        ?? throw new TimelineFactConflictException("Job source missing.");
                    if (change.State == EntityState.Added && change.Entity.ExtractionInputJson is null)
                        await StageExtractionInputAsync(change.Entity, source, ct: ct);
                    if (!stagedExtractionInputs.Contains(change.Entity.Id)
                        || change.State == EntityState.Modified && (change.Property(x => x.ExtractionInputJson).OriginalValue is not null
                            || change.Entity.State != "Running" || change.Property(x => x.State).OriginalValue == "AwaitingClarification"))
                        throw new TimelineFactConflictException("Extraction input is immutable and server captured.");
                    await ReadExtractionInputAsync(change.Entity, source, ct);
                }
                if (change.State == EntityState.Modified && change.Property(x => x.State).OriginalValue == "AwaitingClarification"
                    && (change.Entity.State is not ("AwaitingClarification" or "Superseded")
                        || change.Entity.State == "AwaitingClarification" && change.Properties.Any(p => p.IsModified
                            && p.Metadata.Name is not ("Version" or "UpdatedAtUtc"))))
                    throw new TimelineFactConflictException("Waiting generations require an explicit successor.");
            }
            else if (change.Entity.ExtractionInputJson is not null || change.Entity.EffectiveInputFingerprint is not null || change.Entity.ExtractionSupportJson is not null)
                throw new TimelineFactConflictException("Embedding jobs cannot carry extraction input.");
            if (change.Entity.ExtractionSupportJson is not null && (change.State == EntityState.Added
                || change.Property(x => x.ExtractionSupportJson).IsModified && change.Entity.State != "Completed"))
                throw new TimelineFactConflictException("Supporting evidence requires generated completion.");
            if (change.State == EntityState.Modified && change.Properties.Any(p => p.IsModified
                && p.Metadata.Name is "EntryId" or "FactId" or "WorkKey" or "Kind" or "SourceFingerprint"))
                throw new TimelineFactConflictException("Job identity and source are immutable.");
            if (change.State == EntityState.Modified && change.Property(x => x.Model).IsModified
                && !(change.Property(x => x.Model).OriginalValue == "unassigned" && change.Entity.Kind == "Extraction"
                    && change.Entity.State == "Running" && FactText(change.Entity.Model, 100)))
                throw new TimelineFactConflictException("Assigned processing model is immutable.");
            if (change.State == EntityState.Added && (change.Entity.ExtractionResultHash is not null
                || change.Entity.ExtractionRevision is not null || change.Entity.Kind == "Extraction" && change.Entity.State == "Completed"))
                throw new TimelineFactConflictException("New jobs cannot forge extraction completion.");
            var superseding = change.State == EntityState.Modified && change.Entity.Kind == "Extraction" && change.Entity.State == "Superseded";
            if (superseding && (change.Property(x => x.State).OriginalValue == "Superseded"
                || change.Properties.Any(p => p.IsModified && p.Metadata.Name is not ("State" or "LeaseOwner" or "LeaseExpiresAtUtc" or "UpdatedAtUtc"))
                || change.Entity.LeaseOwner is not null || change.Entity.LeaseExpiresAtUtc is not null
                || await TimelineFacts.AnyAsync(x => x.ExtractionJobId == change.Entity.Id, ct)
                || await TimelineFactProposals.AnyAsync(x => x.EntryId == change.Entity.EntryId && x.Status == "Accepted", ct)))
                throw new TimelineFactConflictException("Accepted evidence cannot be superseded.");
            if (!superseding && change.State == EntityState.Modified && (change.Property(x => x.ExtractionResultHash).IsModified
                && !(change.Entity.Kind == "Extraction" && change.Entity.State == "Completed" && change.Property(x => x.State).OriginalValue == "Running")
                || change.Property(x => x.ExtractionRevision).IsModified && !(change.Entity.Kind == "Extraction" && change.Entity.State == "Running")
                || change.Entity.Kind == "Extraction" && change.Property(x => x.State).OriginalValue is "Completed" or "Superseded"))
                throw new TimelineFactConflictException("Extraction completion evidence is immutable.");
            if (change.State == EntityState.Added && change.Entity.Kind == "Extraction")
            {
                var job = change.Entity;
                var source = await TimelineEntries.FindAsync([job.EntryId], ct)
                    ?? throw new TimelineFactConflictException("Job source missing.");
                await Timelines.FindAsync([source.TimelineId], ct);
                var currentIds = await TimelineFactJobs.Where(x => x.EntryId == job.EntryId && x.Kind == "Extraction"
                    && x.State != "Superseded").Select(x => x.Id).ToListAsync(ct);
                var snapshot = await ReadExtractionInputAsync(job, source, ct);
                if (await TimelineFactJobs.AnyAsync(x => currentIds.Contains(x.Id) && x.SourceFingerprint == job.SourceFingerprint
                    && x.Id != snapshot.PredecessorId, ct))
                    throw new TimelineFactConflictException("Current generation evidence must be carried before supersession.");
                if (!TimelineFactEvidence.ExtractionKey(job) || job.FactId is not null
                    || currentIds.Any(id => !ChangeTracker.Entries<TimelineFactJob>().Any(x => x.Entity.Id == id && x.Entity.State == "Superseded"))
                    || ChangeTracker.Entries<TimelineFactJob>().Count(x => x.State == EntityState.Added && x.Entity.EntryId == job.EntryId && x.Entity.Kind == "Extraction") != 1
                    || await TimelineFacts.AnyAsync(x => x.EntryId == job.EntryId, ct)
                    || await TimelineFactProposals.AnyAsync(x => x.EntryId == job.EntryId && x.Status == "Accepted", ct))
                    throw new TimelineFactConflictException("Generation requires one current job and an unlocked source.");
            }
            change.Entity.Version = Guid.NewGuid();
            if (change.State == EntityState.Added || change.Entity.Kind == "Extraction" && change.Entity.State == "Completed")
            {
                var source = await TimelineEntries.FindAsync([change.Entity.EntryId], ct)
                    ?? throw new TimelineFactConflictException("Job source missing.");
                if (TimelineFactEvidence.Fingerprint(source) != change.Entity.SourceFingerprint)
                    throw new TimelineFactConflictException("Job source changed.");
                timelineIds.Add(source.TimelineId);
            }
        }
        // Every source/effective-state mutation CASes the same timeline row, even when no existing fact was touched.
        foreach (var id in timelineIds)
        {
            var timeline = await Timelines.FindAsync([id], ct) ?? throw new TimelineFactConflictException("Timeline missing.");
            var entry = Entry(timeline);
            if (entry.State != EntityState.Added)
                timeline.FactRevision = checked(entry.Property(x => x.FactRevision).OriginalValue + 1);
        }
    }
}
