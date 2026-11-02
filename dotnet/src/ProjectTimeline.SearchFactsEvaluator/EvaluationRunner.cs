using System.Data.Common;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents.AgentModels;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.DataAccessLogic;

namespace ProjectTimeline.SearchFactsEvaluator;

public sealed record EvaluationScope(Guid ActorId, Guid TimelineId, Guid ProjectId, string Role,
    TimelineFactInformationCoverage Coverage);
public sealed record CapturedContent(string Kind, string? Text = null, string? CallId = null,
    string? Name = null, JsonElement? Payload = null, bool? Failed = null);
public sealed record CapturedMessage(string Role, IReadOnlyList<CapturedContent> Contents);
public interface IScenarioSession : IAsyncDisposable
{
    Task<TimelineFactInformationResponse> RunAsync(string question, CancellationToken ct);
    IReadOnlyList<CapturedMessage> Messages { get; }
    AgentCostSnapshot? Cost { get; }
    EvaluationScope? ObservedScope => null;
    AgentFailureDiagnostic? AgentFailure => null;
    TimelineFactCitationSnapshot? CitationDiagnostics => null;
}
public enum ScopeFailure { ActorUnavailable, ScopeUnavailable, ProjectMismatch, NoEligibleFacts }
public sealed class ScopeSafetyException(ScopeFailure failure) : Exception
{
    public ScopeFailure Failure { get; } = failure;
}
public sealed record SafeError(string Type, string Code);
public sealed record ScenarioOutcome(Guid RunId, string ScenarioId, string InputSha256, Scenario Scenario,
    EvaluationScope Scope, DateTimeOffset StartedUtc, DateTimeOffset EndedUtc, double ElapsedMilliseconds,
    string Outcome, TimelineFactInformationResponse? Response, IReadOnlyList<CapturedMessage> Messages,
    AgentCostSnapshot? Cost, SafeError? Error,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AgentFailureDiagnostic? AgentFailure = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TimelineFactCitationSnapshot? CitationDiagnostics = null);
public sealed class RunManifest
{
    public int SchemaVersion { get; init; } = 1;
    public Guid RunId { get; init; } = Guid.NewGuid();
    public required string InputPath { get; init; }
    public required string InputSha256 { get; init; }
    public required EvaluationScope Scope { get; init; }
    public string Model { get; init; } = "gpt-4o-mini";
    public required int ScenarioCount { get; init; }
    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedUtc { get; set; }
    public string Status { get; set; } = "Interrupted";
    public int Persisted { get; set; }
    public int Returned { get; set; }
    public int Failed { get; set; }
    public int Cancelled { get; set; }
    public int Unstarted => ScenarioCount - Persisted;
    public List<AgentCostSnapshot?> ObservedCostSessions { get; } = [];
    public string CapturePolicy { get; init; } = "Returned agent messages only; no streaming, wire payloads, headers, or arbitrary exception details. Mid-agent failures may have no returned messages.";
}
public static class EvaluationRunner
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string ManifestPath(string output) => output + ".manifest.json";
    public static void ValidateOutput(string input, string output)
    {
        input = Path.GetFullPath(input); output = Path.GetFullPath(output);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(input), Path.GetDirectoryName(output), comparison)
            || !output.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || string.Equals(input, output, comparison))
            throw new ArgumentException("Output must be a new adjacent .jsonl file.");
        if (!Directory.Exists(Path.GetDirectoryName(output))) throw new IOException("Output directory unavailable.");
        foreach (var path in new[] { output, ManifestPath(output) })
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Output or manifest already exists.");
    }

    public static async Task<RunManifest> RunAsync(ScenarioInput input, EvaluationScope scope, string output,
        Func<IScenarioSession> createSession, CancellationToken ct = default)
    {
        ValidateOutput(input.Path, output);
        // Reserve BOTH paths before the first model call; CreateNew also protects against races.
        using var results = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var manifestFile = new FileStream(ManifestPath(output), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(results, new UTF8Encoding(false), leaveOpen: true);
        var manifest = new RunManifest { InputPath = input.Path, InputSha256 = input.Sha256,
            Scope = scope, ScenarioCount = input.Scenarios.Count };
        void SaveManifest()
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
            manifestFile.Position = 0; manifestFile.Write(bytes); manifestFile.SetLength(bytes.Length); manifestFile.Flush(true);
        }
        SaveManifest();
        var interrupted = false;
        try
        {
            foreach (var scenario in input.Scenarios)
            {
                if (ct.IsCancellationRequested) break;
                var started = DateTimeOffset.UtcNow;
                var clock = Stopwatch.StartNew();
                IScenarioSession? session = null;
                TimelineFactInformationResponse? response = null;
                SafeError? error = null;
                AgentFailureDiagnostic? diagnostic = null;
                var outcome = "Returned";
                var stop = false;
                try
                {
                    session = createSession();
                    await using (session)
                    {
                        response = await session.RunAsync(scenario.Question, ct);
                        ct.ThrowIfCancellationRequested();
                    }
                }
                catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
                {
                    outcome = "Cancelled"; error = new("Cancellation", "Cancelled"); stop = true;
                    diagnostic = AgentFailureDiagnostic.FromException(ex, TimelineFactExecutionStage.Session);
                }
                catch (Exception ex)
                {
                    outcome = "Failed";
                    error = Classify(ex);
                    diagnostic = AgentFailureDiagnostic.FromException(ex, TimelineFactExecutionStage.Session);
                    stop = ex is UnauthorizedAccessException or KeyNotFoundException or ScopeSafetyException or DbException;
                }
                // An SDK-loop observation is supplementary, never the terminal domain authority.
                if (diagnostic is not null && session?.AgentFailure is { } observed)
                {
                    var cause = new AgentFailureCause(observed.Category, observed.ExceptionType, observed.HttpStatus, observed.ProviderCode, observed.Reason);
                    if (diagnostic.CausalChain?.Contains(cause) != true) diagnostic = diagnostic with { AgentObservation = cause };
                }
                clock.Stop();
                var row = new ScenarioOutcome(manifest.RunId, $"{manifest.RunId:N}:{scenario.Ordinal:D4}", input.Sha256,
                    scenario, session?.ObservedScope ?? scope, started, DateTimeOffset.UtcNow, clock.Elapsed.TotalMilliseconds, outcome,
                    outcome == "Returned" ? response : null, session?.Messages ?? [], session?.Cost, error, diagnostic, session?.CitationDiagnostics);
                // Cancellation cannot suppress the active row's durable terminal outcome.
                await writer.WriteLineAsync(JsonSerializer.Serialize(row, Json));
                await writer.FlushAsync(); results.Flush(true);
                manifest.Persisted++;
                if (outcome == "Returned") manifest.Returned++;
                else if (outcome == "Cancelled") manifest.Cancelled++;
                else manifest.Failed++;
                manifest.ObservedCostSessions.Add(row.Cost);
                SaveManifest();
                if (stop) { interrupted = true; break; }
            }
            if (!interrupted && manifest.Persisted == manifest.ScenarioCount && manifest.Cancelled == 0 && !ct.IsCancellationRequested)
                manifest.Status = "Completed";
            return manifest;
        }
        finally { manifest.EndedUtc = DateTimeOffset.UtcNow; SaveManifest(); }
    }

    public static SafeError Classify(Exception error) => error switch
    {
        ScopeSafetyException e => new("Scope", e.Failure.ToString()),
        UnauthorizedAccessException => new("Authorization", "AccessDenied"),
        KeyNotFoundException => new("Scope", "ScopeUnavailable"),
        DbException => new("Database", "DatabaseUnavailable"),
        TimelineFactInformationTimeoutException or OperationCanceledException => new("Timeout", "DeadlineExceeded"),
        TimelineFactInformationSynthesisException => new("Agent", "SynthesisUnavailable"),
        TimelineFactConflictException => new("Evidence", "EvidenceChanged"),
        _ => new("Execution", "ExecutionFailed")
    };
}
