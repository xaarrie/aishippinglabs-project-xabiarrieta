using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.DataAccessLogic;
using Xunit;

namespace ProjectTimeline.Tests;

// These test domain behavior and EF's InMemory concurrency tokens, NOT relational transactions or isolation.
public sealed class TimelineFactServiceTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string name = Guid.NewGuid().ToString();
        private readonly InMemoryDatabaseRoot root = new();
        public ProjectTimelineDbContext Db { get; }
        public User Actor { get; } = new() { Email = "facts@example.test", DisplayName = "Author", PasswordHash = "test" };
        public Project Project { get; } = new() { Code = "P", Name = "Project" };
        public Timeline Timeline { get; } = new() { Code = "T", Name = "Timeline" };
        public TimelineEntry Entry { get; } = new() { EntryNumber = 1, Title = "Original", Content = "Alice owns delivery. Bob owns testing." };
        public TimelineFactScope Scope => new(Project.Id, Timeline.Id, Entry.Id);
        public TimelineFactService Service => new(Db);
        public Fixture() => Db = NewContext();
        public ProjectTimelineDbContext NewContext() => new(new DbContextOptionsBuilder<ProjectTimelineDbContext>()
            .UseInMemoryDatabase(name, root).Options);
        public DbContext NewUnguardedContext() => new(new DbContextOptionsBuilder()
            .UseInMemoryDatabase(name, root).UseModel(Db.Model).Options);
        public async Task InitializeAsync()
        {
            Project.OwnerId = Actor.Id;
            Timeline.ProjectId = Project.Id;
            Entry.TimelineId = Timeline.Id;
            Entry.AuthorId = Actor.Id;
            Db.AddRange(Actor, Project, Timeline, Entry);
            await Db.SaveChangesAsync();
        }
        public async Task<TimelineFactProposal> PrepareAsync(TimelineFactDraft? draft = null, TimelineFactScope? scope = null)
        {
            var selected = scope ?? Scope;
            var revision = await Db.Timelines.AsNoTracking().Where(x => x.Id == selected.TimelineId).Select(x => x.FactRevision).SingleAsync();
            return await Service.PrepareAsync(selected, Actor.Id, false, revision, draft ?? Draft());
        }
        public async Task<TimelineFact> AcceptFactAsync()
        {
            var proposal = await PrepareAsync();
            await Service.AcceptAsync(Scope, proposal.Id, Actor.Id, false);
            return await Db.TimelineFacts.SingleAsync();
        }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
    private static TimelineFactDraft Draft() => new([new("Alice owns delivery", "alice", "Person", "Alice owns delivery.")], [], []);

    [Fact]
    public async Task Pending_is_not_effective_acceptance_is_idempotent_and_keeps_source_and_provenance()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var fingerprint = TimelineFactEvidence.Fingerprint(f.Entry);
        var proposal = await f.PrepareAsync();
        Assert.Equal("Pending", proposal.Status);
        Assert.Empty(await f.Service.EffectiveAsync(f.Scope, f.Actor.Id, false));
        Assert.Empty(await f.Db.TimelineFacts.ToListAsync());
        await f.Service.AcceptAsync(f.Scope, proposal.Id, f.Actor.Id, false);
        var revision = f.Timeline.FactRevision;
        await f.Service.AcceptAsync(f.Scope, proposal.Id, f.Actor.Id, false);
        var fact = Assert.Single(await f.Service.EffectiveAsync(f.Scope, f.Actor.Id, false));
        Assert.Equal(proposal.Id, fact.CreationReviewId);
        Assert.Equal(f.Entry.Id, fact.EntryId);
        Assert.Equal(fingerprint, fact.SourceFingerprint);
        Assert.Equal("Alice owns delivery.", fact.SourceQuote);
        Assert.Equal("alice", fact.EntityKey);
        Assert.Equal("Person", fact.EntityType);
        Assert.Equal(revision, f.Timeline.FactRevision);
        Assert.Single(await f.Db.TimelineFactJobs.ToListAsync());
        await f.Db.Entry(f.Entry).ReloadAsync();
        Assert.Equal("Original", f.Entry.Title);
        Assert.Equal("Alice owns delivery. Bob owns testing.", f.Entry.Content);
    }

    [Fact]
    public async Task Acceptance_applies_invalidation_and_replacement_together_once()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var old = await f.AcceptFactAsync();
        var proposal = await f.PrepareAsync(new([new("Bob owns testing", "bob", "Person", "Bob owns testing.")], [new(old.Id, "Replaced", 0)], []));
        Assert.True(old.Valid);
        Assert.Equal(old.Id, Assert.Single(await f.Service.EffectiveAsync(f.Scope, f.Actor.Id, false)).Id);
        await f.Service.AcceptAsync(f.Scope, proposal.Id, f.Actor.Id, false);
        Assert.False(old.Valid);
        Assert.NotNull(old.InvalidatedAtUtc);
        Assert.Equal(proposal.Id, old.InvalidationReviewId);
        Assert.Equal("Bob owns testing", Assert.Single(await f.Service.EffectiveAsync(f.Scope, f.Actor.Id, false)).Claim);
        await f.Service.AcceptAsync(f.Scope, proposal.Id, f.Actor.Id, false);
        Assert.Equal(2, await f.Db.TimelineFacts.CountAsync());
    }

    [Fact]
    public async Task Newly_accepted_fact_stales_proposal_even_without_overlapping_invalidations()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var first = await f.PrepareAsync();
        var second = await f.PrepareAsync();
        await f.Service.AcceptAsync(f.Scope, second.Id, f.Actor.Id, false);
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => f.Service.AcceptAsync(f.Scope, first.Id, f.Actor.Id, false));
        Assert.Single(await f.Db.TimelineFacts.ToListAsync());
        Assert.Equal("Pending", first.Status);
    }

    [Fact]
    public async Task Unanswered_questions_block_acceptance_and_answer_quotes_keep_clarification_evidence()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var pending = await f.PrepareAsync(Draft() with { Questions = [new("Who approved?", null)] });
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => f.Service.AcceptAsync(f.Scope, pending.Id, f.Actor.Id, false));
        Assert.Empty(await f.Db.TimelineFacts.ToListAsync());
        var answered = await f.PrepareAsync(new([new("Carol approved", "carol", "Person", "Carol approved", "Clarification")], [], [new("Who approved?", "Carol approved")]));
        await f.Service.AcceptAsync(f.Scope, answered.Id, f.Actor.Id, false);
        Assert.Contains("Carol approved", (await f.Db.TimelineFacts.SingleAsync()).ClarificationEvidence);
    }

    [Fact]
    public async Task Question_only_proposal_can_persist_but_cannot_be_accepted_as_ready()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var proposal = await f.PrepareAsync(new([], [], [new("What changed?", null)]));
        Assert.Equal("Pending", proposal.Status);
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => f.Service.AcceptAsync(f.Scope, proposal.Id, f.Actor.Id, false));
        Assert.Empty(await f.Db.TimelineFacts.ToListAsync());
    }

    [Fact]
    public async Task Foreign_unknown_and_future_fact_ids_and_wrong_scope_are_rejected()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var fact = await f.AcceptFactAsync();
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => f.PrepareAsync(Draft() with { Invalidations = [new(Guid.NewGuid(), "Unknown", 0)] }));
        var foreign = new Timeline { ProjectId = f.Project.Id, Code = "OTHER", Name = "Other" };
        var foreignEntry = new TimelineEntry { TimelineId = foreign.Id, EntryNumber = 1, Title = "Other", Content = f.Entry.Content, AuthorId = f.Actor.Id };
        var earlier = new TimelineEntry { TimelineId = f.Timeline.Id, EntryNumber = 0, Title = "Earlier", Content = f.Entry.Content, AuthorId = f.Actor.Id };
        f.Db.AddRange(foreign, foreignEntry, earlier); await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => f.PrepareAsync(Draft() with { Invalidations = [new(fact.Id, "Foreign", 0)] }, new(f.Project.Id, foreign.Id, foreignEntry.Id)));
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => f.PrepareAsync(Draft() with { Invalidations = [new(fact.Id, "Future", 0)] }, new(f.Project.Id, f.Timeline.Id, earlier.Id)));
        Assert.Empty(await f.Service.EffectiveAsync(new(f.Project.Id, f.Timeline.Id, earlier.Id), f.Actor.Id, false));
        var proposal = await f.PrepareAsync();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Service.AcceptAsync(new(f.Project.Id, foreign.Id, foreignEntry.Id), proposal.Id, f.Actor.Id, false));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Service.AcceptAsync(f.Scope with { EntryId = foreignEntry.Id }, proposal.Id, f.Actor.Id, false));
        Assert.Single(await f.Db.TimelineFacts.ToListAsync());
    }

    [Fact]
    public async Task Authorization_is_checked_even_for_idempotent_acceptance()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var fact = await f.AcceptFactAsync();
        var outsider = new User { Email = "outsider@example.test", DisplayName = "Outsider", PasswordHash = "test" };
        f.Db.Users.Add(outsider); await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.AcceptAsync(f.Scope, fact.CreationReviewId!.Value, outsider.Id, false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.EffectiveAsync(f.Scope, outsider.Id, false));
    }

    [Theory]
    [InlineData("Title")]
    [InlineData("Content")]
    [InlineData("EntryNumber")]
    [InlineData("TimelineId")]
    [InlineData("AuthorId")]
    [InlineData("Status")]
    public async Task Pending_evidence_protects_source_edits_in_all_save_overloads(string property)
    {
        await using var f = new Fixture(); await f.InitializeAsync(); await f.PrepareAsync();
        f.Db.Entry(f.Entry).Property(property).CurrentValue = property switch
        { "EntryNumber" => 9, "TimelineId" or "AuthorId" => Guid.NewGuid(), _ => "Changed" };
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => f.Db.SaveChangesAsync(true));
        Assert.Throws<TimelineFactConflictException>(() => f.Db.SaveChanges(true));
        await using var reader = f.NewContext();
        Assert.Equal("Original", (await reader.TimelineEntries.SingleAsync()).Title);
    }

    [Fact]
    public async Task Evidence_protects_delete_and_accepted_fact_content_and_proposal_content()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var fact = await f.AcceptFactAsync();
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => new TimelineService(f.Db).DeleteEntryAsync(f.Entry.Id, default));
        f.Db.ChangeTracker.Clear();
        fact = await f.Db.TimelineFacts.SingleAsync(); fact.Claim = "Rewritten";
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => f.Db.SaveChangesAsync());
        f.Db.ChangeTracker.Clear();
        var addition = await f.Db.TimelineFactAdditions.SingleAsync(); addition.SourceQuote = "Rewritten evidence";
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => f.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task Source_changed_outside_EF_is_rejected_at_acceptance_without_partial_changes()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var proposal = await f.PrepareAsync();
        // Deliberately use base DbContext with the same model/store to simulate an external write, without our SaveChanges guards.
        await using (var raw = f.NewUnguardedContext())
        {
            var source = await raw.Set<TimelineEntry>().SingleAsync();
            source.Content = "Changed outside the proposal";
            await raw.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => f.Service.AcceptAsync(f.Scope, proposal.Id, f.Actor.Id, false));
        Assert.Empty(await f.Db.TimelineFacts.ToListAsync());
    }

    [Fact]
    public async Task Timeline_CAS_detects_two_context_source_mutations_in_InMemory_only()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        await using var other = f.NewContext();
        var staleTimeline = await other.Timelines.SingleAsync();
        var source = await other.TimelineEntries.SingleAsync();
        f.Entry.Title = "First change"; await f.Db.SaveChangesAsync();
        source.Title = "Second change";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => other.SaveChangesAsync());
        Assert.True(staleTimeline.FactRevision > 0);
    }

    [Fact]
    public async Task Invalid_quotes_and_stale_planning_revision_are_rejected()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => f.PrepareAsync(new([new("Unsupported", "x", "Person", "not in source")], [], [])));
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => f.Service.PrepareAsync(f.Scope, f.Actor.Id, false, -1, Draft()));
        Assert.Empty(await f.Db.TimelineFactProposals.ToListAsync());
    }

    private static void StageAcceptance(Fixture f, TimelineFactProposal proposal, TimelineFact? old = null,
        bool addFacts = true, bool invalidate = true)
    {
        proposal.Status = "Accepted";
        proposal.AcceptedAtUtc = DateTime.UtcNow;
        proposal.AcceptedById = f.Actor.Id;
        if (invalidate && old is not null)
        {
            old.Valid = false;
            old.InvalidatedAtUtc = proposal.AcceptedAtUtc;
            old.InvalidationReviewId = proposal.Id;
            old.InvalidatedByFactId = proposal.Invalidations.Single(x => x.FactId == old.Id).InvalidatingAdditionId;
        }
        if (addFacts)
            foreach (var addition in proposal.Additions)
            {
                f.Db.TimelineFacts.Add(new TimelineFact
                {
                    Id = addition.Id, EntryId = proposal.EntryId, CreationReviewId = proposal.Id, AdditionId = addition.Id,
                    Claim = addition.Claim, EntityKey = addition.EntityKey, EntityType = addition.EntityType,
                    SourceQuote = addition.SourceQuote, SourceKind = addition.SourceKind,
                    SourceFingerprint = proposal.SourceFingerprint, ClarificationEvidence = proposal.ClarificationEvidence
                });
                f.Db.TimelineFactJobs.Add(new TimelineFactJob { EntryId = proposal.EntryId, FactId = addition.Id,
                    Kind = "Embedding", WorkKey = $"embedding:{addition.Id:N}", SourceFingerprint = proposal.SourceFingerprint });
            }
    }

    [Theory]
    [InlineData("metadata-only")]
    [InlineData("missing-addition")]
    [InlineData("missing-invalidation")]
    [InlineData("unanswered")]
    [InlineData("duplicate-addition")]
    [InlineData("stale")]
    [InlineData("wrong-provenance")]
    public async Task Direct_context_cannot_accept_incomplete_unready_or_unrelated_payload(string scenario)
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var old = await f.AcceptFactAsync();
        var draft = new TimelineFactDraft([new("Bob owns testing", "bob", "Person", "Bob owns testing.")],
            [new(old.Id, "Replaced", 0)], scenario == "unanswered" ? [new("Who approved?", null)] : []);
        var proposal = await f.PrepareAsync(draft);
        if (scenario == "stale") await f.PrepareAsync();
        StageAcceptance(f, proposal, old, addFacts: scenario is not ("missing-addition" or "metadata-only"),
            invalidate: scenario is not ("missing-invalidation" or "metadata-only"));
        if (scenario == "duplicate-addition")
        {
            var extra = f.Db.ChangeTracker.Entries<TimelineFact>().Single(x => x.State == EntityState.Added).Entity;
            f.Db.TimelineFacts.Add(new TimelineFact { EntryId = extra.EntryId, CreationReviewId = extra.CreationReviewId,
                AdditionId = extra.AdditionId, Claim = extra.Claim, EntityKey = extra.EntityKey, EntityType = extra.EntityType,
                SourceQuote = extra.SourceQuote, SourceFingerprint = extra.SourceFingerprint, ClarificationEvidence = extra.ClarificationEvidence });
        }
        if (scenario == "wrong-provenance")
            f.Db.ChangeTracker.Entries<TimelineFact>().Single(x => x.State == EntityState.Added).Entity.SourceQuote = "Unrelated quote";
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => f.Db.SaveChangesAsync());
        await using var reader = f.NewContext();
        Assert.Equal("Pending", (await reader.TimelineFactProposals.SingleAsync(x => x.Id == proposal.Id)).Status);
        Assert.True((await reader.TimelineFacts.SingleAsync()).Valid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_context_acceptance_rechecks_foreign_and_future_invalidation_scope(bool foreign)
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var timeline = foreign ? new Timeline { ProjectId = f.Project.Id, Code = "OTHER", Name = "Other" } : f.Timeline;
        var entry = new TimelineEntry { TimelineId = timeline.Id, EntryNumber = 2, Title = "Later", Content = f.Entry.Content, AuthorId = f.Actor.Id };
        if (foreign) f.Db.Timelines.Add(timeline);
        f.Db.TimelineEntries.Add(entry); await f.Db.SaveChangesAsync();
        var scope = new TimelineFactScope(f.Project.Id, timeline.Id, entry.Id);
        var accepted = await f.PrepareAsync(scope: scope);
        await f.Service.AcceptAsync(scope, accepted.Id, f.Actor.Id, false);
        var target = await f.Db.TimelineFacts.SingleAsync();
        await f.Db.Entry(f.Timeline).ReloadAsync();
        // Direct persistence can create a pending row; acceptance must still validate its server-side scope.
        var proposal = new TimelineFactProposal
        {
            EntryId = f.Entry.Id, ExpectedRevision = f.Timeline.FactRevision + 1,
            SourceFingerprint = TimelineFactEvidence.Fingerprint(f.Entry), ClarificationEvidence = "[]",
        };
        var cause = new TimelineFactAddition { ProposalId = proposal.Id, Claim = "Alice owns delivery", EntityKey = "alice",
            EntityType = "Person", SourceQuote = "Alice owns delivery." };
        proposal.Additions.Add(cause);
        proposal.Invalidations.Add(new() { ProposalId = proposal.Id, FactId = target.Id,
            InvalidatingAdditionId = cause.Id, Reason = "Outside scope" });
        f.Db.TimelineFactProposals.Add(proposal); await f.Db.SaveChangesAsync();
        StageAcceptance(f, proposal, target);
        var error = await Assert.ThrowsAsync<TimelineFactConflictException>(() => f.Db.SaveChangesAsync());
        Assert.Contains("scope", error.Message);
        await using var reader = f.NewContext();
        Assert.True((await reader.TimelineFacts.SingleAsync()).Valid);
        Assert.Equal("Pending", (await reader.TimelineFactProposals.SingleAsync(x => x.Id == proposal.Id)).Status);
    }

    [Fact]
    public async Task Direct_context_cannot_add_unrelated_invalidation_to_complete_payload()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var old = await f.AcceptFactAsync();
        var second = await f.PrepareAsync();
        await f.Service.AcceptAsync(f.Scope, second.Id, f.Actor.Id, false);
        var unrelated = await f.Db.TimelineFacts.SingleAsync(x => x.Id != old.Id);
        var proposal = await f.PrepareAsync(new([new("Bob owns testing", "bob", "Person", "Bob owns testing.")], [new(old.Id, "Replaced", 0)], []));
        StageAcceptance(f, proposal, old);
        unrelated.Valid = false; unrelated.InvalidatedAtUtc = proposal.AcceptedAtUtc; unrelated.InvalidationReviewId = proposal.Id;
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => f.Db.SaveChangesAsync());
        await using var reader = f.NewContext();
        Assert.Equal(2, await reader.TimelineFacts.CountAsync(x => x.Valid));
    }

    [Fact]
    public async Task Direct_context_complete_acceptance_still_succeeds()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var old = await f.AcceptFactAsync();
        var proposal = await f.PrepareAsync(new([new("Bob owns testing", "bob", "Person", "Bob owns testing.")], [new(old.Id, "Replaced", 0)], []));
        StageAcceptance(f, proposal, old);
        await f.Db.SaveChangesAsync();
        Assert.Equal("Bob owns testing", Assert.Single(await f.Service.EffectiveAsync(f.Scope, f.Actor.Id, false)).Claim);
    }

    [Fact]
    public async Task Extraction_job_has_retry_lease_and_version_state_and_protects_source()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var job = new TimelineFactJob { EntryId = f.Entry.Id, WorkKey = $"extract:{f.Entry.Id:N}", SourceFingerprint = TimelineFactEvidence.Fingerprint(f.Entry) };
        f.Db.TimelineFactJobs.Add(job); await f.Db.SaveChangesAsync();
        var version = job.Version;
        job.Attempts = 1; job.State = "Retry"; job.NextAttemptAtUtc = DateTime.UtcNow.AddMinutes(1);
        job.LeaseOwner = "worker"; job.LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(2); job.LastErrorCode = "provider_timeout";
        await f.Db.SaveChangesAsync();
        Assert.NotEqual(version, job.Version);
        f.Entry.Content = "Changed";
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => f.Db.SaveChangesAsync());
    }
}
