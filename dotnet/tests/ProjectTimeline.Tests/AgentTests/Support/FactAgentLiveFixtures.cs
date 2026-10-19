using Microsoft.EntityFrameworkCore;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.DataAccessLogic;
using Xunit;

namespace ProjectTimeline.Tests.AgentTests.Support;

internal sealed record ExtractionLiveCase(string Id, TimelineFactExtraction Source,
    TimelineFactCandidate[] Canonical, string[][] RequiredClaims, string[] Forbidden);

internal static class FactAgentLiveFixtures
{
    // Called only from gated method bodies. No client or credential-bearing static fields/data providers.
    public static CustomOpenAIClient CreateClient()
    {
        var key = LiveModelCredentials.ResolveForLiveExecution(Environment.GetEnvironmentVariable(LiveModelGate.Variable));
        return new CustomOpenAIClient(key, isolateHarnessFacilities: true);
    }

    public static ExtractionLiveCase Extraction(string id) => id switch
    {
        "multi-claim" => new(id, new("Atlas vehicle register",
            "In project Atlas, car C-7 is red. In project Atlas, Nora owns car C-7."),
            [Candidate("In project Atlas, car C-7 is red."), Candidate("In project Atlas, Nora owns car C-7.")],
            [["Atlas", "C-7", "red"], ["Atlas", "Nora", "C-7", "own"]], ["blue", "Liam"]),
        "quantities-units" => new(id, new("Atlas equipment",
            "In project Atlas, tank R-2 holds 24 liters of coolant. In project Atlas, pump P-9 delivers 3 liters per minute."),
            [Candidate("In project Atlas, tank R-2 holds 24 liters of coolant."), Candidate("In project Atlas, pump P-9 delivers 3 liters per minute.")],
            [["Atlas", "R-2", "24", "lit(?:er|re)s?", "coolant"], ["Atlas", "P-9", "3", "lit(?:er|re)s?", "(?:per|/) ?min"]],
            ["gallons", "kilograms", "240", "30"]),
        "date-condition-negation" => new(id, new("Atlas shipment",
            "In project Atlas, shipment S-4 is planned for 12 March 2027 only if Nora approves it. Nora has not approved shipment S-4 in project Atlas."),
            [Candidate("In project Atlas, shipment S-4 is planned for 12 March 2027 only if Nora approves it."),
             Candidate("Nora has not approved shipment S-4 in project Atlas.")],
            [["Atlas", "S-4", "(?:12 March 2027|March 12,? 2027|2027-03-12)", "(?:plan|schedul|propos)", "(?:if|condition|contingent)", "Nora"],
             ["Atlas", "S-4", "Nora", "(?:not|no approval|unapproved|hasn't)", "approv"]],
            ["2026", "has been delivered", "was delivered", "Nora approved"]),
        "ambiguity-duplicate" => new(id, new("Atlas status",
            "In project Atlas, valve V-8 may be leaking; inspection has not confirmed a leak. In project Atlas, valve V-8 may be leaking."),
            [Candidate("In project Atlas, valve V-8 may be leaking."), Candidate("In project Atlas, inspection has not confirmed a leak in valve V-8.")],
            [["Atlas", "V-8", "(?:may|might|possib|suspect)", "leak"], ["Atlas", "V-8", "(?:not|unconfirmed|no confirmation)", "(?:leak|confirm)"]],
            ["confirmed to be leaking", "definitely", "repaired", "Nora"]),
        "empty" => new(id, new("", ""), [], [], []),
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    };

    public static TimelineFactCandidate Candidate(string claim, string key = "atlas", string type = "Equipment") =>
        new(claim, key, type, claim);

    public static readonly TimelineFactExtraction VerificationSource = new("Atlas order",
        "In project Atlas, Nora ordered 24 sensors on 12 March 2027. In project Atlas, Liam did not approve shipment S-4.");

    public static TimelineFactCandidate[] SupportedBatch() =>
    [
        Candidate("In project Atlas, Nora ordered 24 sensors on 12 March 2027.", "nora", "Person"),
        Candidate("In project Atlas, Liam did not approve shipment S-4.", "liam", "Person")
    ];
}

/// <summary>Only synthetic InMemory state. Seeding never invokes extraction, verification or embedding models.</summary>
internal sealed class FactAgentLiveDatabase : IAsyncDisposable
{
    private readonly DbContextOptions<ProjectTimelineDbContext> options = new DbContextOptionsBuilder<ProjectTimelineDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
    public ProjectTimelineDbContext Db { get; }
    public User Actor { get; } = new() { Email = "live-fixture@example.test", DisplayName = "Synthetic author", PasswordHash = "synthetic" };
    public Project Project { get; } = new() { Code = "Atlas", Name = "Synthetic Atlas" };
    public Timeline Timeline { get; } = new() { Code = "main", Name = "Main" };
    public Timeline ForeignTimeline { get; } = new() { Code = "foreign", Name = "Foreign scope" };

    private FactAgentLiveDatabase() => Db = new(options);

    public static async Task<FactAgentLiveDatabase> CreateAsync()
    {
        var f = new FactAgentLiveDatabase();
        f.Project.OwnerId = f.Actor.Id;
        f.Timeline.ProjectId = f.ForeignTimeline.ProjectId = f.Project.Id;
        f.Db.AddRange(f.Actor, f.Project, f.Timeline, f.ForeignTimeline);
        await f.Db.SaveChangesAsync();
        return f;
    }

    public TimelineFactScope Scope(TimelineEntry entry) => new(Project.Id, entry.TimelineId, entry.Id);

    public async Task<TimelineEntry> EntryAsync(string content, Timeline? timeline = null)
    {
        timeline ??= Timeline;
        var entry = new TimelineEntry { TimelineId = timeline.Id, AuthorId = Actor.Id, Title = "Atlas delivery",
            Content = content, EntryNumber = await Db.TimelineEntries.CountAsync(x => x.TimelineId == timeline.Id) + 1 };
        Db.Add(entry);
        await Db.SaveChangesAsync();
        return entry;
    }

    public async Task<TimelineFact> AcceptAsync(string claim, Timeline? timeline = null)
    {
        var entry = await EntryAsync(claim, timeline);
        var revision = await Db.Timelines.Where(x => x.Id == entry.TimelineId).Select(x => x.FactRevision).SingleAsync();
        var service = new TimelineFactService(Db);
        var proposal = await service.PrepareAsync(Scope(entry), Actor.Id, false, revision,
            new([FactAgentLiveFixtures.Candidate(claim, "atlas-delivery", "Responsibility")], [], []));
        await service.AcceptAsync(Scope(entry), proposal.Id, Actor.Id, false);
        return await Db.TimelineFacts.SingleAsync(x => x.CreationReviewId == proposal.Id);
    }

    public async Task GenerateAsync(TimelineEntry entry, params TimelineFactCandidate[] candidates)
    {
        var worker = new TimelineFactProcessing(() => new(options), new SeedProvider(candidates));
        var id = await worker.RequestGenerationAsync(Scope(entry), Actor.Id, false);
        Assert.Equal("Completed", await worker.ProcessAsync(id, "synthetic-live-test-seed"));
        await Db.Entry(Timeline).ReloadAsync();
        Assert.Equal("Generated", (await Db.TimelineFactProposals.AsNoTracking().SingleAsync(x => x.Id == id)).Status);
        Assert.Empty(await Db.TimelineFacts.Where(x => x.EntryId == entry.Id).ToArrayAsync());
    }

    private sealed class SeedProvider(TimelineFactCandidate[] candidates) : ITimelineFactProvider
    {
        public string ExtractionModel => "synthetic-seed-no-model";
        public Task<IReadOnlyList<TimelineFactCandidate>> ExtractAsync(TimelineFactExtraction source, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TimelineFactCandidate>>(candidates);
        public Task<float[]> EmbedAsync(string claim, CancellationToken ct) =>
            throw new InvalidOperationException("Generated candidates must not request embeddings.");
    }

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
