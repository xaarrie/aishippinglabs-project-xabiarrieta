using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.AgentModels;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.BusinessLogic.Agents.Tracking;
using ProjectTimeline.DataAccessLogic;

namespace ProjectTimeline.SearchFactsEvaluator;

public sealed record ScopeCandidate(Guid ActorId, Guid OwnerId, Guid ProjectId, string ProjectCode, string ProjectName,
    Guid TimelineId, string TimelineCode, string TimelineName, TimelineFactInformationCoverage Coverage);
public interface IEvaluationBackend
{
    Task<IReadOnlyList<ScopeCandidate>> ListScopesAsync(CancellationToken ct);
    Task<EvaluationScope> InspectAsync(EvaluatorCommand command, CancellationToken ct);
    IScenarioSession CreateSession(EvaluatorCommand command);
}

public sealed class ProductionBackend(string connection, string? apiKey) : IEvaluationBackend
{
    private ProjectTimelineDbContext CreateDb() => new(new DbContextOptionsBuilder<ProjectTimelineDbContext>()
        .UseNpgsql(connection).UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking).Options);

    // A transaction-level guard also rejects accidental writes from downstream tools. Never commit.
    private static async Task<IDbContextTransaction> ReadOnlyAsync(ProjectTimelineDbContext db, CancellationToken ct)
    {
        var transaction = await db.Database.BeginTransactionAsync(ct);
        try { await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct); return transaction; }
        catch { await transaction.DisposeAsync(); throw; }
    }

    public async Task<EvaluationScope> InspectAsync(EvaluatorCommand command, CancellationToken ct)
    {
        await using var db = CreateDb();
        await using var transaction = await ReadOnlyAsync(db, ct);
        return await InspectAsync(db, command, ct);
    }

    private static async Task<EvaluationScope> InspectAsync(ProjectTimelineDbContext db, EvaluatorCommand command,
        CancellationToken ct, bool requireFacts = true)
    {
        var role = await db.Users.Where(x => x.Id == command.ActorId && x.IsActive)
            .Select(x => x.UserRole.RoleName).SingleOrDefaultAsync(ct)
            ?? throw new ScopeSafetyException(ScopeFailure.ActorUnavailable);
        var project = await db.Timelines.Where(x => x.Id == command.TimelineId)
            .Select(x => (Guid?)x.ProjectId).SingleOrDefaultAsync(ct)
            ?? throw new ScopeSafetyException(ScopeFailure.ScopeUnavailable);
        if (project != command.ExpectedProjectId) throw new ScopeSafetyException(ScopeFailure.ProjectMismatch);
        var evidence = await new TimelineFactInformationSearch(db, new(project, command.TimelineId), command.ActorId,
            role == "Admin").CaptureCoverageAsync("coverage", ct);
        if (requireFacts && evidence.Coverage.AcceptedValidFacts == 0) throw new ScopeSafetyException(ScopeFailure.NoEligibleFacts);
        return new(command.ActorId, command.TimelineId, project, role, evidence.Coverage);
    }

    public async Task<IReadOnlyList<ScopeCandidate>> ListScopesAsync(CancellationToken ct)
    {
        await using var db = CreateDb();
        await using var transaction = await ReadOnlyAsync(db, ct);
        // Project only the safe discovery fields, never user profiles, entry text or fact content.
        var actors = await db.Users.Where(x => x.IsActive).OrderBy(x => x.Id)
            .Select(x => new { x.Id, Administrator = x.UserRole.RoleName == "Admin" }).ToListAsync(ct);
        var timelines = await db.Timelines.OrderBy(x => x.Project.Code).ThenBy(x => x.Code).Select(x => new
        { x.Id, x.Code, x.Name, x.ProjectId, x.Project.OwnerId, ProjectCode = x.Project.Code, ProjectName = x.Project.Name }).ToListAsync(ct);
        var candidates = new List<ScopeCandidate>();
        foreach (var timeline in timelines)
        foreach (var actor in actors)
        {
            if (!await new ProjectAuthorization(db).CanReadAsync(timeline.ProjectId, actor.Id, actor.Administrator, ct)) continue;
            var command = new EvaluatorCommand(null, null, actor.Id, timeline.Id, timeline.ProjectId, true, false);
            var scope = await InspectAsync(db, command, ct, requireFacts: false);
            candidates.Add(new(actor.Id, timeline.OwnerId, timeline.ProjectId, timeline.ProjectCode, timeline.ProjectName,
                timeline.Id, timeline.Code, timeline.Name, scope.Coverage));
        }
        return candidates;
    }

    public IScenarioSession CreateSession(EvaluatorCommand command) => new ProductionSession(CreateDb(), command, apiKey!);

    private sealed class ProductionSession(ProjectTimelineDbContext db, EvaluatorCommand command, string key) : IScenarioSession
    {
        private CapturingAgent? capture;
        private TimelineFactCitationCapture? citations;
        public TimelineFactCitationSnapshot? CitationDiagnostics => citations?.Snapshot;
        public IReadOnlyList<CapturedMessage> Messages => capture?.Messages ?? [];
        public AgentFailureDiagnostic? AgentFailure => capture?.FailureDiagnostic;
        public AgentCostSnapshot? Cost { get; private set; }
        public EvaluationScope? ObservedScope { get; private set; }
        public async Task<TimelineFactInformationResponse> RunAsync(string question, CancellationToken ct)
        {
            using var cost = AgentCostSession.Start();
            var citationCapture = new TimelineFactCitationCapture();
            citations = citationCapture;
            try
            {
                await using var transaction = await ReadOnlyAsync(db, ct);
                var scope = await InspectAsync(db, command, ct);
                ObservedScope = scope;
                var tracker = new FunctionCallTracker();
                return await new SearchEntriesAgent(db, new(scope.ProjectId, scope.TimelineId), scope.ActorId,
                    scope.Role == "Admin", agentFactory: () => capture = new CapturingAgent(new LocalCustomHarnessAgent(
                        new CustomOpenAIClient(key, transport: null, isolateHarnessFacilities: true, telemetryOptions: new(false)))),
                    callTracker: tracker, telemetryOptions: new(false)).RunAsync(question, ct, citationCapture);
            }
            finally { cost.Dispose(); Cost = cost.GetSnapshot(); }
        }
        public ValueTask DisposeAsync() => db.DisposeAsync();
    }
}
