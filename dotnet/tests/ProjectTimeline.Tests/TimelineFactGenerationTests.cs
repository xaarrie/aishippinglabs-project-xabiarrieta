using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Text.Json;
using ProjectTimeline.UI.WebApi;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.DataAccessLogic;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class TimelineFactGenerationTests
{
    private sealed class Fixture : IAsyncDisposable, ITimelineFactProvider
    {
        private readonly DbContextOptions<ProjectTimelineDbContext> options = new DbContextOptionsBuilder<ProjectTimelineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString(), new Microsoft.EntityFrameworkCore.Storage.InMemoryDatabaseRoot()).Options;
        public ProjectTimelineDbContext Db() => new(options);
        public User Actor = new() { Email = "generation@example.test", DisplayName = "Author", PasswordHash = "test" };
        public Project Project = new() { Code = "P", Name = "Project" };
        public Timeline Timeline = new() { Code = "T", Name = "Timeline" };
        public TimelineEntry Entry = null!;
        public Guid JobId;
        public TimelineFactScope Scope => new(Project.Id, Timeline.Id, Entry.Id);
        public string ExtractionModel => "fake";
        public IReadOnlyList<TimelineFactCandidate> Candidates = [new("Alice owns delivery", "alice", "Person", "Alice owns delivery.")];
        public Func<Task>? DuringExtraction;
        public Exception? Error = null;
        public async Task Initialize()
        {
            await using var db = Db();
            Project.OwnerId = Actor.Id; Timeline.ProjectId = Project.Id;
            db.AddRange(Actor, Project, Timeline); await db.SaveChangesAsync();
            Entry = await new TimelineService(db).AddEntryAsync(Timeline.Id, Actor.Id, "Delivery", "Alice owns delivery.", default);
            JobId = await db.TimelineFactJobs.Select(x => x.Id).SingleAsync();
        }
        public async Task<IReadOnlyList<TimelineFactCandidate>> ExtractAsync(TimelineFactExtraction source, CancellationToken ct)
        {
            if (DuringExtraction is not null) await DuringExtraction();
            if (Error is not null) throw Error;
            return Candidates;
        }
        public Task<float[]> EmbedAsync(string claim, CancellationToken ct) => throw new Exception("Pending candidates must not be embedded.");
        public Task<string> Run() => new TimelineFactProcessing(Db, this).ProcessAsync(JobId, "test");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static JsonElement Envelope(TimelineFactStateDto state) =>
        JsonSerializer.SerializeToElement(state, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public async Task Read_envelope_binds_authorized_saved_source_and_generated_proposal_after_another_session_edits()
    {
        await using var f = new Fixture(); await f.Initialize(); await f.Run();
        await using var reader = f.Db();
        var staleEntry = await reader.TimelineEntries.SingleAsync();
        await using (var edit = f.Db())
        {
            var current = await edit.TimelineEntries.SingleAsync();
            current.Title = "Updated elsewhere"; current.Content = "Alice owns delivery. Updated elsewhere.";
            await edit.SaveChangesAsync();
        }
        var worker = new TimelineFactProcessing(f.Db, f);
        await using (var generate = f.Db())
            await new TimelineFactApplication(generate, () => new FactReviewFakeAgent(), worker)
                .GenerateAsync(f.Entry.Id, f.Actor.Id, false, new());
        var dto = Envelope(await new TimelineFactApplication(reader, () => throw new Exception("Read only"))
            .ReadAsync(f.Entry.Id, f.Actor.Id, false));
        Assert.True(dto.TryGetProperty("source", out var source), "Facts must carry their authoritative saved source, not rely on a separate entry read.");
        Assert.Equal("Updated elsewhere", source.GetProperty("title").GetString());
        Assert.Equal("Alice owns delivery. Updated elsewhere.", source.GetProperty("content").GetString());
        Assert.Equal(f.Entry.Id, source.GetProperty("entryId").GetGuid());
        Assert.Equal(f.Timeline.Id, source.GetProperty("timelineId").GetGuid());
        Assert.Equal("Delivery", staleEntry.Title); // The tracked entry is intentionally stale.
        var generated = dto.GetProperty("proposals").EnumerateArray().Single(p => p.GetProperty("proposalId").GetGuid() == dto.GetProperty("generationId").GetGuid());
        await using var verify = f.Db();
        var fingerprint = TimelineFactEvidence.Fingerprint(await verify.TimelineEntries.SingleAsync());
        Assert.Equal(fingerprint, source.GetProperty("fingerprint").GetString());
        Assert.Equal(fingerprint, generated.GetProperty("sourceFingerprint").GetString());
        Assert.False(dto.GetProperty("refreshRequired").GetBoolean());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new TimelineFactApplication(reader, () => new FactReviewFakeAgent())
            .ReadAsync(f.Entry.Id, Guid.NewGuid(), false));
    }

    private sealed class ReadRace(Action change) : IMaterializationInterceptor
    {
        public bool Armed = true;
        public bool Fired;
        public object InitializedInstance(MaterializationInterceptionData data, object entity)
        {
            if (Armed && entity is TimelineFactProposal) { Armed = false; Fired = true; change(); }
            return entity;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_change_during_read_explicitly_requires_refresh_instead_of_claiming_atomic_evidence(bool restore)
    {
        await using var f = new Fixture(); await f.Initialize(); await f.Run();
        var race = new ReadRace(() =>
        {
            using var edit = f.Db();
            var source = edit.TimelineEntries.Single();
            source.Content = "Concurrent source change.";
            edit.SaveChanges();
            if (restore) { source.Content = f.Entry.Content; edit.SaveChanges(); }
        });
        await using var original = f.Db();
        var options = (DbContextOptions<ProjectTimelineDbContext>)original.GetService<IDbContextOptions>();
        await using var reader = new ProjectTimelineDbContext(new DbContextOptionsBuilder<ProjectTimelineDbContext>(options).AddInterceptors(race).Options);
        var dto = Envelope(await new TimelineFactApplication(reader, () => new FactReviewFakeAgent()).ReadAsync(f.Entry.Id, f.Actor.Id, false));
        Assert.True(race.Fired, "The concurrent edit must execute inside the read, not before or after it.");
        Assert.True(dto.TryGetProperty("refreshRequired", out var refresh), "Mixed reads need an explicit refresh gate.");
        Assert.True(refresh.GetBoolean());
        Assert.Equal(restore ? f.Entry.Content : "Concurrent source change.", dto.GetProperty("source").GetProperty("content").GetString());
    }

    [Fact]
    public async Task Cross_project_move_during_read_cannot_expose_destination_source_text()
    {
        await using var f = new Fixture(); await f.Initialize(); await f.Run();
        Guid destinationId;
        await using (var setup = f.Db())
        {
            var outsider = new User { Email = "other-tenant@example.test", DisplayName = "Other", PasswordHash = "test" };
            var project = new Project { Code = "PRIVATE", Name = "Other tenant", OwnerId = outsider.Id };
            var timeline = new Timeline { ProjectId = project.Id, Code = "PRIVATE", Name = "Private" };
            setup.AddRange(outsider, project, timeline); await setup.SaveChangesAsync(); destinationId = timeline.Id;
        }
        var race = new ReadRace(() =>
        {
            using var edit = f.Db();
            var source = edit.TimelineEntries.Single();
            source.TimelineId = destinationId; source.Content = "Destination-only source text.";
            edit.SaveChanges();
        });
        await using var original = f.Db();
        var options = (DbContextOptions<ProjectTimelineDbContext>)original.GetService<IDbContextOptions>();
        await using var reader = new ProjectTimelineDbContext(new DbContextOptionsBuilder<ProjectTimelineDbContext>(options).AddInterceptors(race).Options);
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => new TimelineFactApplication(reader, () => new FactReviewFakeAgent())
            .ReadAsync(f.Entry.Id, f.Actor.Id, false));
        Assert.True(race.Fired);
        await using var verify = f.Db();
        Assert.Equal(destinationId, (await verify.TimelineEntries.SingleAsync()).TimelineId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new TimelineFactApplication(verify, () => new FactReviewFakeAgent())
            .ReadAsync(f.Entry.Id, f.Actor.Id, false));
    }

    [Fact]
    public async Task Empty_generation_flows_through_application_planning_and_explicit_acceptance()
    {
        await using var f = new Fixture(); await f.Initialize(); f.Candidates = [];
        await using var db = f.Db();
        var fake = new FactReviewFakeAgent();
        var api = new TimelineFactApplication(db, () => fake, new(f.Db, f));
        var empty = await api.GenerateAsync(f.Entry.Id, f.Actor.Id, false, new());
        Assert.Equal("Empty", empty.LifecycleState);
        Assert.Empty(Assert.Single(empty.Proposals).Additions);
        var review = await api.PlanAsync(f.Entry.Id, f.Actor.Id, false, new(null, []));
        Assert.Contains("\"pending_extraction_candidates\":[]", fake.Message);
        Assert.True(review.Ready); Assert.Empty(review.Additions); Assert.Empty(review.Invalidations);
        Assert.NotEqual(empty.GenerationId, review.ProposalId);
        Assert.Equal("Accepted", (await api.AcceptAsync(f.Entry.Id, review.ProposalId!.Value, f.Actor.Id, false)).State);
        var accepted = await api.ReadAsync(f.Entry.Id, f.Actor.Id, false);
        Assert.Equal("Reconciled", accepted.LifecycleState); Assert.False(accepted.CanEdit);
        Assert.Empty(accepted.Facts);
        (await db.TimelineEntries.SingleAsync()).Content = "Forbidden source edit";
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Generation_is_pending_not_searchable_or_acceptible_until_reconciled()
    {
        await using var f = new Fixture(); await f.Initialize();
        Assert.Equal("Completed", await f.Run());
        await using var db = f.Db();
        Assert.Empty(await db.TimelineFacts.ToListAsync());
        Assert.Empty(await db.TimelineFactJobs.Where(x => x.Kind == "Embedding").ToListAsync());
        var draft = await db.TimelineFactProposals.SingleAsync();
        Assert.Equal(f.JobId, draft.Id);
        Assert.Equal("Generated", draft.Status);
        Assert.Single(await db.TimelineFactAdditions.ToListAsync());
        Assert.Empty(await new TimelineFactSearch(db, f.Scope, f.Actor.Id, false).SearchAsync("Alice"));
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => new TimelineFactService(db).AcceptAsync(f.Scope, draft.Id, f.Actor.Id, false));
        var revision = await db.Timelines.Select(x => x.FactRevision).SingleAsync();
        var review = await new TimelineFactService(db).PrepareAsync(f.Scope, f.Actor.Id, false, revision, new(f.Candidates, [], []));
        await new TimelineFactService(db).AcceptAsync(f.Scope, review.Id, f.Actor.Id, false);
        await new TimelineFactService(db).AcceptAsync(f.Scope, review.Id, f.Actor.Id, false);
        Assert.Single(await db.TimelineFacts.ToListAsync());
        Assert.Single(await db.TimelineFactJobs.Where(x => x.Kind == "Embedding").ToListAsync());
        (await db.TimelineEntries.SingleAsync()).Content = "Corrected";
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Pending_source_edits_revoke_old_proposals_even_when_source_is_restored()
    {
        await using var f = new Fixture(); await f.Initialize(); await f.Run();
        Guid proposalId;
        await using (var db = f.Db())
        {
            var review = await new TimelineFactService(db).PrepareAsync(f.Scope, f.Actor.Id, false,
                await db.Timelines.Select(x => x.FactRevision).SingleAsync(), new(f.Candidates, [], []));
            proposalId = review.Id;
            (await db.TimelineEntries.SingleAsync()).Content = "Changed";
            await db.SaveChangesAsync();
        }
        await using (var db = f.Db())
        {
            (await db.TimelineEntries.SingleAsync()).Content = f.Entry.Content;
            await db.SaveChangesAsync();
        }
        await using var verify = f.Db();
        Assert.All(await verify.TimelineFactProposals.ToListAsync(), x => Assert.Equal("Superseded", x.Status));
        Assert.Equal("Superseded", (await verify.TimelineFactJobs.SingleAsync()).State);
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => new TimelineFactService(verify).AcceptAsync(f.Scope, proposalId, f.Actor.Id, false));
        Assert.Empty(await verify.TimelineFacts.ToListAsync());
    }

    [Fact]
    public async Task Source_edit_during_provider_call_revokes_lease_and_cannot_commit_old_result()
    {
        await using var f = new Fixture(); await f.Initialize();
        f.DuringExtraction = async () =>
        {
            await using var edit = f.Db();
            (await edit.TimelineEntries.SingleAsync()).Content = "Changed";
            await edit.SaveChangesAsync();
        };
        Assert.Equal("LostLease", await f.Run());
        await using var db = f.Db();
        Assert.Equal("Superseded", (await db.TimelineFactJobs.SingleAsync()).State);
        Assert.Empty(await db.TimelineFacts.ToListAsync()); Assert.Empty(await db.TimelineFactProposals.ToListAsync());
    }

    [Fact]
    public async Task Explicit_generation_and_regeneration_are_authorized_durable_and_lock_after_acceptance()
    {
        await using var f = new Fixture(); await f.Initialize();
        await using var db = f.Db();
        var worker = new TimelineFactProcessing(f.Db, f);
        var agent = new FactReviewFakeAgent { Result = new() { Additions = f.Candidates.ToList() } };
        var api = new TimelineFactApplication(db, () => agent, worker);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => api.GenerateAsync(f.Entry.Id, Guid.NewGuid(), true, new()));
        var first = await api.GenerateAsync(f.Entry.Id, f.Actor.Id, false, new());
        Assert.Equal("Pending", first.LifecycleState); Assert.True(first.CanEdit); Assert.True(first.CanRegenerate);
        Assert.False(Assert.Single(first.Proposals).Ready);
        var oldReview = await api.PlanAsync(f.Entry.Id, f.Actor.Id, false, new(null, []));
        var next = await api.GenerateAsync(f.Entry.Id, f.Actor.Id, false, new(true, first.GenerationId));
        Assert.NotEqual(first.GenerationId, next.GenerationId);
        Assert.Equal("Superseded", next.Proposals.Single(x => x.ProposalId == first.GenerationId).State);
        Assert.Equal("Superseded", next.Proposals.Single(x => x.ProposalId == oldReview.ProposalId).State);
        Assert.Single(next.Proposals, x => x.State == "Generated");
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => api.GenerateAsync(f.Entry.Id, f.Actor.Id, false, new(true, first.GenerationId)));
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => api.AcceptAsync(f.Entry.Id, oldReview.ProposalId!.Value, f.Actor.Id, false));
        var review = await api.PlanAsync(f.Entry.Id, f.Actor.Id, false, new(null, []));
        await api.AcceptAsync(f.Entry.Id, review.ProposalId!.Value, f.Actor.Id, false);
        var accepted = await api.ReadAsync(f.Entry.Id, f.Actor.Id, false);
        Assert.Equal("Reconciled", accepted.LifecycleState);
        Assert.False(accepted.CanEdit); Assert.False(accepted.CanRegenerate); Assert.False(accepted.CanGenerate);
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => api.GenerateAsync(f.Entry.Id, f.Actor.Id, false, new(true, next.GenerationId)));
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => api.PlanAsync(f.Entry.Id, f.Actor.Id, false, new(null, [])));
    }

    [Theory]
    [InlineData("Unavailable")]
    [InlineData("InvalidResponse")]
    [InlineData("Empty")]
    public async Task Explicit_generation_returns_safe_retry_or_empty_state_without_facts(string outcome)
    {
        await using var f = new Fixture(); await f.Initialize();
        if (outcome == "Empty") f.Candidates = [];
        else f.Error = new TimelineFactProviderException(outcome, "ResponseRefused");
        await using var db = f.Db();
        var api = new TimelineFactApplication(db, () => throw new Exception("Not planning"), new(f.Db, f));
        var state = await api.GenerateAsync(f.Entry.Id, f.Actor.Id, false, new());
        Assert.Equal(outcome == "Empty" ? "Empty" : "Retry", state.LifecycleState);
        Assert.Empty(state.Facts);
        if (outcome == "Empty") Assert.Empty(Assert.Single(state.Proposals).Additions);
        else
        {
            Assert.Empty(state.Proposals);
            Assert.Equal(outcome, Assert.Single(state.Processing).ErrorCode);
            Assert.NotNull(Assert.Single(state.Processing).NextAttemptAtUtc);
        }
    }

    [Fact]
    public async Task Regeneration_during_inflight_extraction_revokes_old_result_and_failure_lease()
    {
        await using var f = new Fixture(); await f.Initialize();
        var worker = new TimelineFactProcessing(f.Db, f);
        Guid replacement = Guid.Empty;
        f.DuringExtraction = async () => replacement = await worker.RequestGenerationAsync(f.Scope, f.Actor.Id, false, true, f.JobId);
        Assert.Equal("LostLease", await f.Run());
        f.DuringExtraction = null;
        Assert.Equal("Completed", await worker.ProcessAsync(replacement, "new-worker"));
        await using var db = f.Db();
        Assert.Equal(replacement, (await db.TimelineFactProposals.SingleAsync()).Id);
        Assert.Single(await db.TimelineFactJobs.Where(x => x.State != "Superseded").ToListAsync());
        Assert.Empty(await db.TimelineFacts.ToListAsync());
    }

    [Fact]
    public async Task Stale_source_writer_cannot_overwrite_a_concurrent_edit()
    {
        await using var f = new Fixture(); await f.Initialize();
        await using var first = f.Db(); await using var second = f.Db();
        var a = await first.TimelineEntries.SingleAsync(); var b = await second.TimelineEntries.SingleAsync();
        a.Content = "First edit"; await first.SaveChangesAsync();
        b.Content = "Second edit";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        await using var verify = f.Db(); Assert.Equal("First edit", (await verify.TimelineEntries.SingleAsync()).Content);
    }

    [Fact]
    public async Task Empty_generation_is_durable_and_zero_addition_reconciliation_still_locks_source()
    {
        await using var f = new Fixture(); await f.Initialize(); f.Candidates = [];
        Assert.Equal("Completed", await f.Run());
        await using var db = f.Db();
        Assert.Equal("Generated", (await db.TimelineFactProposals.SingleAsync()).Status);
        var review = await new TimelineFactService(db).PrepareAsync(f.Scope, f.Actor.Id, false,
            await db.Timelines.Select(x => x.FactRevision).SingleAsync(), new([], [], []));
        await new TimelineFactService(db).AcceptAsync(f.Scope, review.Id, f.Actor.Id, false);
        Assert.Empty(await db.TimelineFacts.ToListAsync());
        (await db.TimelineEntries.SingleAsync()).Title = "Changed";
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => db.SaveChangesAsync());
    }
}

// Deterministic persisted generation for review/API tests; runs the real lease and completion guards.
internal sealed class PendingGenerationProvider : ITimelineFactProvider
{
    public string ExtractionModel => "offline";
    public Task<IReadOnlyList<TimelineFactCandidate>> ExtractAsync(TimelineFactExtraction source, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TimelineFactCandidate>>([new("Alice owns delivery.", "alice", "Person", "Alice owns delivery.")]);
    public Task<float[]> EmbedAsync(string claim, CancellationToken ct) => Task.FromResult(Enumerable.Repeat(0.1f, 1536).ToArray());
    public static async Task GenerateAsync(ProjectTimelineDbContext db, TimelineEntry entry, Guid actor, Guid project)
    {
        var options = (DbContextOptions<ProjectTimelineDbContext>)db.GetService<IDbContextOptions>();
        var worker = new TimelineFactProcessing(() => new(options), new PendingGenerationProvider());
        var id = await worker.RequestGenerationAsync(new(project, entry.TimelineId, entry.Id), actor, false);
        Assert.Equal("Completed", await worker.ProcessAsync(id, "fixture"));
        await db.Entry(await db.Timelines.SingleAsync(x => x.Id == entry.TimelineId)).ReloadAsync();
    }
}
