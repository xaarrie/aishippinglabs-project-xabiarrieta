using Microsoft.EntityFrameworkCore;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.DataAccessLogic;

namespace ProjectTimeline.UI.WebApi;

/// <summary>
/// Deployment handoff: install official pgvector and apply migrations before starting this application.
/// Startup schema failures are fatal. ProcessingEnabled defaults FALSE: no startup queue drain or legacy scan.
/// After migration and explicit billing approval set TimelineFacts__ProcessingEnabled=true and OPENAI_API_KEY
/// (existing credential convention). Optional TimelineFacts__ExtractionModel defaults gpt-4o-mini;
/// MaxJobsPerCycle defaults 10 (1..100); PollSeconds defaults 15 (1..300). Backfill is explicit enqueue only.
/// Review requests invoke the fact-only planner on demand, not the background gate. No whole-history fallback.
/// </summary>
public static class TimelineFactHosting
{
    public static IServiceCollection AddTimelineFacts(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ITimelineFactProvider>(sp =>
        {
            var key = configuration["OPENAI_API_KEY"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (string.IsNullOrWhiteSpace(key)) throw new TimelineFactProviderException("Unavailable");
            var client = new CustomOpenAIClient(key);
            var model = configuration["TimelineFacts:ExtractionModel"] ?? "gpt-4o-mini";
            return new TimelineFactOpenAIProvider(new TimelineFactExtractionAgent(client, model),
                new TimelineFactSemanticVerifierAgent(client, model), client);
        });
        services.AddScoped<TimelineFactService>();
        services.AddScoped(sp => new TimelineFactApplication(sp.GetRequiredService<ProjectTimelineDbContext>(),
            () => new LocalCustomHarnessAgent(new CustomOpenAIClient(isolateHarnessFacilities: true)),
            sp.GetRequiredService<TimelineFactProcessing>()));
        services.AddScoped(sp => new TimelineFactProcessing(
            () => new ProjectTimelineDbContext(sp.GetRequiredService<DbContextOptions<ProjectTimelineDbContext>>()),
            new DeferredProvider(() => sp.GetRequiredService<ITimelineFactProvider>())));
        services.AddHostedService<TimelineFactPoller>();
        return services;
    }

    // Enqueue never resolves credentials or constructs a remote provider.
    private sealed class DeferredProvider(Func<ITimelineFactProvider> resolve) : ITimelineFactProvider
    {
        public string ExtractionModel => resolve().ExtractionModel;
        public Task<IReadOnlyList<TimelineFactCandidate>> ExtractAsync(TimelineFactExtraction source, CancellationToken ct) => resolve().ExtractAsync(source, ct);
        public Task<float[]> EmbedAsync(string claim, CancellationToken ct) => resolve().EmbedAsync(claim, ct);
    }
}

public sealed class TimelineFactPoller(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<TimelineFactPoller> logger) : BackgroundService
{
    public async Task<int> PollOnceAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!configuration.GetValue("TimelineFacts:ProcessingEnabled", false)) return 0;
        var limit = Math.Clamp(configuration.GetValue("TimelineFacts:MaxJobsPerCycle", 10), 1, 100);
        Guid[] ids;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProjectTimelineDbContext>();
            var now = DateTime.UtcNow;
            ids = await db.TimelineFactJobs.AsNoTracking().Where(x =>
                ((x.State == "Pending" || x.State == "Retry") && (x.NextAttemptAtUtc == null || x.NextAttemptAtUtc <= now))
                || (x.State == "Running" && (x.LeaseExpiresAtUtc == null || x.LeaseExpiresAtUtc <= now)))
                .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).Take(limit).Select(x => x.Id).ToArrayAsync(ct);
        }
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<TimelineFactProcessing>().ProcessAsync(id, "fact-poller:" + Guid.NewGuid().ToString("N"), ct);
            ct.ThrowIfCancellationRequested();
        }
        return ids.Length;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PollOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { logger.LogWarning("Timeline fact processing cycle failed; queued work remains retryable. Verify deployment configuration."); }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("TimelineFacts:PollSeconds", 15), 1, 300)), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
