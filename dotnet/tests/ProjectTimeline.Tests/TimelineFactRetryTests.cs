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
        public ProjectTimelineDbContext Db() => new(new DbContextOptionsBuilder<ProjectTimelineDbContext>()
            .UseInMemoryDatabase(database, root).Options);
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
            return [new("Alice owns delivery", "alice", "Person", "Alice owns delivery.")];
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
    public async Task Retry_reuses_job_increments_attempts_saves_directly_and_never_replays()
    {
        await using var f = new Fixture(); await f.Initialize();
        await f.Run(); await f.Run();
        Assert.Equal("Saved", f.Results[0].Outcome); Assert.Equal(1, f.Calls);
        Assert.Equal("ExistingWork", f.Results[1].Outcome);
        await using var db = f.Db();
        var job = await db.TimelineFactJobs.SingleAsync(x => x.Id == f.Job.Id);
        Assert.Equal(2, job.Attempts); Assert.Equal("Completed", job.State);
        Assert.Empty(await db.TimelineFactProposals.ToListAsync());
        Assert.Empty(await db.TimelineFactAdditions.ToListAsync());
        var fact = Assert.Single(await db.TimelineFacts.ToListAsync());
        Assert.Equal(job.Id, fact.ExtractionJobId);
        Assert.Null(fact.CreationReviewId);
        Assert.Null(fact.AdditionId);
        Assert.Equal(f.Entry.Content, fact.SourceQuote);
        Assert.Equal("Pending", (await db.TimelineFactJobs.SingleAsync(x => x.Kind == "Embedding")).State);
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
    [InlineData("competing")]
    [InlineData("unrelated")]
    public async Task Ineligible_work_is_untouched(string reason)
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
                case "proposal": case "accepted":
                    db.Add(new TimelineFactProposal { EntryId = f.Entry.Id, Status = reason == "accepted" ? "Accepted" : "Pending" }); break;
                case "competing": db.Add(new TimelineFactJob { EntryId = f.Entry.Id, Kind = "Embedding", WorkKey = "other" }); break;
                case "unrelated": (await db.Set<TimelineEntry>().SingleAsync()).TimelineId = Guid.NewGuid(); break;
                default: job.State = reason; break;
            }
            await db.SaveChangesAsync();
        }
        await f.Run();
        Assert.Equal(0, f.Creations); Assert.Equal(0, f.Calls);
        await using var check = f.Db();
        Assert.Equal(1, (await check.TimelineFactJobs.SingleAsync(x => x.Id == f.Job.Id)).Attempts);
        Assert.DoesNotContain(f.Results, x => x.Outcome is "Saved" or "Accepted");
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
    public async Task Rechecks_before_paid_call_and_before_direct_persistence(string change)
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
