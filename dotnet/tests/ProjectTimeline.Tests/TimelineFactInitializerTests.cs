using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.DataAccessLogic;
using ProjectTimeline.FactsInitializer;
using Xunit;
using InitializerProgram = ProjectTimeline.FactsInitializer.Program;

namespace ProjectTimeline.Tests;

public sealed class TimelineFactInitializerTests
{
    private sealed class Provider : ITimelineFactProvider
    {
        public string ExtractionModel => "fake";
        public int Calls;
        public Func<Task>? DuringExtraction { get; set; }
        public IReadOnlyList<TimelineFactCandidate> Claims = [new("Alice owns delivery", "alice", "Person", "Alice owns delivery.")];

        public async Task<IReadOnlyList<TimelineFactCandidate>> ExtractAsync(TimelineFactExtraction source, CancellationToken ct)
        {
            Calls++;
            if (DuringExtraction is not null) await DuringExtraction();
            return Claims;
        }

        public Task<float[]> EmbedAsync(string claim, CancellationToken ct) => throw new InvalidOperationException("Must not drain embeddings.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string database = Guid.NewGuid().ToString();
        private readonly InMemoryDatabaseRoot root = new();
        public Func<Task>? AfterCompletion { get; set; }
        public ProjectTimelineDbContext NewContext()
        {
            var db = new ProjectTimelineDbContext(new DbContextOptionsBuilder<ProjectTimelineDbContext>()
                .UseInMemoryDatabase(database, root).Options);
            db.SavedChanges += (_, _) =>
            {
                if (AfterCompletion is { } callback && db.ChangeTracker.Entries<TimelineFactJob>()
                    .Any(x => x.Entity.Kind == "Extraction" && x.Entity.State == "Completed"))
                { AfterCompletion = null; callback().GetAwaiter().GetResult(); }
            };
            return db;
        }
        // Historical rows are deliberately seeded outside current write guards, never by weakening them.
        public DbContext Raw()
        {
            using var db = NewContext();
            return new(new DbContextOptionsBuilder().UseInMemoryDatabase(database, root).UseModel(db.Model).Options);
        }
        public readonly User Actor = new() { Email = "initializer@example.test", DisplayName = "Author", PasswordHash = "test" };
        public readonly Provider Provider = new();
        public readonly List<EntryResult> Results = [];
        public readonly Project Project = new() { Code = "P", Name = "Project" };
        public readonly Timeline Timeline = new() { Code = "T", Name = "Timeline" };
        public int ProviderCreations;
        public FactInitializer Runner => new(NewContext, () => { ProviderCreations++; return Provider; });

        public async Task InitializeAsync()
        {
            await using var db = NewContext();
            Project.OwnerId = Actor.Id;
            Timeline.ProjectId = Project.Id;
            var role = new UserRole { RoleName = "User" };
            Actor.IDUserRole = role.Id;
            db.AddRange(role, Actor, Project, Timeline);
            await db.SaveChangesAsync();
        }

        public async Task<TimelineEntry> AddAsync(int number, Guid? timelineId = null)
        {
            await using var db = NewContext();
            var entry = new TimelineEntry
            {
                TimelineId = timelineId ?? Timeline.Id, AuthorId = Actor.Id, EntryNumber = number,
                Title = "Title", Content = "Alice owns delivery."
            };
            db.Add(entry); // Legacy entries deliberately have no extraction jobs.
            await db.SaveChangesAsync();
            return entry;
        }

        public Task RunAsync(bool apply = true, int pageSize = 1, CancellationToken ct = default) =>
            Runner.RunAsync(new(Actor.Id, apply, pageSize), Results.Add, ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Apply_saves_pending_candidates_and_repeat_never_bills_or_drains_embeddings()
    {
        await using var f = new Fixture();
        await f.InitializeAsync();
        var entry = await f.AddAsync(1);
        await f.RunAsync();
        Assert.Equal("Saved", Assert.Single(f.Results).Outcome);
        await using var db = f.NewContext();
        Assert.Empty(await db.TimelineFacts.ToListAsync());
        var proposal = Assert.Single(await db.TimelineFactProposals.ToListAsync());
        var addition = Assert.Single(await db.TimelineFactAdditions.ToListAsync());
        var extraction = await db.TimelineFactJobs.SingleAsync();
        Assert.Equal("Completed", extraction.State);
        Assert.Equal(extraction.Id, proposal.Id);
        Assert.Equal("Generated", proposal.Status);
        Assert.Equal(proposal.Id, addition.ProposalId);
        Assert.Equal(TimelineFactEvidence.Fingerprint(entry), proposal.SourceFingerprint);
        Assert.Equal(TimelineFactEvidence.ResultHash([TimelineFactEvidence.CandidateEvidence(addition)]), extraction.ExtractionResultHash);
        Assert.Empty(await new TimelineFactService(db).EffectiveAsync(new(f.Project.Id, f.Timeline.Id, entry.Id), f.Actor.Id, false));
        Assert.Empty(await db.TimelineFactJobs.Where(x => x.Kind == "Embedding").ToListAsync());
        Assert.Equal(entry.Content, (await db.TimelineEntries.SingleAsync()).Content);
        await f.RunAsync();
        Assert.Equal(1, f.Provider.Calls);
        Assert.Equal("ExistingWork", f.Results[^1].Outcome);
        Assert.Single(await db.TimelineFactAdditions.ToListAsync());
        Assert.Empty(await db.TimelineFacts.ToListAsync());
    }

    [Theory]
    [InlineData("source")]
    [InlineData("proposal")]
    [InlineData("job")]
    [InlineData("hash")]
    [InlineData("missing-hash")]
    [InlineData("fingerprint")]
    [InlineData("revision")]
    [InlineData("accepted-zero")]
    [InlineData("regenerated")]
    public async Task Completion_reporting_never_counts_stale_or_superseded_candidates(string change)
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var entry = await f.AddAsync(1);
        f.AfterCompletion = () => GeneratedCompletionChanges.ApplyAsync(f.NewContext, f.Raw,
            new(f.Project.Id, f.Timeline.Id, entry.Id), f.Actor.Id, change);
        await f.RunAsync();
        Assert.Equal("Stale", Assert.Single(f.Results).Outcome);
        Assert.Equal(1, f.Provider.Calls);
        await using var db = f.NewContext(); Assert.Empty(await db.TimelineFacts.ToListAsync());
    }

    [Fact]
    public async Task Historical_effective_extraction_is_untouched_without_provider_calls()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var entry = await f.AddAsync(1);
        var job = TimelineFactProcessing.NewExtractionJob(entry);
        var fact = new TimelineFact { EntryId = entry.Id, ExtractionJobId = job.Id, SourceFingerprint = job.SourceFingerprint,
            Claim = "Alice owns delivery", EntityKey = "alice", EntityType = "Person", SourceQuote = entry.Content };
        fact.CandidateKey = TimelineFactEvidence.CandidateKey(fact);
        job.State = "Completed"; job.ExtractionResultHash = TimelineFactEvidence.ResultHash([fact]);
        await using (var historical = f.Raw()) { historical.AddRange(job, fact); await historical.SaveChangesAsync(); }
        await f.RunAsync();
        Assert.Empty(f.Results); Assert.Equal(0, f.ProviderCreations); Assert.Equal(0, f.Provider.Calls);
        await using var db = f.NewContext();
        Assert.Equal(job.Version, (await db.TimelineFactJobs.SingleAsync()).Version);
        Assert.Equal(fact.Id, Assert.Single(await new TimelineFactService(db).EffectiveAsync(new(f.Project.Id, f.Timeline.Id, entry.Id), f.Actor.Id, false)).Id);
        Assert.Empty(await db.TimelineFactProposals.ToListAsync());
    }

    [Fact]
    public async Task Dry_run_is_read_only_and_does_not_even_construct_provider()
    {
        await using var f = new Fixture();
        await f.InitializeAsync();
        await f.AddAsync(1);
        await using var db = f.NewContext();
        var revision = await db.Timelines.Select(x => x.FactRevision).SingleAsync();
        await f.RunAsync(apply: false);
        Assert.Equal("WouldSave", Assert.Single(f.Results).Outcome);
        Assert.Equal(0, f.ProviderCreations);
        Assert.Empty(await db.TimelineFactJobs.ToListAsync());
        Assert.Empty(await db.TimelineFactProposals.ToListAsync());
        Assert.Empty(await db.TimelineFacts.ToListAsync());
        Assert.Equal(revision, await db.Timelines.AsNoTracking().Select(x => x.FactRevision).SingleAsync());
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Running")]
    [InlineData("Retry")]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("Skipped")]
    public async Task Existing_jobs_are_never_resumed_or_changed(string state)
    {
        await using var f = new Fixture();
        await f.InitializeAsync();
        var entry = await f.AddAsync(1);
        await using var db = f.NewContext();
        var job = TimelineFactProcessing.NewExtractionJob(entry);
        job.State = state;
        if (state == "Completed")
        {
            await using var historical = f.Raw();
            historical.Add(job);
            await historical.SaveChangesAsync();
        }
        else
        {
            db.Add(job);
            await db.SaveChangesAsync();
        }
        var version = job.Version;
        await f.RunAsync();
        Assert.Equal("ExistingWork", Assert.Single(f.Results).Outcome);
        Assert.Equal(0, f.ProviderCreations);
        var stored = await db.TimelineFactJobs.AsNoTracking().SingleAsync();
        Assert.Equal(state, stored.State);
        Assert.Equal(version, stored.Version);
        Assert.Equal(0, stored.Attempts);
    }

    [Theory]
    [InlineData(false, "ExistingWork")]
    [InlineData(true, "Questions")]
    public async Task Preexisting_proposals_are_reported_never_accepted_or_answered(bool questions, string outcome)
    {
        await using var f = new Fixture();
        await f.InitializeAsync();
        var entry = await f.AddAsync(1);
        await using var db = f.NewContext();
        var revision = await db.Timelines.Select(x => x.FactRevision).SingleAsync();
        var proposal = await new TimelineFactService(db).PrepareAsync(new(f.Project.Id, f.Timeline.Id, entry.Id),
            f.Actor.Id, false, revision, questions ? new([], [], [new("Who owns delivery?", null)]) : new(f.Provider.Claims, [], []));
        await f.RunAsync();
        Assert.Equal(outcome, Assert.Single(f.Results).Outcome);
        Assert.Equal(0, f.ProviderCreations);
        var stored = await db.TimelineFactProposals.AsNoTracking().SingleAsync();
        Assert.Equal("Pending", stored.Status);
        Assert.Equal(proposal.QuestionsJson, stored.QuestionsJson);
        Assert.Empty(await db.TimelineFacts.ToListAsync());
        Assert.Empty(await db.TimelineFactJobs.ToListAsync());
    }

    [Fact]
    public async Task Invalid_physical_facts_disqualify_an_entry()
    {
        await using var f = new Fixture();
        await f.InitializeAsync();
        var entry = await f.AddAsync(1);
        await f.RunAsync();
        await using var db = f.NewContext();
        var service = new TimelineFactService(db);
        var accepted = await service.PrepareAsync(new(f.Project.Id, f.Timeline.Id, entry.Id), f.Actor.Id, false,
            await db.Timelines.Select(x => x.FactRevision).SingleAsync(), new(f.Provider.Claims, [], []));
        await service.AcceptAsync(new(f.Project.Id, f.Timeline.Id, entry.Id), accepted.Id, f.Actor.Id, false);
        var fact = await db.TimelineFacts.SingleAsync();
        // Put the replacement in a later entry so the original entry has ONLY an invalid
        // physical fact: a valid same-entry replacement would mask this eligibility check.
        var correction = await f.AddAsync(2);
        var revision = await db.Timelines.Select(x => x.FactRevision).SingleAsync();
        var scope = new TimelineFactScope(f.Project.Id, f.Timeline.Id, correction.Id);
        var proposal = await new TimelineFactService(db).PrepareAsync(scope, f.Actor.Id, false, revision,
            new([new("Bob owns delivery.", "bob", "Person", "Bob owns delivery.", "Clarification")],
                [new(fact.Id, "Owner corrected", 0)], [new("Who owns delivery now?", "Bob owns delivery.")]));
        await new TimelineFactService(db).AcceptAsync(scope, proposal.Id, f.Actor.Id, false);
        var original = await db.TimelineFacts.AsNoTracking().SingleAsync(x => x.EntryId == entry.Id);
        Assert.False(original.Valid);
        Assert.Equal(proposal.Additions.Single().Id, original.InvalidatedByFactId);
        f.Results.Clear();
        await f.RunAsync();
        Assert.Empty(f.Results);
        Assert.Equal(1, f.Provider.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_inactive_actor_is_rejected_before_provider(bool missing)
    {
        await using var f = new Fixture();
        await f.InitializeAsync();
        await f.AddAsync(1);
        await using var db = f.NewContext();
        var actor = await db.Users.SingleAsync();
        actor.IsActive = false;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Runner.RunAsync(
            new(missing ? Guid.NewGuid() : f.Actor.Id, true), f.Results.Add));
        Assert.Equal(0, f.ProviderCreations);
        Assert.Empty(await db.TimelineFactJobs.ToListAsync());
    }

    [Theory]
    [InlineData("Reader", false)]
    [InlineData("User", false)]
    [InlineData("Admin", true)]
    public async Task Administrator_is_derived_from_database_and_normal_write_access_is_enforced(string role, bool allowed)
    {
        await using var f = new Fixture();
        await f.InitializeAsync();
        await f.AddAsync(1);
        await using var db = f.NewContext();
        (await db.UserRoles.SingleAsync()).RoleName = role;
        (await db.Projects.SingleAsync()).OwnerId = Guid.NewGuid();
        db.Add(new ProjectParticipant { ProjectId = f.Project.Id, UserId = f.Actor.Id, ProjectRoleName = "Reader" });
        await db.SaveChangesAsync();
        await f.RunAsync();
        Assert.Equal(allowed ? "Saved" : "AccessDenied", Assert.Single(f.Results).Outcome);
        Assert.Equal(allowed ? 1 : 0, f.Provider.Calls);
        Assert.Empty(await db.TimelineFacts.ToListAsync());
        Assert.Equal(allowed ? 1 : 0, await db.TimelineFactAdditions.CountAsync());
    }

    [Fact]
    public async Task Revoked_access_during_extraction_prevents_candidate_persistence()
    {
        await using var f = new Fixture();
        await f.InitializeAsync();
        await f.AddAsync(1);
        f.Provider.DuringExtraction = async () =>
        {
            await using var change = f.NewContext();
            (await change.Users.SingleAsync()).IsActive = false;
            await change.SaveChangesAsync();
        };
        await f.RunAsync();
        Assert.Equal("AccessDenied", Assert.Single(f.Results).Outcome);
        await using var db = f.NewContext();
        Assert.Empty(await db.TimelineFactProposals.ToListAsync());
        Assert.Empty(await db.TimelineFacts.ToListAsync());
        Assert.Equal("Retry", (await db.TimelineFactJobs.SingleAsync()).State);
    }

    [Fact]
    public async Task Revoked_invoking_contributor_cannot_persist_using_still_authorized_source_author()
    {
        await using var f = new Fixture();
        await f.InitializeAsync();
        await f.AddAsync(1);
        var contributor = new User { Email = "contributor@example.test", DisplayName = "Contributor", PasswordHash = "test" };
        await using (var db = f.NewContext())
        {
            contributor.IDUserRole = (await db.UserRoles.SingleAsync()).Id;
            db.AddRange(contributor, new ProjectParticipant { ProjectId = f.Project.Id,
                UserId = contributor.Id, ProjectRoleName = "Contributor" });
            await db.SaveChangesAsync();
        }
        f.Provider.DuringExtraction = async () =>
        {
            await using var change = f.NewContext();
            (await change.Users.SingleAsync(x => x.Id == contributor.Id)).IsActive = false;
            await change.SaveChangesAsync();
        };
        await f.Runner.RunAsync(new(contributor.Id, true), f.Results.Add);
        Assert.Equal("AccessDenied", Assert.Single(f.Results).Outcome);
        await using var completed = f.NewContext();
        Assert.True((await completed.Users.SingleAsync(x => x.Id == f.Actor.Id)).IsActive);
        Assert.Empty(await completed.TimelineFacts.ToListAsync());
        Assert.Empty(await completed.TimelineFactProposals.ToListAsync());
        Assert.Equal("Retry", (await completed.TimelineFactJobs.SingleAsync()).State);
    }

    [Theory]
    [InlineData("Creator")]
    [InlineData("Contributor")]
    public async Task Normal_project_writers_do_not_need_administrator_role(string projectRole)
    {
        await using var f = new Fixture();
        await f.InitializeAsync();
        await f.AddAsync(1);
        await using var db = f.NewContext();
        (await db.Projects.SingleAsync()).OwnerId = Guid.NewGuid();
        db.Add(new ProjectParticipant { ProjectId = f.Project.Id, UserId = f.Actor.Id, ProjectRoleName = projectRole });
        await db.SaveChangesAsync();
        await f.RunAsync();
        Assert.Equal("Saved", Assert.Single(f.Results).Outcome);
    }

    [Fact]
    public async Task Empty_extraction_is_reported_and_not_rebilled_on_repeat()
    {
        await using var f = new Fixture();
        await f.InitializeAsync();
        await f.AddAsync(1);
        f.Provider.Claims = [];
        await f.RunAsync();
        Assert.Equal("Empty", Assert.Single(f.Results).Outcome);
        await f.RunAsync();
        Assert.Equal("ExistingWork", f.Results[^1].Outcome);
        Assert.Equal(1, f.Provider.Calls);
        await using var db = f.NewContext();
        Assert.Equal("Generated", (await db.TimelineFactProposals.SingleAsync()).Status);
        Assert.Empty(await db.TimelineFactAdditions.ToListAsync());
        Assert.Empty(await db.TimelineFacts.ToListAsync());
    }

    [Fact]
    public async Task Per_entry_provider_failure_is_sanitized_and_does_not_stop_later_entries()
    {
        await using var f = new Fixture();
        await f.InitializeAsync();
        await f.AddAsync(1);
        await f.AddAsync(2);
        f.Provider.DuringExtraction = () => f.Provider.Calls == 1
            ? throw new Exception("PRIVATE provider payload") : Task.CompletedTask;
        await f.RunAsync();
        Assert.Equal(["ExtractionFailed", "Saved"], f.Results.Select(x => x.Outcome));
        await using var db = f.NewContext();
        Assert.Equal("Unavailable", (await db.TimelineFactJobs.SingleAsync(x => x.State == "Retry")).LastErrorCode);
        Assert.Empty(await db.TimelineFacts.ToListAsync());
        Assert.Single(await db.TimelineFactProposals.ToListAsync());
        Assert.Single(await db.TimelineFactAdditions.ToListAsync());
    }

    [Fact]
    public async Task Invalid_batch_reports_payload_free_diagnostic_without_saving_partial_facts()
    {
        await using var f = new Fixture();
        await f.InitializeAsync();
        await f.AddAsync(1);
        f.Provider.Claims = [f.Provider.Claims[0], new("PRIVATE claim", "PRIVATE key", "Person", "PRIVATE unsupported quote")];
        await f.RunAsync();
        var result = Assert.Single(f.Results);
        Assert.Equal("ExtractionFailed", result.Outcome);
        Assert.Equal("UnsupportedQuote", result.DiagnosticCode);
        Assert.DoesNotContain("PRIVATE", result.ToString());
        await using var db = f.NewContext();
        Assert.Empty(await db.TimelineFacts.ToListAsync());
        Assert.Empty(await db.TimelineFactProposals.ToListAsync());
        var job = Assert.Single(await db.TimelineFactJobs.ToListAsync());
        Assert.Equal("Retry", job.State);
        Assert.Equal("InvalidResponse", job.LastErrorCode);
    }

    [Fact]
    public async Task Concurrent_proposal_is_preserved_and_no_extraction_proposal_is_created()
    {
        await using var f = new Fixture();
        await f.InitializeAsync();
        var entry = await f.AddAsync(1);
        f.Provider.DuringExtraction = async () =>
        {
            await using var change = f.NewContext();
            var revision = await change.Timelines.Select(x => x.FactRevision).SingleAsync();
            await new TimelineFactService(change).PrepareAsync(new(f.Project.Id, f.Timeline.Id, entry.Id),
                f.Actor.Id, false, revision, new([], [], [new("Clarify?", null)]));
        };
        await f.RunAsync();
        Assert.Equal("Stale", Assert.Single(f.Results).Outcome);
        await using var db = f.NewContext();
        Assert.Equal(1, await db.TimelineFactProposals.CountAsync());
        Assert.All(await db.TimelineFactProposals.ToListAsync(), x => Assert.Equal("Pending", x.Status));
        Assert.Empty(await db.TimelineFacts.ToListAsync());
    }

    [Fact]
    public async Task Concurrent_accepted_fact_disqualifies_entry_even_when_our_extraction_completes()
    {
        await using var f = new Fixture();
        await f.InitializeAsync();
        var entry = await f.AddAsync(1);
        f.Provider.DuringExtraction = async () =>
        {
            await using var change = f.NewContext();
            var revision = await change.Timelines.Select(x => x.FactRevision).SingleAsync();
            var scope = new TimelineFactScope(f.Project.Id, f.Timeline.Id, entry.Id);
            var proposal = await new TimelineFactService(change).PrepareAsync(scope, f.Actor.Id, false, revision,
                new(f.Provider.Claims, [], []));
            await new TimelineFactService(change).AcceptAsync(scope, proposal.Id, f.Actor.Id, false);
        };
        await f.RunAsync();
        Assert.Equal("Stale", Assert.Single(f.Results).Outcome);
        await using var db = f.NewContext();
        Assert.Single(await db.TimelineFacts.ToListAsync());
        Assert.Empty(await db.TimelineFactProposals.Where(x => x.Status == "Pending").ToListAsync());
        Assert.Single(await db.TimelineFactProposals.Where(x => x.Status == "Accepted").ToListAsync());
        Assert.All(await db.TimelineFacts.ToListAsync(), x => Assert.Null(x.ExtractionJobId));
    }

    [Fact]
    public async Task Source_changed_after_page_selection_is_skipped_before_extraction()
    {
        await using var f = new Fixture();
        await f.InitializeAsync();
        await f.AddAsync(1);
        var second = await f.AddAsync(2);
        f.Provider.DuringExtraction = async () =>
        {
            await using var change = f.NewContext();
            (await change.TimelineEntries.SingleAsync(x => x.Id == second.Id)).Content = "Changed source.";
            await change.SaveChangesAsync();
        };
        await f.RunAsync(pageSize: 2);
        Assert.Equal(["ExtractionFailed", "Stale"], f.Results.Select(x => x.Outcome));
        Assert.Equal(1, f.Provider.Calls);
        await using var db = f.NewContext();
        Assert.Empty(await db.TimelineFacts.ToListAsync());
        Assert.Empty(await db.TimelineFactProposals.ToListAsync());
        Assert.Equal("Retry", (await db.TimelineFactJobs.SingleAsync()).State);
    }

    [Fact]
    public async Task Pagination_orders_entries_and_defers_entries_beyond_fixed_upper_bound()
    {
        await using var f = new Fixture();
        await f.InitializeAsync();
        var third = await f.AddAsync(3);
        var first = await f.AddAsync(1);
        var second = await f.AddAsync(2);
        f.Provider.DuringExtraction = async () =>
        {
            if (f.Provider.Calls == 1) await f.AddAsync(4);
        };
        await f.RunAsync(pageSize: 2);
        Assert.Equal([first.Id, second.Id, third.Id], f.Results.Select(x => x.EntryId));
        // The concurrent insert changes the timeline revision: D1 rejects the first completion.
        Assert.Equal(["ExtractionFailed", "Saved", "Saved"], f.Results.Select(x => x.Outcome));
        await using (var db = f.NewContext())
        {
            Assert.Empty(await db.TimelineFacts.Where(x => x.EntryId == first.Id).ToListAsync());
            Assert.Equal("Retry", (await db.TimelineFactJobs.SingleAsync(x => x.EntryId == first.Id)).State);
            Assert.Empty(await db.TimelineFactProposals.Where(x => x.EntryId == first.Id).ToListAsync());
            Assert.Equal(2, await db.TimelineFactProposals.CountAsync(x => x.Status == "Generated"));
        }
        await f.RunAsync();
        Assert.Equal(4, f.Provider.Calls); // New entry processed; first entry's retry is not silently resumed.
    }

    [Fact]
    public async Task Timeline_keyset_pages_are_bounded_and_do_not_drain_an_unrelated_job()
    {
        await using var f = new Fixture();
        await f.InitializeAsync();
        await using var db = f.NewContext();
        var secondTimeline = new Timeline { ProjectId = f.Project.Id, Code = "T2", Name = "Second" };
        db.Add(secondTimeline);
        await db.SaveChangesAsync();
        var a = await f.AddAsync(1);
        var b = await f.AddAsync(1, secondTimeline.Id);
        var unrelated = await f.AddAsync(2, secondTimeline.Id);
        db.ChangeTracker.Clear();
        var job = TimelineFactProcessing.NewExtractionJob(unrelated);
        db.Add(job);
        await db.SaveChangesAsync();
        await f.RunAsync();
        Assert.Equal(2, f.Provider.Calls);
        Assert.Contains(f.Results, x => x.EntryId == a.Id && x.Outcome == "Saved");
        Assert.Contains(f.Results, x => x.EntryId == b.Id && x.Outcome == "Saved");
        Assert.Equal("Pending", (await db.TimelineFactJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id)).State);
    }

    [Fact]
    public async Task Cancellation_during_provider_call_preserves_retry_job_and_stops_selection()
    {
        await using var f = new Fixture();
        await f.InitializeAsync();
        await f.AddAsync(1);
        await f.AddAsync(2);
        using var cts = new CancellationTokenSource();
        f.Provider.DuringExtraction = () => { cts.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.RunAsync(ct: cts.Token));
        Assert.Equal(1, f.Provider.Calls);
        await using var db = f.NewContext();
        Assert.Equal("Retry", (await db.TimelineFactJobs.SingleAsync()).State);
        Assert.Empty(await db.TimelineFactProposals.ToListAsync());
        Assert.Empty(await db.TimelineFacts.ToListAsync());
    }

    [Fact]
    public void Command_defaults_to_dry_run_and_rejects_admin_overrides_and_duplicate_options()
    {
        var actor = Guid.NewGuid().ToString();
        Assert.Equal(new(Guid.Parse(actor), false, 50), InitializerCommand.Parse(["--actor-id", actor]));
        Assert.True(InitializerCommand.Parse(["--actor-id", actor, "--apply"]).Apply);
        Assert.Throws<ArgumentException>(() => InitializerCommand.Parse(["--actor-id", actor, "--administrator"]));
        Assert.Throws<ArgumentException>(() => InitializerCommand.Parse(["--actor-id", actor, "--apply", "--apply"]));
        Assert.Throws<ArgumentException>(() => InitializerCommand.Parse(["--actor-id", actor, "--page-size", "101"]));
        Assert.Throws<ArgumentException>(() => InitializerCommand.Parse(["--actor-id", Guid.Empty.ToString()]));
        Assert.Throws<ArgumentException>(() => InitializerCommand.Parse([]));
    }

    [Fact]
    public async Task Missing_connection_has_no_fallback_and_errors_never_echo_arguments_or_credentials()
    {
        using var output = new StringWriter();
        Assert.Equal(1, await InitializerProgram.ExecuteAsync(["--actor-id", Guid.NewGuid().ToString()], _ => null, output));
        Assert.Contains("Configuration invalid", output.ToString());
        output.GetStringBuilder().Clear();
        Assert.Equal(1, await InitializerProgram.ExecuteAsync(["PRIVATE-ARGUMENT"], _ => "PRIVATE-CREDENTIAL", output));
        Assert.DoesNotContain("PRIVATE", output.ToString());
        output.GetStringBuilder().Clear();
        Assert.Equal(1, await InitializerProgram.ExecuteAsync(["--actor-id", Guid.NewGuid().ToString()],
            _ => "PRIVATE-CREDENTIAL", output));
        Assert.DoesNotContain("PRIVATE", output.ToString());
        output.GetStringBuilder().Clear();
        Assert.Equal(1, await InitializerProgram.ExecuteAsync(["--actor-id", Guid.NewGuid().ToString(), "--apply"],
            key => key == "ConnectionStrings__ProjectTimeline" ? "Host=example.invalid;Database=offline;Username=offline" : null, output));
        Assert.Contains("Configuration invalid", output.ToString());
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await InitializerProgram.ExecuteAsync(["--help"], _ => throw new Exception("Must not read environment"), output));
        Assert.Contains("WouldSave", output.ToString());
        Assert.Contains("Saved means nonempty pending evidence, not active facts", output.ToString());
        Assert.Contains("Activation requires explicit reconciliation acceptance elsewhere", output.ToString());
        Assert.Contains("never accepts or queues embeddings", output.ToString());
        Assert.DoesNotContain("automatic acceptance", output.ToString());
    }

    [Fact]
    public void Uuid_keyset_and_zero_physical_rows_query_translate_without_connecting()
    {
        using var db = new ProjectTimelineDbContext(new DbContextOptionsBuilder<ProjectTimelineDbContext>()
            .UseNpgsql("Host=example.invalid;Database=offline;Username=offline").Options);
        var cursor = Guid.NewGuid();
        var upper = Guid.NewGuid();
        var sql = db.Timelines.Where(x => x.Id.CompareTo(cursor) > 0 && x.Id.CompareTo(upper) <= 0)
            .OrderBy(x => x.Id).Take(2).ToQueryString();
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("LIMIT", sql);
        var entriesSql = db.TimelineEntries.Where(x => !db.TimelineFacts.Any(f => f.EntryId == x.Id)).ToQueryString();
        Assert.Contains("TimelineEntryFacts", entriesSql);
        Assert.Contains("NOT EXISTS", entriesSql);
        Assert.DoesNotContain("\"Valid\"", entriesSql);
    }
}

internal static class GeneratedCompletionChanges
{
    // Inject after the worker's successful durable save but before the runner reads its outcome.
    public static async Task ApplyAsync(Func<ProjectTimelineDbContext> create, Func<DbContext> raw,
        TimelineFactScope scope, Guid actor, string change)
    {
        if (change == "source")
        {
            await using var db = create(); var source = await db.TimelineEntries.SingleAsync(x => x.Id == scope.EntryId);
            var content = source.Content; source.Content = "Changed"; await db.SaveChangesAsync();
            source.Content = content; await db.SaveChangesAsync();
        }
        else if (change == "accepted-zero")
        {
            await using var db = create(); var service = new TimelineFactService(db);
            var proposal = await service.PrepareAsync(scope, actor, false,
                await db.Timelines.Where(x => x.Id == scope.TimelineId).Select(x => x.FactRevision).SingleAsync(), new([], [], []));
            await service.AcceptAsync(scope, proposal.Id, actor, false);
        }
        else if (change == "regenerated")
        {
            await using var db = create(); var id = await db.TimelineFactJobs.Where(x => x.EntryId == scope.EntryId).Select(x => x.Id).SingleAsync();
            await new TimelineFactProcessing(create, new PendingGenerationProvider()).RequestGenerationAsync(scope, actor, false, true, id);
        }
        else
        {
            // Simulate inconsistent external writes without weakening production provenance guards.
            await using var db = raw(); var job = await db.Set<TimelineFactJob>().SingleAsync(x => x.EntryId == scope.EntryId);
            var proposal = await db.Set<TimelineFactProposal>().SingleAsync(x => x.Id == job.Id);
            switch (change)
            {
                case "proposal": proposal.Status = "Superseded"; break;
                case "job": job.State = "Superseded"; break;
                case "hash": job.ExtractionResultHash = "wrong"; break;
                case "missing-hash": job.ExtractionResultHash = null; break;
                case "fingerprint": proposal.SourceFingerprint = "wrong"; break;
                case "revision": proposal.ExpectedRevision++; break;
                default: throw new ArgumentException("Unknown test mutation.");
            }
            await db.SaveChangesAsync();
        }
    }
}
