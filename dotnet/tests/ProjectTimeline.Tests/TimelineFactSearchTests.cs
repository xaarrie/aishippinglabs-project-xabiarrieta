using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ProjectTimeline.BusinessLogic.Agents.Tools;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.DataAccessLogic;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class TimelineFactSearchTests
{
    [Fact]
    public async Task Whole_lexeme_search_returns_accepted_evidence_not_pending_candidates()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice-delivery");
        await f.NewSourceAsync();
        var pending = await f.PrepareAsync("Bob owns testing.", "bob");
        Assert.NotEqual(fact.EntryId, pending.EntryId);
        Assert.Equal(f.Entry.Id, pending.EntryId);
        Assert.Equal("Bob owns testing.", pending.Additions.Single().SourceQuote);
        Assert.Empty(await f.Search.SearchAsync("ALIC"));
        var hit = Assert.Single(await f.Search.SearchAsync("ALICE"));
        Assert.Equal(fact.Id, hit.Id);
        Assert.Equal("Alice owns delivery.", hit.SourceQuote);
        Assert.Equal(fact.SourceFingerprint, hit.SourceFingerprint);
        Assert.Empty(await f.Search.SearchAsync("bob"));
        Assert.Equal(fact.Id, Assert.Single(await f.Search.SearchAsync("delivery")).Id);
    }

    [Theory]
    [InlineData("future")]
    [InlineData("foreignTimeline")]
    [InlineData("foreignProject")]
    [InlineData("pendingReview")]
    [InlineData("invalid")]
    [InlineData("futureReview")]
    public async Task Search_and_fetch_exclude_ineffective_or_out_of_scope_facts(string kind)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var accepted = await f.AcceptAsync("Alice owns delivery.", "alice");
        await f.NewSourceAsync();
        var pending = await f.PrepareAsync("Bob owns testing.", "bob");
        Assert.NotEqual(accepted.EntryId, pending.EntryId);
        // Simulate persisted rows, including hostile provenance combinations, without the write guards.
        await using var raw = f.Unguarded();
        var fact = await raw.Set<TimelineFact>().SingleAsync();
        if (kind == "invalid") fact.Valid = false;
        if (kind == "pendingReview") fact.CreationReviewId = pending.Id;
        if (kind is "future" or "foreignTimeline" or "foreignProject" or "futureReview")
        {
            var timeline = f.Timeline;
            if (kind is "foreignTimeline" or "foreignProject")
            {
                var projectId = f.Project.Id;
                if (kind == "foreignProject")
                {
                    var project = new Project { OwnerId = f.Actor.Id, Code = "other", Name = "Other" };
                    raw.Add(project); projectId = project.Id;
                }
                timeline = new Timeline { ProjectId = projectId, Code = "other", Name = "Other" };
                raw.Add(timeline);
            }
            var other = new TimelineEntry { TimelineId = timeline.Id, AuthorId = f.Actor.Id, EntryNumber = f.Entry.EntryNumber + 1,
                Title = "Other", Content = "Alice owns delivery." };
            raw.Add(other);
            if (kind == "futureReview")
                (await raw.Set<TimelineFactProposal>().SingleAsync(x => x.Id == fact.CreationReviewId)).EntryId = other.Id;
            else fact.EntryId = other.Id;
        }
        await raw.SaveChangesAsync();
        Assert.Empty(await f.Search.SearchAsync("alice"));
        Assert.Empty(await f.Search.FetchAsync([accepted.Id]));
        var fake = new FactReviewFakeAgent();
        fake.Result.Additions.Add(new("Bob owns testing.", "bob", "Person", "Bob owns testing."));
        fake.Result.Invalidations.Add(new(accepted.Id, "Untrusted target", 0));
        fake.Result.InvalidationAssessments.Add(TimelineFactContextReconciliationTests.Assessment(accepted, fake.Result.Additions[0]));
        var error = await Assert.ThrowsAsync<TimelineFactConflictException>(() => new TimelineFactReviewPlanner(f.Db, f.Scope,
            f.Actor.Id, false, new TimelineFactOptimizationAgent(fake)).PlanAsync());
        Assert.Contains("authorized effective scope", error.Message);
    }

    [Fact]
    public async Task Read_limits_are_enforced_and_every_read_reauthorizes()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        await using (var raw = f.Unguarded())
        {
            for (var i = 0; i < 60; i++) raw.Add(new TimelineFact { EntryId = f.Entry.Id,
                CreationReviewId = fact.CreationReviewId, AdditionId = Guid.NewGuid(), Claim = "Alice " + i,
                EntityKey = "alice", SourceQuote = fact.SourceQuote, SourceFingerprint = fact.SourceFingerprint });
            await raw.SaveChangesAsync();
        }
        Assert.Equal(50, (await f.Search.SearchAsync("alice", 50)).Count);
        Assert.Equal(3, (await f.Search.SearchAsync("alice", 3)).Count);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => f.Search.SearchAsync("alice", 51));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Search.SearchAsync(new string('x', 301)));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Search.FetchAsync([fact.Id, fact.Id]));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Search.FetchAsync(Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()).ToArray()));
        var tools = new TimelineFactReviewTools(f.Search);
        var cached = await tools.search_facts("alice", 1);
        for (var i = 1; i < TimelineFactReviewTools.MaximumCalls; i++) await tools.search_facts("alice " + i, 1);
        Assert.Same(cached, await tools.search_facts("ALICE", 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.fetch_facts([fact.Id]));
        f.Actor.IsActive = false; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Search.SearchAsync("alice"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Search.FetchAsync([fact.Id]));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Search.CoverageAsync());
    }

    [Fact]
    public async Task Foreign_scope_and_unknown_actor_are_rejected_even_for_empty_search()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => new TimelineFactSearch(f.Db,
            f.Scope with { TimelineId = Guid.NewGuid() }, f.Actor.Id, false).SearchAsync(""));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new TimelineFactSearch(f.Db,
            f.Scope, Guid.NewGuid(), false).SearchAsync(""));
    }

    [Fact]
    public void Npgsql_search_translates_filter_and_limit_without_connection_or_full_history_projection()
    {
        using var db = new ProjectTimelineDbContext(new DbContextOptionsBuilder<ProjectTimelineDbContext>()
            .UseNpgsql("Host=localhost;Database=offline_sql_only", o => o.UseVector()).Options);
        var search = new TimelineFactSearch(db, new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid(), false);
        var method = typeof(TimelineFactSearch).GetMethod("SearchQuery", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var query = (IQueryable<TimelineFactSearchHit>)method.Invoke(search, [2, new[] { "alic", "delivery" }, 20])!;
        var sql = query.ToQueryString(); // Translation only; no database call.
        Assert.Contains("LIMIT", sql);
        Assert.Contains("@@", sql);
        Assert.Contains("ts_rank(", sql);
        Assert.Contains("Accepted", sql);
        Assert.Contains("TimelineId", sql);
        Assert.DoesNotContain("\"Content\"", sql);
        Assert.DoesNotContain("\"Embedding\"", sql);
    }
}

internal sealed class TimelineFactReviewFixture : IAsyncDisposable
{
    private readonly string database = Guid.NewGuid().ToString();
    private readonly InMemoryDatabaseRoot root = new();
    public ProjectTimelineDbContext Db { get; }
    public TimelineFactReviewFixture() => Db = new(new DbContextOptionsBuilder<ProjectTimelineDbContext>()
        .UseInMemoryDatabase(database, root).Options);
    public DbContext Unguarded() => new(new DbContextOptionsBuilder().UseInMemoryDatabase(database, root).UseModel(Db.Model).Options);
    public User Actor { get; } = new() { Email = "review@example.test", DisplayName = "Author", PasswordHash = "test" };
    public Project Project { get; } = new() { Code = "P", Name = "Project" };
    public Timeline Timeline { get; } = new() { Code = "T", Name = "Timeline" };
    public TimelineEntry Entry { get; private set; } = new() { EntryNumber = 2, Title = "  Original\nTitle  ", Content = "Alice owns delivery.\nBob owns testing.  " };
    public TimelineFactScope Scope => new(Project.Id, Timeline.Id, Entry.Id);
    public TimelineFactSearch Search => new(Db, Scope, Actor.Id, false);
    public static async Task<TimelineFactReviewFixture> CreateAsync()
    {
        var f = new TimelineFactReviewFixture();
        f.Project.OwnerId = f.Actor.Id; f.Timeline.ProjectId = f.Project.Id;
        f.Entry.TimelineId = f.Timeline.Id; f.Entry.AuthorId = f.Actor.Id;
        f.Db.AddRange(f.Actor, f.Project, f.Timeline, f.Entry);
        await f.Db.SaveChangesAsync();
        return f;
    }
    // Explicitly advance only tests that need a new reconciliation source; accepted evidence stays immutable.
    public async Task NewSourceAsync()
    {
        var source = new TimelineEntry { TimelineId = Timeline.Id, AuthorId = Actor.Id,
            EntryNumber = await Db.TimelineEntries.Where(x => x.TimelineId == Timeline.Id).MaxAsync(x => x.EntryNumber) + 1,
            Title = Entry.Title, Content = Entry.Content };
        Db.Add(source); await Db.SaveChangesAsync();
        Entry = source;
    }
    public async Task<TimelineFactProposal> PrepareAsync(string quote, string entity)
    {
        var revision = await Db.Timelines.AsNoTracking().Where(x => x.Id == Timeline.Id).Select(x => x.FactRevision).SingleAsync();
        return await new TimelineFactService(Db).PrepareAsync(Scope, Actor.Id, false, revision,
            new([new(quote, entity, "Person", quote)], [], []));
    }
    public async Task<TimelineFact> AcceptAsync(string quote, string entity)
    {
        var proposal = await PrepareAsync(quote, entity);
        await new TimelineFactService(Db).AcceptAsync(Scope, proposal.Id, Actor.Id, false);
        return await Db.TimelineFacts.SingleAsync(x => x.CreationReviewId == proposal.Id);
    }
    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
