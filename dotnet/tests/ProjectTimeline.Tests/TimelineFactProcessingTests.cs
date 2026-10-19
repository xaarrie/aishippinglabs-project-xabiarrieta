using Microsoft.EntityFrameworkCore;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.DataAccessLogic;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class TimelineFactProcessingTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Value = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Value;
    }
    private sealed class FakeProvider : ITimelineFactProvider
    {
        public string ExtractionModel => "fake-model";
        public int Extractions, Embeddings;
        public Action? DuringExtraction;
        public Exception? Error;
        public IReadOnlyList<TimelineFactCandidate> Claims = [new("Alice owns delivery", "alice", "Person", "Alice owns delivery.")];
        public float[] Vector = Enumerable.Repeat(0.1f, 1536).ToArray();
        public Task<IReadOnlyList<TimelineFactCandidate>> ExtractAsync(TimelineFactExtraction source, CancellationToken ct)
        {
            Extractions++; DuringExtraction?.Invoke();
            if (Error is not null) throw Error;
            return Task.FromResult(Claims);
        }
        public Task<float[]> EmbedAsync(string claim, CancellationToken ct)
        { Embeddings++; if (Error is not null) throw Error; return Task.FromResult(Vector); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly DbContextOptions<ProjectTimelineDbContext> options = new DbContextOptionsBuilder<ProjectTimelineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        public ProjectTimelineDbContext Db { get; }
        public ProjectTimelineDbContext NewContext() => new(options);
        public readonly Clock Clock = new();
        public readonly FakeProvider Provider = new();
        public TimelineFactProcessing Worker => new(NewContext, Provider, Clock);
        public readonly User Actor = new() { Email = "worker@example.test", DisplayName = "Author", PasswordHash = "test" };
        public readonly Project Project = new() { Code = "P", Name = "Project" };
        public readonly Timeline Timeline = new() { Code = "T", Name = "Timeline" };
        public TimelineEntry Entry = null!;
        public TimelineFactScope Scope => new(Project.Id, Timeline.Id, Entry.Id);
        public Fixture() => Db = NewContext();
        public async Task<Guid> InitializeAsync()
        {
            Project.OwnerId = Actor.Id; Timeline.ProjectId = Project.Id;
            Db.AddRange(Actor, Project, Timeline); await Db.SaveChangesAsync();
            Entry = await new TimelineService(Db).AddEntryAsync(Timeline.Id, Actor.Id, "Title", "Alice owns delivery.", default);
            return await Db.TimelineFactJobs.Select(x => x.Id).SingleAsync();
        }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    [Fact]
    public async Task Extraction_is_direct_evidenced_and_replay_never_duplicates_facts_or_embeddings()
    {
        await using var f = new Fixture(); var id = await f.InitializeAsync();
        Assert.Equal("Completed", await f.Worker.ProcessAsync(id, "one"));
        Assert.Empty(await f.Db.TimelineFactProposals.ToListAsync());
        Assert.Empty(await f.Db.TimelineFactAdditions.ToListAsync());
        var direct = Assert.Single(await new TimelineFactService(f.Db).EffectiveAsync(f.Scope, f.Actor.Id, false));
        Assert.Equal("Alice owns delivery.", direct.SourceQuote);
        Assert.Equal(0, f.Provider.Embeddings);
        for (var i = 0; i < 3; i++) Assert.Equal("Completed", await f.Worker.ProcessAsync(id, "restarted"));
        Assert.Equal(1, f.Provider.Extractions);
        Assert.Empty(await f.Db.TimelineFactProposals.ToListAsync());
        Assert.Single(await f.Db.TimelineFacts.ToListAsync());
        await using var acceptance = f.NewContext();
        var embedId = await acceptance.TimelineFactJobs.Where(x => x.Kind == "Embedding").Select(x => x.Id).SingleAsync();
        Assert.Equal("Completed", await f.Worker.ProcessAsync(embedId, "embedder"));
        var fact = await acceptance.TimelineFacts.AsNoTracking().SingleAsync();
        Assert.NotNull(fact.EmbeddedAtUtc); Assert.Equal("text-embedding-3-small", fact.EmbeddingModel);
        Assert.Equal(1, f.Provider.Embeddings);
        Assert.Equal("Alice owns delivery.", (await acceptance.TimelineEntries.AsNoTracking().SingleAsync()).Content);
    }

    [Fact]
    public async Task Retry_retains_error_backoff_and_recovers_with_direct_facts()
    {
        await using var f = new Fixture(); var id = await f.InitializeAsync();
        f.Provider.Error = new Exception("PRIVATE provider payload");
        Assert.Equal("Retry", await f.Worker.ProcessAsync(id, "worker"));
        var failed = await f.Db.TimelineFactJobs.AsNoTracking().SingleAsync();
        Assert.Equal("Unavailable", failed.LastErrorCode); Assert.Equal(1, failed.Attempts);
        Assert.Equal("NotDue", await f.Worker.ProcessAsync(id, "worker"));
        Assert.Empty(await f.Db.TimelineFactProposals.ToListAsync());
        f.Clock.Value = f.Clock.Value.AddHours(1); f.Provider.Error = null;
        Assert.Equal("Completed", await f.Worker.ProcessAsync(id, "worker"));
        Assert.Single(await f.Db.TimelineFacts.ToListAsync());
    }

    [Fact]
    public async Task Expired_lease_discards_remote_result_and_can_be_reclaimed_after_crash()
    {
        await using var f = new Fixture(); var id = await f.InitializeAsync();
        f.Provider.DuringExtraction = () => f.Clock.Value = f.Clock.Value.AddMinutes(4);
        Assert.Equal("LostLease", await f.Worker.ProcessAsync(id, "old"));
        Assert.Empty(await f.Db.TimelineFactProposals.ToListAsync());
        f.Provider.DuringExtraction = null;
        Assert.Equal("Completed", await f.Worker.ProcessAsync(id, "new"));
        Assert.Single(await f.Db.TimelineFacts.ToListAsync());
    }

    [Fact]
    public async Task Changed_durable_version_prevents_old_owner_from_committing_result()
    {
        await using var f = new Fixture(); var id = await f.InitializeAsync();
        f.Provider.DuringExtraction = () =>
        {
            using var competing = f.NewContext();
            var job = competing.TimelineFactJobs.Single(x => x.Id == id);
            job.LeaseOwner = "recovered-owner";
            competing.SaveChanges(); // Guards generate a new concurrency version.
        };
        Assert.Equal("LostLease", await f.Worker.ProcessAsync(id, "original-owner"));
        Assert.Empty(await f.Db.TimelineFactProposals.ToListAsync());
        f.Provider.DuringExtraction = null; f.Clock.Value = f.Clock.Value.AddMinutes(4);
        Assert.Equal("Completed", await f.Worker.ProcessAsync(id, "recovery"));
        Assert.Single(await f.Db.TimelineFacts.ToListAsync());
    }

    [Fact]
    public async Task Active_lease_excludes_second_worker_and_retry_budget_is_bounded()
    {
        await using var f = new Fixture(); var id = await f.InitializeAsync();
        f.Provider.DuringExtraction = () => Assert.Equal("NotDue", f.Worker.ProcessAsync(id, "second").GetAwaiter().GetResult());
        f.Provider.Error = new TimelineFactProviderException("RateLimited");
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            Assert.Equal(attempt == 8 ? "Failed" : "Retry", await f.Worker.ProcessAsync(id, "worker"));
            f.Clock.Value = f.Clock.Value.AddHours(2);
        }
        Assert.Equal("Failed", await f.Worker.ProcessAsync(id, "restart"));
        Assert.Equal(8, f.Provider.Extractions);
        Assert.Empty(await f.Db.TimelineFactProposals.ToListAsync());
    }

    [Fact]
    public async Task Invalidation_before_embedding_skips_provider_and_keeps_accepted_provenance()
    {
        await using var f = new Fixture(); var id = await f.InitializeAsync();
        await f.Worker.ProcessAsync(id, "worker");
        await using var db = f.NewContext();
        var fact = await db.TimelineFacts.SingleAsync();
        var embedId = await db.TimelineFactJobs.Where(x => x.Kind == "Embedding").Select(x => x.Id).SingleAsync();
        var revision = await db.Timelines.Select(x => x.FactRevision).SingleAsync();
        var retraction = await new TimelineFactService(db).PrepareAsync(f.Scope, f.Actor.Id, false, revision,
            new([new("Bob owns delivery.", "bob", "Person", "Bob owns delivery.", "Clarification")],
                [new(fact.Id, "Owner corrected", 0)], [new("Who owns delivery now?", "Bob owns delivery.")]));
        await new TimelineFactService(db).AcceptAsync(f.Scope, retraction.Id, f.Actor.Id, false);
        Assert.Equal("Skipped", await f.Worker.ProcessAsync(embedId, "embedder"));
        Assert.Equal(0, f.Provider.Embeddings);
        var original = await db.TimelineFacts.AsNoTracking().SingleAsync(x => x.Id == fact.Id);
        var replacement = await db.TimelineFacts.AsNoTracking().SingleAsync(x => x.CreationReviewId == retraction.Id);
        Assert.Null(original.EmbeddedAtUtc);
        Assert.Equal(id, original.ExtractionJobId);
        Assert.Null(original.CreationReviewId);
        Assert.Equal(replacement.Id, original.InvalidatedByFactId);
        Assert.Equal(retraction.Id, original.InvalidationReviewId);
        Assert.True(replacement.Valid);
    }

    [Fact]
    public async Task Cancellation_releases_work_for_retry_without_losing_source()
    {
        await using var f = new Fixture(); var id = await f.InitializeAsync();
        using var cts = new CancellationTokenSource();
        f.Provider.DuringExtraction = cts.Cancel;
        Assert.Equal("Cancelled", await f.Worker.ProcessAsync(id, "worker", cts.Token));
        Assert.Equal("Retry", (await f.Db.TimelineFactJobs.AsNoTracking().SingleAsync()).State);
        Assert.Empty(await f.Db.TimelineFactProposals.ToListAsync());
        Assert.Single(await f.Db.TimelineEntries.ToListAsync());
    }

    [Fact]
    public async Task Unsupported_evidence_retries_and_empty_extraction_completes_without_active_facts()
    {
        await using var f = new Fixture(); var id = await f.InitializeAsync();
        f.Provider.Claims = [new("Invented", "alice", "Person", "Not in source")];
        Assert.Equal("Retry", await f.Worker.ProcessAsync(id, "worker"));
        Assert.Equal("InvalidResponse", (await f.Db.TimelineFactJobs.AsNoTracking().SingleAsync()).LastErrorCode);
        f.Clock.Value = f.Clock.Value.AddHours(1); f.Provider.Claims = [];
        Assert.Equal("Completed", await f.Worker.ProcessAsync(id, "worker"));
        Assert.Empty(await f.Db.TimelineFacts.ToListAsync()); Assert.Empty(await f.Db.TimelineFactProposals.ToListAsync());
    }

    [Theory]
    [InlineData("quote", "UnsupportedQuote")]
    [InlineData("key", "CandidateShape")]
    [InlineData("origin", "CandidateShape")]
    [InlineData("duplicate", "DuplicateCandidate")]
    public async Task Invalid_batches_emit_only_safe_diagnostics_and_never_commit_a_valid_prefix(string variant, string diagnostic)
    {
        await using var f = new Fixture(); var id = await f.InitializeAsync();
        var valid = f.Provider.Claims[0];
        var invalid = variant switch
        {
            "quote" => valid with { SourceQuote = "PRIVATE unsupported quote" },
            "key" => valid with { EntityKey = new string('x', 301) },
            "origin" => valid with { SourceKind = "Clarification" },
            _ => valid
        };
        f.Provider.Claims = [valid, invalid];
        var diagnostics = new List<string>();
        var worker = new TimelineFactProcessing(f.NewContext, f.Provider, f.Clock, diagnostics.Add);
        Assert.Equal("Retry", await worker.ProcessAsync(id, "worker"));
        Assert.Equal(diagnostic, Assert.Single(diagnostics));
        Assert.Empty(await f.Db.TimelineFacts.ToListAsync());
        Assert.Empty(await f.Db.TimelineFactProposals.ToListAsync());
        Assert.Empty(await f.Db.TimelineFactJobs.Where(x => x.Kind == "Embedding").ToListAsync());
        var job = await f.Db.TimelineFactJobs.AsNoTracking().SingleAsync();
        Assert.Equal("InvalidResponse", job.LastErrorCode);
        Assert.Null(job.ExtractionResultHash);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task Invalid_embedding_retains_retry_state(int variant)
    {
        await using var f = new Fixture(); var id = await f.InitializeAsync();
        await f.Worker.ProcessAsync(id, "worker");
        await using var db = f.NewContext();
        var embedId = await db.TimelineFactJobs.Where(x => x.Kind == "Embedding").Select(x => x.Id).SingleAsync();
        f.Provider.Vector = variant switch
        {
            0 => new float[1536], 1 => new float[12],
            2 => Enumerable.Repeat(float.NaN, 1536).ToArray(),
            _ => Enumerable.Repeat(float.PositiveInfinity, 1536).ToArray()
        };
        Assert.Equal("Retry", await f.Worker.ProcessAsync(embedId, "embed"));
        Assert.Equal("InvalidResponse", (await db.TimelineFactJobs.AsNoTracking().SingleAsync(x => x.Id == embedId)).LastErrorCode);
        Assert.Null((await db.TimelineFacts.AsNoTracking().SingleAsync()).EmbeddedAtUtc);
    }

    [Fact]
    public async Task Backfill_is_explicit_bounded_authorized_resumable_and_idempotent()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var legacy = new TimelineEntry { TimelineId = f.Timeline.Id, AuthorId = f.Actor.Id, EntryNumber = 2, Title = "Legacy", Content = "Alice owns delivery." };
        f.Db.Add(legacy); await f.Db.SaveChangesAsync();
        Assert.Single(await f.Db.TimelineFactJobs.ToListAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Worker.EnqueueAsync(f.Project.Id, f.Timeline.Id, Guid.NewGuid(), false, 0, 2, 1));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Worker.EnqueueAsync(f.Project.Id, Guid.NewGuid(), f.Actor.Id, false, 0, 2, 1));
        var page = await f.Worker.EnqueueAsync(f.Project.Id, f.Timeline.Id, f.Actor.Id, false, 0, 2, 1);
        Assert.Equal(1, page.LastEntryNumber); Assert.Equal(0, page.Enqueued);
        page = await f.Worker.EnqueueAsync(f.Project.Id, f.Timeline.Id, f.Actor.Id, false, page.LastEntryNumber, 2, 1);
        Assert.Equal(2, page.LastEntryNumber); Assert.Equal(1, page.Enqueued);
        Assert.Equal(0, (await f.Worker.EnqueueAsync(f.Project.Id, f.Timeline.Id, f.Actor.Id, false, 0, 2, 100)).Enqueued);
        Assert.Equal(2, await f.Db.TimelineFactJobs.CountAsync());
    }

    [Fact]
    public async Task Saving_entry_also_persists_extraction_work_and_protects_source()
    {
        await using var db = new ProjectTimelineDbContext(new DbContextOptionsBuilder<ProjectTimelineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var actor = new User { Email = "processing@example.test", DisplayName = "Author", PasswordHash = "test" };
        var project = new Project { OwnerId = actor.Id, Code = "P", Name = "Project" };
        var timeline = new Timeline { ProjectId = project.Id, Code = "T", Name = "Timeline" };
        db.AddRange(actor, project, timeline); await db.SaveChangesAsync();
        Assert.Empty(await db.TimelineFactJobs.ToListAsync()); // No implicit scan of existing entries.
        var entry = await new TimelineService(db).AddEntryAsync(timeline.Id, actor.Id, "Title", "Alice owns delivery.", default);
        var job = Assert.Single(await db.TimelineFactJobs.ToListAsync());
        Assert.Equal(entry.Id, job.EntryId);
        Assert.Equal(TimelineFactEvidence.Fingerprint(entry), job.SourceFingerprint);
        Assert.Equal("Extraction", job.Kind);
        Assert.Empty(await db.TimelineFacts.ToListAsync());
        entry.Content = "Replacement";
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => db.SaveChangesAsync());
    }
}
