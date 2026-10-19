using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.DataAccessLogic;
using ProjectTimeline.FactsInitializer;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class TimelineFactRetryTests
{
    [Fact]
    public void Retry_requires_explicit_timeline_and_preserves_legacy_defaults()
    {
        var actor = Guid.NewGuid().ToString();
        var timeline = Guid.NewGuid();
        var options = InitializerCommand.ParseRetry(["--retry-existing", "--actor-id", actor, "--timeline-id", timeline.ToString()]);
        Assert.False(options.Apply);
        Assert.Equal(timeline, options.TimelineId);
        Assert.Throws<ArgumentException>(() => InitializerCommand.ParseRetry(["--retry-existing", "--actor-id", actor]));
        Assert.Throws<ArgumentException>(() => InitializerCommand.ParseRetry(["--actor-id", actor, "--timeline-id", timeline.ToString()]));
        Assert.Throws<ArgumentException>(() => InitializerCommand.Parse(["--actor-id", actor, "--timeline-id", timeline.ToString()]));
    }

    private sealed class Fixture : IAsyncDisposable, ITimelineFactProvider
    {
        private readonly string database = Guid.NewGuid().ToString();
        private readonly InMemoryDatabaseRoot root = new();
        public Func<Task>? AfterCompletion { get; set; }
        public ProjectTimelineDbContext Db()
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
        // Simulate legacy/stale rows or external concurrent writers without weakening production guards.
        public DbContext Raw()
        {
            using var db = Db();
            return new(new DbContextOptionsBuilder().UseInMemoryDatabase(database, root).UseModel(db.Model).Options);
        }
        public User Actor = new() { Email = "retry@example.test", DisplayName = "Writer", PasswordHash = "test" };
        public Timeline Timeline = new() { Code = "T", Name = "Timeline" };
        public TimelineEntry Entry = new() { EntryNumber = 1, Title = "Title", Content = "Alice owns delivery." };
        public TimelineFactJob Job = null!;
        public int Calls, Creations;
        public Func<Task>? DuringCall;
        public Func<Task>? DuringCreation;
        public Func<Task>? BeforeClaim;
        private int contextsAfterCreation;
        private ProjectTimelineDbContext RunnerContext()
        {
            if (Creations > 0 && ++contextsAfterCreation == 2 && BeforeClaim is not null)
                BeforeClaim().GetAwaiter().GetResult();
            return Db();
        }
        public List<EntryResult> Results = [];
        public string ExtractionModel => "fake";
        public IReadOnlyList<TimelineFactCandidate> Claims { get; set; } = [new("Alice owns delivery", "alice", "Person", "Alice owns delivery.")];
        public async Task Initialize()
        {
            await using var db = Db();
            var role = new UserRole { RoleName = "User" };
            Actor.IDUserRole = role.Id;
            var project = new Project { Code = "P", Name = "Project", OwnerId = Actor.Id };
            Timeline.ProjectId = project.Id;
            Entry.TimelineId = Timeline.Id; Entry.AuthorId = Actor.Id;
            Job = TimelineFactProcessing.NewExtractionJob(Entry);
            Job.State = "Retry"; Job.Model = "fake"; Job.Attempts = 1; Job.LastErrorCode = "InvalidResponse";
            db.AddRange(role, Actor, project, Timeline, Entry, Job);
            await db.SaveChangesAsync();
        }
        public async Task<Guid> RegeneratedRetry()
        {
            var worker = new TimelineFactProcessing(Db, this);
            var id = await worker.RequestGenerationAsync(new(Timeline.ProjectId, Timeline.Id, Entry.Id), Actor.Id, false, true, Job.Id);
            DuringCall = () => throw new TimelineFactProviderException("InvalidResponse");
            Assert.Equal("Retry", await worker.ProcessAsync(id, "failed-generation"));
            DuringCall = null;
            await using var elapsed = Raw();
            (await elapsed.Set<TimelineFactJob>().SingleAsync(x => x.Id == id)).NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(-1);
            await elapsed.SaveChangesAsync(); // Simulate elapsed backoff without changing the retry's identity or attempts.
            return id;
        }
        public Task Run(bool apply = true) => new TimelineFactRetryRunner(RunnerContext, () =>
        {
            Creations++;
            DuringCreation?.Invoke().GetAwaiter().GetResult();
            return this;
        }, "fake").RunAsync(new(Actor.Id, Timeline.Id, apply, 1), Results.Add);
        public async Task<IReadOnlyList<TimelineFactCandidate>> ExtractAsync(TimelineFactExtraction source, CancellationToken ct)
        {
            Calls++;
            if (DuringCall is not null) await DuringCall();
            return Claims;
        }
        public Task<float[]> EmbedAsync(string claim, CancellationToken ct) => throw new Exception("No embeddings");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Dry_run_preserves_all_durable_state_without_constructing_provider()
    {
        await using var f = new Fixture(); await f.Initialize();
        await f.Run(false);
        Assert.Equal("WouldRetry", Assert.Single(f.Results).Outcome);
        Assert.Equal(0, f.Creations); Assert.Equal(0, f.Calls);
        await using var db = f.Db();
        var job = await db.TimelineFactJobs.SingleAsync();
        Assert.Equal(f.Job.Version, job.Version); Assert.Equal(1, job.Attempts);
        Assert.Equal("InvalidResponse", job.LastErrorCode);
        Assert.Empty(await db.TimelineFactProposals.ToListAsync());
        Assert.Empty(await db.TimelineFacts.ToListAsync());
        Assert.Equal(0, (await db.Timelines.SingleAsync()).FactRevision);
    }

    [Fact]
    public async Task Retry_reuses_job_increments_attempts_saves_pending_candidates_and_never_replays()
    {
        await using var f = new Fixture(); await f.Initialize();
        await f.Run(); await f.Run();
        Assert.Equal("Saved", f.Results[0].Outcome); Assert.Equal(1, f.Calls);
        Assert.Equal("ExistingWork", f.Results[1].Outcome);
        await using var db = f.Db();
        var job = await db.TimelineFactJobs.SingleAsync(x => x.Id == f.Job.Id);
        Assert.Equal(2, job.Attempts); Assert.Equal("Completed", job.State);
        var proposal = Assert.Single(await db.TimelineFactProposals.ToListAsync());
        var addition = Assert.Single(await db.TimelineFactAdditions.ToListAsync());
        Assert.Equal(job.Id, proposal.Id);
        Assert.Equal("Generated", proposal.Status);
        Assert.Equal(proposal.Id, addition.ProposalId);
        Assert.Equal(f.Entry.Content, addition.SourceQuote);
        Assert.Equal(TimelineFactEvidence.Fingerprint(f.Entry), proposal.SourceFingerprint);
        Assert.Equal(TimelineFactEvidence.ResultHash([TimelineFactEvidence.CandidateEvidence(addition)]), job.ExtractionResultHash);
        Assert.Empty(await db.TimelineFacts.ToListAsync());
        Assert.Empty(await db.TimelineFactJobs.Where(x => x.Kind == "Embedding").ToListAsync());
    }

    [Fact]
    public async Task Regenerated_retry_selects_only_current_generation_preserves_history_and_reports_pending_saved()
    {
        await using var f = new Fixture(); await f.Initialize();
        var id = await f.RegeneratedRetry();
        await using var before = f.Db();
        var historical = await before.TimelineFactJobs.AsNoTracking().SingleAsync(x => x.Id == f.Job.Id);
        await f.Run(false);
        Assert.Equal("WouldRetry", Assert.Single(f.Results).Outcome);
        Assert.Equal(0, f.Creations); Assert.Equal(1, f.Calls);
        await f.Run(); await f.Run();
        Assert.Equal(["WouldRetry", "Saved", "ExistingWork"], f.Results.Select(x => x.Outcome));
        Assert.Equal(1, f.Creations); Assert.Equal(2, f.Calls);
        await using var db = f.Db();
        var old = await db.TimelineFactJobs.SingleAsync(x => x.Id == f.Job.Id);
        Assert.Equal("Superseded", old.State); Assert.Equal(historical.Version, old.Version);
        Assert.Equal(historical.Attempts, old.Attempts); Assert.Equal(historical.LastErrorCode, old.LastErrorCode);
        var current = await db.TimelineFactJobs.SingleAsync(x => x.Id == id);
        Assert.Equal(2, current.Attempts); Assert.Equal("Completed", current.State);
        Assert.Equal(id, (await db.TimelineFactProposals.SingleAsync()).Id);
        Assert.Equal("Generated", (await db.TimelineFactProposals.SingleAsync()).Status);
        Assert.Single(await db.TimelineFactAdditions.ToListAsync()); Assert.Empty(await db.TimelineFacts.ToListAsync());
        Assert.Empty(await db.TimelineFactJobs.Where(x => x.Kind == "Embedding").ToListAsync());
    }

    [Fact]
    public async Task Superseded_generated_candidates_do_not_turn_current_empty_retry_into_saved()
    {
        await using var f = new Fixture(); await f.Initialize();
        Assert.Equal("Completed", await new TimelineFactProcessing(f.Db, f).ProcessAsync(f.Job.Id, "old-generation"));
        var id = await f.RegeneratedRetry(); f.Claims = [];
        await f.Run();
        Assert.Equal("Empty", Assert.Single(f.Results).Outcome);
        await using var db = f.Db();
        Assert.Equal("Superseded", (await db.TimelineFactProposals.SingleAsync(x => x.Id == f.Job.Id)).Status);
        Assert.Single(await db.TimelineFactAdditions.Where(x => x.ProposalId == f.Job.Id).ToListAsync());
        Assert.Equal("Generated", (await db.TimelineFactProposals.SingleAsync(x => x.Id == id)).Status);
        Assert.Empty(await db.TimelineFactAdditions.Where(x => x.ProposalId == id).ToListAsync());
        Assert.Empty(await db.TimelineFacts.ToListAsync());
    }

    [Theory]
    [InlineData("ambiguous")]
    [InlineData("source")]
    [InlineData("key")]
    [InlineData("superseded")]
    public async Task Ambiguous_or_stale_regenerated_retry_never_constructs_provider_or_changes_history(string change)
    {
        await using var f = new Fixture(); await f.Initialize(); var id = await f.RegeneratedRetry();
        await using (var raw = f.Raw())
        {
            var current = await raw.Set<TimelineFactJob>().SingleAsync(x => x.Id == id);
            if (change == "source") (await raw.Set<TimelineEntry>().SingleAsync()).Content = "Changed";
            else if (change == "key") current.WorkKey = $"extraction:{f.Entry.Id:N}:{Guid.NewGuid():N}";
            else if (change == "superseded") current.State = "Superseded";
            else
            {
                var competing = TimelineFactProcessing.NewExtractionJob(f.Entry);
                competing.WorkKey = $"extraction:{f.Entry.Id:N}:{competing.Id:N}";
                competing.State = "Retry"; raw.Add(competing);
            }
            await raw.SaveChangesAsync();
        }
        await f.Run();
        Assert.Equal(change is "source" or "key" ? "Stale" : "ExistingWork", Assert.Single(f.Results).Outcome);
        Assert.Equal(0, f.Creations); Assert.Equal(1, f.Calls);
        await using var db = f.Db();
        Assert.Equal(1, (await db.TimelineFactJobs.SingleAsync(x => x.Id == id)).Attempts);
        Assert.Equal("Superseded", (await db.TimelineFactJobs.SingleAsync(x => x.Id == f.Job.Id)).State);
        Assert.Empty(await db.TimelineFactProposals.ToListAsync()); Assert.Empty(await db.TimelineFacts.ToListAsync());
    }

    [Fact]
    public async Task Empty_retry_is_durable_without_facts_embeddings_or_rebilling()
    {
        await using var f = new Fixture(); await f.Initialize(); f.Claims = [];
        await f.Run(); await f.Run();
        Assert.Equal(["Empty", "ExistingWork"], f.Results.Select(x => x.Outcome));
        Assert.Equal(1, f.Calls); Assert.Equal(1, f.Creations);
        await using var db = f.Db();
        Assert.Equal("Completed", (await db.TimelineFactJobs.SingleAsync()).State);
        Assert.Equal("Generated", (await db.TimelineFactProposals.SingleAsync()).Status);
        Assert.Empty(await db.TimelineFactAdditions.ToListAsync()); Assert.Empty(await db.TimelineFacts.ToListAsync());
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
        await using var f = new Fixture(); await f.Initialize();
        f.AfterCompletion = () => GeneratedCompletionChanges.ApplyAsync(f.Db, f.Raw,
            new(f.Timeline.ProjectId, f.Timeline.Id, f.Entry.Id), f.Actor.Id, change);
        await f.Run();
        Assert.Equal("Stale", Assert.Single(f.Results).Outcome); Assert.Equal(1, f.Calls);
        await using var db = f.Db(); Assert.Empty(await db.TimelineFacts.ToListAsync());
    }

    [Fact]
    public async Task Existing_accepted_fact_blocks_retry_even_if_job_is_retry()
    {
        await using var f = new Fixture(); await f.Initialize();
        await using (var db = f.Db())
        {
            var scope = new TimelineFactScope(f.Timeline.ProjectId, f.Timeline.Id, f.Entry.Id);
            var revision = (await db.Timelines.SingleAsync()).FactRevision;
            var proposal = await new TimelineFactService(db).PrepareAsync(scope, f.Actor.Id, false, revision,
                new([new("Alice owns delivery", "alice", "Person", f.Entry.Content)], [], []));
            await new TimelineFactService(db).AcceptAsync(scope, proposal.Id, f.Actor.Id, false);
        }
        await f.Run();
        Assert.Equal(0, f.Calls); Assert.Equal(0, f.Creations);
        await using var check = f.Db();
        var preserved = Assert.Single(await check.TimelineFacts.ToListAsync());
        Assert.Equal("Alice owns delivery", preserved.Claim);
        Assert.Null(preserved.ExtractionJobId);
        Assert.Equal("Accepted", (await check.TimelineFactProposals.SingleAsync()).Status);
        Assert.Equal("Pending", (await check.TimelineFactJobs.SingleAsync(x => x.Kind == "Embedding")).State);
        Assert.Equal(1, (await check.TimelineFactJobs.SingleAsync(x => x.Id == f.Job.Id)).Attempts);
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Superseded")]
    [InlineData("Failed")]
    [InlineData("Skipped")]
    [InlineData("Pending")]
    [InlineData("Running")]
    [InlineData("lease")]
    [InlineData("future")]
    [InlineData("source")]
    [InlineData("model")]
    [InlineData("proposal")]
    [InlineData("accepted")]
    [InlineData("generated")]
    [InlineData("superseded-proposal")]
    [InlineData("competing")]
    [InlineData("unrelated")]
    public async Task Ineligible_work_is_untouched_and_superseded_proposals_are_preserved(string reason)
    {
        await using var f = new Fixture(); await f.Initialize();
        await using (var db = f.Raw())
        {
            var job = await db.Set<TimelineFactJob>().SingleAsync();
            switch (reason)
            {
                case "lease": job.LeaseExpiresAtUtc = DateTime.UtcNow.AddHours(1); job.LeaseOwner = "other"; break;
                case "future": job.NextAttemptAtUtc = DateTime.UtcNow.AddHours(1); break;
                case "source": (await db.Set<TimelineEntry>().SingleAsync()).Content = "Changed"; break;
                case "model": job.Model = "other-model"; break;
                case "proposal": case "accepted": case "generated": case "superseded-proposal":
                    db.Add(new TimelineFactProposal { EntryId = f.Entry.Id, Status = reason switch
                    { "accepted" => "Accepted", "generated" => "Generated", "superseded-proposal" => "Superseded", _ => "Pending" } }); break;
                case "competing": db.Add(new TimelineFactJob { EntryId = f.Entry.Id, Kind = "Embedding", WorkKey = "other" }); break;
                case "unrelated": (await db.Set<TimelineEntry>().SingleAsync()).TimelineId = Guid.NewGuid(); break;
                default: job.State = reason; break;
            }
            await db.SaveChangesAsync();
        }
        await f.Run();
        await using var check = f.Db();
        if (reason == "superseded-proposal")
        {
            Assert.Equal(1, f.Creations); Assert.Equal(1, f.Calls);
            Assert.Equal("Saved", Assert.Single(f.Results).Outcome);
            Assert.Single(await check.TimelineFactProposals.Where(x => x.Status == "Superseded").ToListAsync());
            Assert.Single(await check.TimelineFactProposals.Where(x => x.Status == "Generated").ToListAsync());
            Assert.Equal(2, (await check.TimelineFactJobs.SingleAsync()).Attempts);
            Assert.Empty(await check.TimelineFacts.ToListAsync());
        }
        else
        {
            Assert.Equal(0, f.Creations); Assert.Equal(0, f.Calls);
            Assert.Equal(1, (await check.TimelineFactJobs.SingleAsync(x => x.Id == f.Job.Id)).Attempts);
            Assert.DoesNotContain(f.Results, x => x.Outcome is "Saved" or "Accepted");
        }
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Retry")]
    public async Task Changed_job_between_precheck_and_claim_cannot_be_paid_or_accepted(string state)
    {
        await using var f = new Fixture(); await f.Initialize();
        f.BeforeClaim = async () =>
        {
            await using var db = f.Db();
            var job = await db.TimelineFactJobs.SingleAsync();
            job.State = state; job.LastErrorCode = "Unavailable";
            await db.SaveChangesAsync(); // Changes the job concurrency token even if state remains Retry.
        };
        await f.Run();
        Assert.Equal(0, f.Calls);
        Assert.Equal("Stale", Assert.Single(f.Results).Outcome);
        await using var check = f.Db();
        Assert.Equal(1, (await check.TimelineFactJobs.SingleAsync()).Attempts);
        Assert.Empty(await check.TimelineFacts.ToListAsync());
    }

    [Theory]
    [InlineData("User", true, true)]
    [InlineData("User", false, false)]
    [InlineData("Admin", false, true)]
    public async Task Role_is_database_derived_and_active_actor_is_required(string role, bool owner, bool allowed)
    {
        await using var f = new Fixture(); await f.Initialize();
        await using (var db = f.Db())
        {
            (await db.UserRoles.SingleAsync()).RoleName = role;
            if (!owner) (await db.Projects.SingleAsync()).OwnerId = Guid.NewGuid();
            await db.SaveChangesAsync();
        }
        if (allowed) { await f.Run(); Assert.Equal(1, f.Calls); }
        else await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Run());
        await using (var db = f.Db())
        {
            (await db.Users.SingleAsync()).IsActive = false;
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Run());
    }

    [Fact]
    public async Task Failure_keeps_same_retry_history_and_continues_in_bounded_entry_order()
    {
        await using var f = new Fixture(); await f.Initialize();
        Guid secondId;
        await using (var db = f.Db())
        {
            var second = new TimelineEntry { TimelineId = f.Timeline.Id, AuthorId = f.Actor.Id, EntryNumber = 2,
                Title = "Title", Content = f.Entry.Content };
            secondId = second.Id;
            var job = TimelineFactProcessing.NewExtractionJob(second); job.State = "Retry";
            db.AddRange(second, job); await db.SaveChangesAsync();
        }
        f.DuringCall = () => f.Calls == 1 ? throw new TimelineFactProviderException("InvalidResponse") : Task.CompletedTask;
        await f.Run();
        Assert.Equal([f.Entry.Id, secondId], f.Results.Select(x => x.EntryId));
        Assert.Equal(["ExtractionFailed", "Saved"], f.Results.Select(x => x.Outcome));
        await using var check = f.Db();
        var failed = await check.TimelineFactJobs.SingleAsync(x => x.Id == f.Job.Id);
        Assert.Equal("Retry", failed.State); Assert.Equal(2, failed.Attempts);
        Assert.Equal("InvalidResponse", failed.LastErrorCode); Assert.True(failed.NextAttemptAtUtc > DateTime.UtcNow);
    }

    [Fact]
    public async Task Retry_reports_validation_diagnostic_but_retains_compatible_failure_category()
    {
        await using var f = new Fixture(); await f.Initialize();
        f.DuringCall = () => throw new TimelineFactProviderException("InvalidResponse", "ResponseTruncated");
        await f.Run();
        var result = Assert.Single(f.Results);
        Assert.Equal("ExtractionFailed", result.Outcome);
        Assert.Equal("ResponseTruncated", result.DiagnosticCode);
        await using var db = f.Db();
        var job = Assert.Single(await db.TimelineFactJobs.ToListAsync());
        Assert.Equal(f.Job.Id, job.Id);
        Assert.Equal(2, job.Attempts);
        Assert.Equal("Retry", job.State);
        Assert.Equal("InvalidResponse", job.LastErrorCode);
        Assert.Empty(await db.TimelineFacts.ToListAsync());
        Assert.Empty(await db.TimelineFactProposals.ToListAsync());
    }

    [Theory]
    [InlineData("access")]
    [InlineData("source")]
    [InlineData("competing")]
    public async Task Rechecks_before_paid_call_and_before_candidate_persistence(string change)
    {
        foreach (var beforeCall in new[] { true, false })
        {
            await using var f = new Fixture(); await f.Initialize();
            async Task Mutate()
            {
                await using var db = f.Raw();
                if (change == "access") (await db.Set<User>().SingleAsync()).IsActive = false;
                else if (change == "source") (await db.Set<TimelineEntry>().SingleAsync()).Content = "Changed";
                else db.Add(new TimelineFactJob { EntryId = f.Entry.Id, Kind = "Embedding", WorkKey = "competing" });
                await db.SaveChangesAsync();
            }
            if (beforeCall) f.DuringCreation = Mutate; else f.DuringCall = Mutate;
            await f.Run();
            Assert.Equal(beforeCall ? 0 : 1, f.Calls);
            if (change == "access") Assert.Equal("AccessDenied", Assert.Single(f.Results).Outcome);
            Assert.DoesNotContain(f.Results, x => x.Outcome is "Saved" or "Accepted");
            await using var check = f.Db();
            Assert.Empty(await check.TimelineFacts.ToListAsync());
        }
    }
}
