using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Linq.Expressions;
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
        public TimelineEntry Entry { get; private set; } = new() { EntryNumber = 1, Title = "Original", Content = "Alice owns delivery. Bob owns testing." };
        public TimelineFactScope Scope => new(Project.Id, Timeline.Id, Entry.Id);
        public TimelineFactService Service => new(Db);
        public Fixture() => Db = NewContext();
        public ProjectTimelineDbContext NewContext() => NewContextWith();
        public ProjectTimelineDbContext NewContextWith(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<ProjectTimelineDbContext>()
            .UseInMemoryDatabase(name, root).AddInterceptors(interceptors).Options);
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
            return await Db.TimelineFacts.SingleAsync(x => x.CreationReviewId == proposal.Id);
        }
        public async Task NewSourceAsync()
        {
            Entry = new TimelineEntry { TimelineId = Timeline.Id, AuthorId = Actor.Id,
                EntryNumber = await Db.TimelineEntries.MaxAsync(x => x.EntryNumber) + 1, Title = Entry.Title, Content = Entry.Content };
            Db.Add(Entry); await Db.SaveChangesAsync();
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

    [Theory]
    [InlineData(false, "prepare")]
    [InlineData(true, "prepare")]
    [InlineData(false, "insert")]
    [InlineData(true, "insert")]
    [InlineData(false, "accept")]
    [InlineData(true, "accept")]
    [InlineData(false, "context-accept")]
    [InlineData(true, "context-accept")]
    public async Task Accepted_entry_rejects_second_reconciliation_at_service_and_context_boundaries(bool empty, string route)
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var accepted = await f.PrepareAsync(empty ? new([], [], []) : Draft());
        await f.Service.AcceptAsync(f.Scope, accepted.Id, f.Actor.Id, false);
        await f.Service.AcceptAsync(f.Scope, accepted.Id, f.Actor.Id, false); // The same accepted ID remains idempotent.
        if (route == "prepare")
            await Assert.ThrowsAsync<TimelineFactConflictException>(() => f.PrepareAsync(new([], [], [])));
        else
        {
            var proposal = new TimelineFactProposal { EntryId = f.Entry.Id,
                SourceFingerprint = TimelineFactEvidence.Fingerprint(f.Entry), ClarificationEvidence = "[]",
                ExpectedRevision = f.Timeline.FactRevision + (route == "insert" ? 1 : 0) };
            if (route == "insert")
            {
                f.Db.Add(proposal);
                await Assert.ThrowsAsync<TimelineFactConflictException>(() => f.Db.SaveChangesAsync());
            }
            else
            {
                // Previously persisted/corrupt pending evidence must not bypass an accepted-entry lock.
                await using (var raw = f.NewUnguardedContext()) { raw.Add(proposal); await raw.SaveChangesAsync(); }
                if (route == "accept")
                    await Assert.ThrowsAsync<TimelineFactConflictException>(() => f.Service.AcceptAsync(f.Scope, proposal.Id, f.Actor.Id, false));
                else
                {
                    f.Db.Attach(proposal); StageAcceptance(f, proposal);
                    await Assert.ThrowsAsync<TimelineFactConflictException>(() => f.Db.SaveChangesAsync());
                }
            }
        }
        await using var reader = f.NewContext();
        Assert.Equal(1, await reader.TimelineFactProposals.CountAsync(x => x.Status == "Accepted"));
        Assert.Equal(empty ? 0 : 1, await reader.TimelineFacts.CountAsync());
    }

    private sealed class BeforeSupersession(Action action) : IQueryExpressionInterceptor
    {
        public bool Fired { get; private set; }
        public Expression QueryCompilationStarting(Expression expression, QueryExpressionEventData eventData)
        {
            var text = expression.ToString();
            if (!Fired && text.Contains("\"Pending\"") && text.Contains("\"Generated\""))
            { Fired = true; action(); }
            return expression;
        }
    }

    [Fact]
    public async Task Timeline_move_reserves_original_CAS_before_jobless_zero_addition_acceptance_interleaves()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var target = new Timeline { ProjectId = f.Project.Id, Code = "TARGET", Name = "Target" };
        f.Db.Add(target); await f.Db.SaveChangesAsync();
        var proposal = await f.PrepareAsync(new([], [], []));
        var baseline = f.Timeline.FactRevision;
        var gate = new BeforeSupersession(() =>
        {
            using var accepting = f.NewContext();
            new TimelineFactService(accepting).AcceptAsync(f.Scope, proposal.Id, f.Actor.Id, false).GetAwaiter().GetResult();
        });
        await using var moving = f.NewContextWith(gate);
        (await moving.TimelineEntries.SingleAsync()).TimelineId = target.Id;
        var failure = await Record.ExceptionAsync(() => moving.SaveChangesAsync());
        Assert.True(gate.Fired); // Actual interleaving, not a static assertion about source text.
        Assert.IsType<DbUpdateConcurrencyException>(failure);
        Assert.Equal(baseline, moving.Entry((await moving.Timelines.FindAsync(f.Timeline.Id))!).Property(x => x.FactRevision).OriginalValue);
        await using var reader = f.NewContext();
        Assert.Equal("Accepted", (await reader.TimelineFactProposals.SingleAsync()).Status);
        Assert.Empty(await reader.TimelineFactJobs.ToListAsync()); Assert.Empty(await reader.TimelineFacts.ToListAsync());
        // InMemory CAS evidence only; relational atomic rollback is covered by the gated PostgreSQL case.
    }

    [Fact]
    public async Task Acceptance_applies_invalidation_and_replacement_together_once()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var old = await f.AcceptFactAsync();
        await f.NewSourceAsync();
        Assert.NotEqual(old.EntryId, f.Entry.Id);
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
        Assert.Equal("Superseded", first.Status);
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
        await f.NewSourceAsync();
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
    public async Task Pending_edits_supersede_evidence_and_restoring_source_never_revives_proposal_in_all_save_overloads(string property)
    {
        foreach (var mode in SaveModes)
        {
            await using var f = new Fixture(); await f.InitializeAsync();
            var otherTimeline = new Timeline { ProjectId = f.Project.Id, Code = "OTHER", Name = "Other" };
            var otherAuthor = new User { Email = "other@example.test", DisplayName = "Other", PasswordHash = "test" };
            f.Db.AddRange(otherTimeline, otherAuthor); await f.Db.SaveChangesAsync();
            var proposal = await f.PrepareAsync();
            var original = f.Db.Entry(f.Entry).Property(property).CurrentValue;
            var changed = property switch
            { "EntryNumber" => (object)9, "TimelineId" => otherTimeline.Id, "AuthorId" => otherAuthor.Id, _ => "Changed" };
            f.Db.Entry(f.Entry).Property(property).CurrentValue = changed;
            await SaveAsync(f.Db, mode);
            await using (var restore = f.NewContext())
            {
                var source = await restore.TimelineEntries.SingleAsync();
                Assert.Equal(changed, restore.Entry(source).Property(property).CurrentValue);
                Assert.Equal("Superseded", (await restore.TimelineFactProposals.SingleAsync()).Status);
                Assert.Empty(await restore.TimelineFacts.ToListAsync());
                Assert.Equal("Alice owns delivery.", (await restore.TimelineFactAdditions.SingleAsync()).SourceQuote);
                restore.Entry(source).Property(property).CurrentValue = original;
                await SaveAsync(restore, mode);
            }
            await Assert.ThrowsAsync<TimelineFactConflictException>(() => f.Service.AcceptAsync(f.Scope, proposal.Id, f.Actor.Id, false));
        }
    }

    private static readonly string[] SaveModes = ["async", "async-bool", "sync", "sync-bool"];
    private static async Task SaveAsync(ProjectTimelineDbContext db, string mode)
    {
        switch (mode)
        {
            case "async": await db.SaveChangesAsync(); break;
            case "async-bool": await db.SaveChangesAsync(true); break;
            case "sync": db.SaveChanges(); break;
            case "sync-bool": db.SaveChanges(true); break;
            default: throw new ArgumentException("Unknown test save mode.");
        }
    }

    [Theory]
    [InlineData("Title")]
    [InlineData("Content")]
    [InlineData("EntryNumber")]
    [InlineData("TimelineId")]
    [InlineData("AuthorId")]
    [InlineData("Status")]
    public async Task Accepted_evidence_keeps_source_locked_in_all_save_overloads(string property)
    {
        foreach (var mode in SaveModes)
        {
            await using var f = new Fixture(); await f.InitializeAsync(); await f.AcceptFactAsync();
            f.Db.Entry(f.Entry).Property(property).CurrentValue = property switch
            { "EntryNumber" => 9, "TimelineId" or "AuthorId" => Guid.NewGuid(), _ => "Changed" };
            await Assert.ThrowsAsync<TimelineFactConflictException>(() => SaveAsync(f.Db, mode));
            await using var reader = f.NewContext();
            Assert.Equal(TimelineFactEvidence.Fingerprint(await reader.TimelineEntries.SingleAsync()), (await reader.TimelineFactProposals.SingleAsync()).SourceFingerprint);
            Assert.Single(await reader.TimelineFacts.ToListAsync());
        }
    }

    [Theory]
    [InlineData("async")]
    [InlineData("async-bool")]
    [InlineData("sync")]
    [InlineData("sync-bool")]
    public async Task Accepted_zero_addition_reconciliation_also_locks_source(string mode)
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var proposal = await f.PrepareAsync(new([], [], []));
        await f.Service.AcceptAsync(f.Scope, proposal.Id, f.Actor.Id, false);
        f.Entry.Title = "Changed";
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => SaveAsync(f.Db, mode));
        await using var reader = f.NewContext();
        Assert.Equal("Original", (await reader.TimelineEntries.SingleAsync()).Title);
        Assert.Empty(await reader.TimelineFacts.ToListAsync());
        Assert.Equal("Accepted", (await reader.TimelineFactProposals.SingleAsync()).Status);
    }

    private sealed class EditingProvider(Func<Task> edit) : ITimelineFactProvider
    {
        public string ExtractionModel => "fake";
        public async Task<IReadOnlyList<TimelineFactCandidate>> ExtractAsync(TimelineFactExtraction source, CancellationToken ct)
        { await edit(); return Draft().Additions; }
        public Task<float[]> EmbedAsync(string claim, CancellationToken ct) => throw new InvalidOperationException("No embeddings before acceptance.");
    }

    [Theory]
    [InlineData("async")]
    [InlineData("async-bool")]
    [InlineData("sync")]
    [InlineData("sync-bool")]
    public async Task Pending_edit_revokes_inflight_worker_even_after_source_restoration(string mode)
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var job = TimelineFactProcessing.NewExtractionJob(f.Entry);
        f.Db.Add(job); await f.Db.SaveChangesAsync();
        var provider = new EditingProvider(async () =>
        {
            await using var edit = f.NewContext();
            var source = await edit.TimelineEntries.SingleAsync();
            var original = source.Content;
            source.Content = "Changed"; await SaveAsync(edit, mode);
            source.Content = original; await SaveAsync(edit, mode);
        });
        var worker = new TimelineFactProcessing(f.NewContext, provider);
        Assert.Equal("LostLease", await worker.ProcessAsync(job.Id, "old-worker"));
        Assert.Equal("Superseded", await worker.ProcessAsync(job.Id, "restart"));
        await using var reader = f.NewContext();
        Assert.Equal("Superseded", (await reader.TimelineFactJobs.SingleAsync()).State);
        Assert.Empty(await reader.TimelineFactProposals.ToListAsync());
        Assert.Empty(await reader.TimelineFacts.ToListAsync());
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
        await f.NewSourceAsync();
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
        Assert.Equal(scenario == "stale" ? "Superseded" : "Pending", (await reader.TimelineFactProposals.SingleAsync(x => x.Id == proposal.Id)).Status);
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
        await f.NewSourceAsync();
        var second = await f.PrepareAsync();
        await f.Service.AcceptAsync(f.Scope, second.Id, f.Actor.Id, false);
        var unrelated = await f.Db.TimelineFacts.SingleAsync(x => x.Id != old.Id);
        await f.NewSourceAsync();
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
        await f.NewSourceAsync();
        var proposal = await f.PrepareAsync(new([new("Bob owns testing", "bob", "Person", "Bob owns testing.")], [new(old.Id, "Replaced", 0)], []));
        StageAcceptance(f, proposal, old);
        await f.Db.SaveChangesAsync();
        Assert.Equal("Bob owns testing", Assert.Single(await f.Service.EffectiveAsync(f.Scope, f.Actor.Id, false)).Claim);
    }

    [Fact]
    public async Task Extraction_job_retains_retry_history_and_revokes_lease_on_pending_source_edit()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var job = new TimelineFactJob { EntryId = f.Entry.Id, WorkKey = $"extraction:{f.Entry.Id:N}", SourceFingerprint = TimelineFactEvidence.Fingerprint(f.Entry) };
        f.Db.TimelineFactJobs.Add(job); await f.Db.SaveChangesAsync();
        var version = job.Version;
        job.Attempts = 1; job.State = "Retry"; job.NextAttemptAtUtc = DateTime.UtcNow.AddMinutes(1);
        job.LeaseOwner = "worker"; job.LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(2); job.LastErrorCode = "provider_timeout";
        await f.Db.SaveChangesAsync();
        Assert.NotEqual(version, job.Version);
        f.Entry.Content = "Changed";
        await f.Db.SaveChangesAsync();
        Assert.Equal("Superseded", job.State);
        Assert.Null(job.LeaseOwner); Assert.Null(job.LeaseExpiresAtUtc);
        Assert.Equal(1, job.Attempts); Assert.Equal("provider_timeout", job.LastErrorCode);
        Assert.Empty(await f.Db.TimelineFacts.ToListAsync());
    }
}
