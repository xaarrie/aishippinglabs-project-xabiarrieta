using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.DataAccessLogic;
using ProjectTimeline.UI.WebApi;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class TimelineFactHostingTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 2)]
    public async Task Poller_is_opt_in_and_bounds_work_per_cycle(bool enabled, int expected)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        for (var i = 0; i < 5; i++)
        {
            var entry = new TimelineEntry { TimelineId = f.Timeline.Id, AuthorId = f.Actor.Id,
                EntryNumber = i + 3, Title = "Poll fixture", Content = "Alice owns delivery." };
            var job = TimelineFactProcessing.NewExtractionJob(entry);
            job.Model = "fake";
            f.Db.AddRange(entry, job);
        }
        await f.Db.SaveChangesAsync();
        var provider = new HostingProvider();
        var services = new ServiceCollection();
        var options = (DbContextOptions<ProjectTimelineDbContext>)f.Db.GetService<IDbContextOptions>();
        services.AddScoped(_ => new ProjectTimelineDbContext(options));
        services.AddScoped(_ => new TimelineFactProcessing(() => new ProjectTimelineDbContext(options), provider));
        await using var root = services.BuildServiceProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["TimelineFacts:ProcessingEnabled"] = enabled ? "true" : null, ["TimelineFacts:MaxJobsPerCycle"] = "2" }).Build();
        var poller = new TimelineFactPoller(root.GetRequiredService<IServiceScopeFactory>(), config, NullLogger<TimelineFactPoller>.Instance);
        await poller.PollOnceAsync(CancellationToken.None);
        Assert.Equal(expected, provider.Calls);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poller.PollOnceAsync(cancelled.Token));
        Assert.Equal(expected, provider.Calls);
    }

    [Fact]
    public async Task Poller_only_selects_pending_due_retries_and_expired_leases()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var now = DateTime.UtcNow;
        var jobs = new[] {
            new TimelineFactJob { State = "Retry", NextAttemptAtUtc = now.AddMinutes(-1) },
            new TimelineFactJob { State = "Running", LeaseExpiresAtUtc = now.AddMinutes(-1), LeaseOwner = "expired" },
            new TimelineFactJob { State = "Retry", NextAttemptAtUtc = now.AddHours(1) },
            new TimelineFactJob { State = "Running", LeaseExpiresAtUtc = now.AddHours(1), LeaseOwner = "live" },
            new TimelineFactJob { State = "Completed" }, new TimelineFactJob { State = "Failed" }
        };
        for (var i = 0; i < jobs.Length; i++)
        {
            var entry = new TimelineEntry { TimelineId = f.Timeline.Id, AuthorId = f.Actor.Id,
                EntryNumber = i + 3, Title = "Poll fixture", Content = "Alice owns delivery." };
            var job = jobs[i];
            job.EntryId = entry.Id;
            job.WorkKey = $"extraction:{entry.Id:N}";
            job.Model = "fake";
            job.SourceFingerprint = TimelineFactEvidence.Fingerprint(entry);
            f.Db.Add(entry);
            if (job.State != "Completed") f.Db.Add(job);
        }
        await f.Db.SaveChangesAsync();
        // Preserve the historical terminal fixture without forging completion through current application guards.
        await using (var historical = f.Unguarded())
        {
            historical.Add(jobs.Single(x => x.State == "Completed"));
            await historical.SaveChangesAsync();
        }
        var provider = new HostingProvider();
        var services = new ServiceCollection();
        var options = (DbContextOptions<ProjectTimelineDbContext>)f.Db.GetService<IDbContextOptions>();
        services.AddScoped(_ => new ProjectTimelineDbContext(options));
        services.AddScoped(_ => new TimelineFactProcessing(() => new ProjectTimelineDbContext(options), provider));
        await using var root = services.BuildServiceProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["TimelineFacts:ProcessingEnabled"] = "true" }).Build();
        var poller = new TimelineFactPoller(root.GetRequiredService<IServiceScopeFactory>(), config, NullLogger<TimelineFactPoller>.Instance);
        Assert.Equal(2, await poller.PollOnceAsync(CancellationToken.None));
        Assert.Equal(2, provider.Calls);
        Assert.Equal(2, await f.Db.TimelineFactProposals.CountAsync(x => x.Status == "Generated"));
        Assert.Empty(await f.Db.TimelineFacts.ToListAsync());
        Assert.Equal(2, await f.Db.TimelineFactJobs.CountAsync(x => jobs.Take(2).Select(j => j.Id).Contains(x.Id)
            && x.State == "Completed"));
        Assert.Equal("Completed", (await f.Db.TimelineFactJobs.AsNoTracking().SingleAsync(x => x.Id == jobs[4].Id)).State);
        Assert.Equal("Failed", (await f.Db.TimelineFactJobs.AsNoTracking().SingleAsync(x => x.Id == jobs[5].Id)).State);
    }

    [Fact]
    public async Task Backfill_is_explicit_authorized_bounded_enqueue_without_provider_resolution()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var config = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        var options = (DbContextOptions<ProjectTimelineDbContext>)f.Db.GetService<IDbContextOptions>();
        services.AddSingleton(options);
        services.AddScoped(_ => new ProjectTimelineDbContext(options));
        services.AddTimelineFacts(config);
        services.AddScoped<ITimelineFactProvider>(_ => throw new Exception("Provider must not be resolved for enqueue"));
        await using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var processing = scope.ServiceProvider.GetRequiredService<TimelineFactProcessing>();
        Assert.Empty(await f.Db.TimelineFactJobs.ToListAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => processing.EnqueueAsync(f.Project.Id, f.Timeline.Id, Guid.NewGuid(), true, 0, 3, 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => processing.EnqueueAsync(f.Project.Id, f.Timeline.Id, f.Actor.Id, false, 0, 3, 101));
        var page = await processing.EnqueueAsync(f.Project.Id, f.Timeline.Id, f.Actor.Id, false, 0, 3, 1);
        Assert.Equal(1, page.Enqueued);
        Assert.Equal(f.Entry.EntryNumber, page.LastEntryNumber);
        Assert.Equal(0, (await processing.EnqueueAsync(f.Project.Id, f.Timeline.Id, f.Actor.Id, false, 0, 3, 1)).Enqueued);
        Assert.Equal("Pending", (await f.Db.TimelineFactJobs.SingleAsync()).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_generation_runs_with_poller_disabled_and_unavailable_factory_is_durable(bool unavailable)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var services = new ServiceCollection();
        var options = (DbContextOptions<ProjectTimelineDbContext>)f.Db.GetService<IDbContextOptions>();
        services.AddSingleton(options);
        services.AddScoped(_ => new ProjectTimelineDbContext(options));
        services.AddTimelineFacts(new ConfigurationBuilder().Build()); // No poller switch, credentials or live configuration.
        var provider = new HostingProvider();
        services.AddScoped<ITimelineFactProvider>(_ => unavailable ? throw new InvalidOperationException("PRIVATE") : provider);
        await using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<TimelineFactApplication>();
        var state = await api.GenerateAsync(f.Entry.Id, f.Actor.Id, false, new());
        Assert.Equal(unavailable ? "Retry" : "Empty", state.LifecycleState);
        Assert.Equal(unavailable ? 0 : 1, provider.Calls);
        Assert.Empty(state.Facts);
        Assert.Equal(unavailable ? "Unavailable" : null, Assert.Single(state.Processing).ErrorCode);
    }

    private sealed class HostingProvider : ITimelineFactProvider
    {
        public int Calls;
        public string ExtractionModel => "fake";
        public Task<IReadOnlyList<TimelineFactCandidate>> ExtractAsync(TimelineFactExtraction source, CancellationToken ct)
        { Calls++; return Task.FromResult<IReadOnlyList<TimelineFactCandidate>>([]); }
        public Task<float[]> EmbedAsync(string claim, CancellationToken ct) => throw new NotSupportedException();
    }
}
