using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents.Tools;
using ProjectTimeline.DataAccessLogic;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class TimelineFactReviewToolsTests
{
    [Fact]
    public async Task Groups_preserve_query_identity_independent_rank_limits_and_scalar_cache()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await f.AcceptAsync("Alice owns delivery.", "alice");
        await f.NewSourceAsync();
        await f.AcceptAsync("Bob owns testing.", "bob");
        var tools = new TimelineFactReviewTools(f.Search);
        var queries = new[] { "ALICE", "bob", "  alice  ", "ALIC", "alice delivery" };
        var groups = await tools.search_facts_batch(queries, 1);
        Assert.Equal(queries, groups.Select(x => x.Query));
        for (var i = 0; i < queries.Length; i++)
            Assert.Equal(await f.Search.SearchAsync(queries[i], 1), groups[i].Hits);
        Assert.Same(groups[0].Hits, groups[2].Hits);
        Assert.Same(groups[0].Hits, await tools.search_facts("Alice", 1));
        Assert.Same(groups[4].Hits, await tools.search_facts(" Alice\tDELIVERY\n", 1));
        Assert.NotSame(groups[0].Hits, groups[4].Hits); // Overlapping terms are not duplicate queries.
        Assert.NotSame(groups[0].Hits, await tools.search_facts("alice", 2));
        Assert.NotSame(groups[0].Hits, await new TimelineFactReviewTools(f.Search).search_facts("alice", 1));
        Assert.Empty(groups[3].Hits);
        Assert.False(f.Db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task Batch_counts_each_uncached_read_and_preflights_remaining_budget_without_partial_work()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var tools = new TimelineFactReviewTools(f.Search);
        Assert.Equal(60, tools.RemainingReads);
        Assert.Equal(20, TimelineFactReviewTools.MaximumBatchQueries);
        for (var i = 0; i < 7; i++)
            await tools.search_facts_batch(Enumerable.Range(i * 8, 8).Select(x => "query " + x).ToArray());
        await tools.fetch_facts([]); // 57 actual backend calls.
        Assert.Equal(3, tools.RemainingReads);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.search_facts_batch(["one", "two", "three", "four"]));
        Assert.Equal(3, tools.RemainingReads);
        // Rejection must leave all three remaining reads available, not cache a partial batch.
        var groups = await tools.search_facts_batch(["one", "two", "three", "ONE"]);
        Assert.Same(groups[0].Hits, groups[3].Hits);
        Assert.Same(groups[0].Hits, await tools.search_facts("one"));
        await tools.search_facts_batch(["one", "two", "three"]);
        Assert.Equal(0, tools.RemainingReads);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.search_facts("four"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.search_facts("one", 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.fetch_facts([]));
    }

    [Theory]
    [InlineData("nullBatch")]
    [InlineData("empty")]
    [InlineData("oversize")]
    [InlineData("nullQuery")]
    [InlineData("longQuery")]
    [InlineData("zeroLimit")]
    [InlineData("largeLimit")]
    public async Task Invalid_batches_fail_before_any_backend_work(string kind)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var tools = new TimelineFactReviewTools(f.Search);
        string[] queries = kind switch {
            "nullBatch" => null!, "empty" => [],
            "oversize" => Enumerable.Repeat("alice", TimelineFactReviewTools.MaximumBatchQueries + 1).ToArray(),
            "nullQuery" => ["alice", null!], "longQuery" => ["alice", new string('x', 301)],
            _ => ["alice"]
        };
        var limit = kind == "zeroLimit" ? 0 : kind == "largeLimit" ? 51 : 20;
        await Assert.ThrowsAnyAsync<ArgumentException>(() => tools.search_facts_batch(queries, limit));
        for (var i = 0; i < TimelineFactReviewTools.MaximumCalls; i++) await tools.fetch_facts([]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.fetch_facts([]));
    }

    [Fact]
    public async Task Backend_term_truncation_and_blank_query_semantics_are_unchanged()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await f.AcceptAsync("Alice owns delivery.", "alice");
        const string terms = "aa bb cc dd ee ff gg hh ii jj kk ll alice";
        var tools = new TimelineFactReviewTools(f.Search);
        var groups = await tools.search_facts_batch([terms, "", "alice"], 50);
        Assert.Empty(groups[0].Hits);
        Assert.Empty(groups[1].Hits);
        Assert.Single(groups[2].Hits);
        Assert.Equal(await f.Search.SearchAsync(terms, 50), groups[0].Hits);
    }

    [Fact]
    public async Task Failed_reads_are_not_cached_and_still_consume_backend_budget()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var accepted = await f.AcceptAsync("Alice owns delivery.", "alice");
        var tools = new TimelineFactReviewTools(f.Search);
        f.Actor.IsActive = false;
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.search_facts_batch(["alice"]));
        f.Actor.IsActive = true;
        await f.Db.SaveChangesAsync();
        Assert.Equal(accepted.Id, Assert.Single(await tools.search_facts("alice")).Id);
        for (var i = 2; i < TimelineFactReviewTools.MaximumCalls; i++) await tools.fetch_facts([]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.fetch_facts([]));
        Assert.Single(await tools.search_facts("ALICE"));
    }

    [Theory]
    [InlineData("future")]
    [InlineData("foreign")]
    [InlineData("invalid")]
    [InlineData("pending")]
    public async Task Batch_retains_effective_authorized_scope(string kind)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await f.AcceptAsync("Alice owns delivery.", "alice");
        await using (var raw = f.Unguarded())
        {
            var fact = await raw.Set<TimelineFact>().SingleAsync();
            if (kind == "invalid") fact.Valid = false;
            if (kind == "pending") (await raw.Set<TimelineFactProposal>().SingleAsync()).Status = "Pending";
            if (kind is "future" or "foreign")
            {
                var timelineId = f.Timeline.Id;
                if (kind == "foreign")
                {
                    var other = new Timeline { ProjectId = f.Project.Id, Code = "OTHER", Name = "Other" };
                    raw.Add(other); timelineId = other.Id;
                }
                var entry = new TimelineEntry { TimelineId = timelineId, AuthorId = f.Actor.Id,
                    EntryNumber = f.Entry.EntryNumber + 1, Content = "Alice owns delivery." };
                raw.Add(entry); fact.EntryId = entry.Id;
            }
            await raw.SaveChangesAsync();
        }
        var groups = await new TimelineFactReviewTools(f.Search).search_facts_batch(["alice", "delivery"]);
        Assert.All(groups, x => Assert.Empty(x.Hits));
    }

    [Fact]
    public async Task Cancellation_applies_to_cached_hits_batches_and_fetch()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        using var cts = new CancellationTokenSource();
        var tools = new TimelineFactReviewTools(f.Search, cts.Token);
        await tools.search_facts("alice");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.search_facts("alice"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.search_facts_batch(["alice", "bob"]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.fetch_facts([]));
    }

    [Fact]
    public async Task Gate_serializes_batch_scalar_and_fetch_backend_reads()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await f.AcceptAsync("Alice owns delivery.", "alice");
        using var probe = new BlockingQuery();
        var options = (DbContextOptions<ProjectTimelineDbContext>)f.Db.GetService<IDbContextOptions>();
        await using var db = new ProjectTimelineDbContext(new DbContextOptionsBuilder<ProjectTimelineDbContext>(options)
            .AddInterceptors(probe).Options);
        var tools = new TimelineFactReviewTools(new TimelineFactSearch(db, f.Scope, f.Actor.Id, false));
        var first = Task.Run(() => tools.search_facts_batch(["alice", "bob"]));
        Task<IReadOnlyList<TimelineFactSearchHit>>? scalar = null, fetch = null;
        try
        {
            Assert.True(await Task.Run(() => probe.Started.Wait(TimeSpan.FromSeconds(10))));
            scalar = tools.search_facts("delivery");
            fetch = tools.fetch_facts([]);
            Assert.False(scalar.IsCompleted);
            Assert.False(fetch.IsCompleted);
            probe.Release.Set();
            await first; await scalar; await fetch;
            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally { probe.Release.Set(); await first; if (scalar is not null) await scalar; if (fetch is not null) await fetch; }
    }

    [Fact]
    public async Task Cancellation_interrupts_queued_reads_without_releasing_another_calls_gate()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        using var probe = new BlockingQuery();
        using var cts = new CancellationTokenSource();
        var options = (DbContextOptions<ProjectTimelineDbContext>)f.Db.GetService<IDbContextOptions>();
        await using var db = new ProjectTimelineDbContext(new DbContextOptionsBuilder<ProjectTimelineDbContext>(options)
            .AddInterceptors(probe).Options);
        var tools = new TimelineFactReviewTools(new TimelineFactSearch(db, f.Scope, f.Actor.Id, false), cts.Token);
        var first = Task.Run(() => tools.search_facts_batch(["alice", "bob"]));
        try
        {
            Assert.True(await Task.Run(() => probe.Started.Wait(TimeSpan.FromSeconds(10))));
            var scalar = tools.search_facts("delivery");
            var fetch = tools.fetch_facts([]);
            Assert.False(scalar.IsCompleted);
            Assert.False(fetch.IsCompleted);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scalar);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
        }
        finally
        {
            cts.Cancel();
            probe.Release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        }
    }

    [Theory]
    [InlineData("future")]
    [InlineData("foreign")]
    [InlineData("stale")]
    [InlineData("invalid")]
    public async Task Context_reads_exclude_ineligible_or_changed_sources(string change)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        await using (var raw = f.Unguarded())
        {
            var stored = await raw.Set<TimelineFact>().SingleAsync();
            var entry = await raw.Set<TimelineEntry>().SingleAsync();
            if (change == "stale") entry.Content += " Changed";
            if (change == "invalid") stored.Valid = false;
            if (change == "future")
            {
                var future = new TimelineEntry { TimelineId = f.Timeline.Id, AuthorId = f.Actor.Id,
                    EntryNumber = entry.EntryNumber + 1, Content = entry.Content };
                raw.Add(future); stored.EntryId = future.Id;
            }
            if (change == "foreign")
            {
                var other = new Timeline { ProjectId = f.Project.Id, Code = "OTHER", Name = "Other" };
                raw.Add(other); entry.TimelineId = other.Id;
            }
            await raw.SaveChangesAsync();
        }
        var tools = new TimelineFactReviewTools(f.Search, db: f.Db);
        if (change == "foreign")
            await Assert.ThrowsAsync<KeyNotFoundException>(() => tools.fetch_fact_contexts([fact.Id]));
        else Assert.Empty(await tools.fetch_fact_contexts([fact.Id]));
    }

    [Fact]
    public async Task Context_tool_reauthorizes_is_bounded_and_obeys_cancellation_and_shared_budget()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        using var cts = new CancellationTokenSource();
        var tools = new TimelineFactReviewTools(f.Search, cts.Token, f.Db);
        var context = Assert.Single(await tools.fetch_fact_contexts([fact.Id]));
        Assert.Equal(f.Entry.Content, context.Content);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => tools.fetch_fact_contexts([fact.Id, fact.Id]));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => tools.fetch_fact_contexts(
            Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()).ToArray()));
        f.Actor.IsActive = false;
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.fetch_fact_contexts([fact.Id]));
        f.Actor.IsActive = true;
        await f.Db.SaveChangesAsync();
        for (var i = 4; i < TimelineFactReviewTools.MaximumCalls; i++) await tools.fetch_facts([]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.fetch_fact_contexts([fact.Id]));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.fetch_fact_contexts([fact.Id]));
        Assert.False(f.Db.ChangeTracker.HasChanges());
    }

    private sealed class BlockingQuery : IQueryExpressionInterceptor, IDisposable
    {
        private int armed = 1;
        public ManualResetEventSlim Started { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public Expression QueryCompilationStarting(Expression expression, QueryExpressionEventData eventData)
        {
            if (Interlocked.Exchange(ref armed, 0) == 1)
            {
                Started.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Test query barrier timed out.");
            }
            return expression;
        }
        public void Dispose() { Started.Dispose(); Release.Dispose(); }
    }
}
