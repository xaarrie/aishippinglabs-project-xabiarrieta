using System.ClientModel;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using ProjectTimeline.DataAccessLogic;
using Microsoft.Extensions.AI;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.AgentModels;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.BusinessLogic.Agents.Models;
using ProjectTimeline.SearchFactsEvaluator;
using Xunit;

namespace ProjectTimeline.Tests.AgentTests;

public sealed class SearchFactsEvaluationRunnerTests
{
    [Fact]
    public void Csv_preserves_order_exact_question_multiline_quotes_empty_metadata_and_byte_hash()
    {
        const string text = "question,category,type,extra\r\n\"  Who, \"\"Alice\"\"?\r\nWhy?  \", cat ,adversarial,\r\nSecond question,x,y,z\r\n";
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        var input = ScenarioInput.Parse(bytes, "scenarios.csv");
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), input.Sha256);
        Assert.Equal(2, input.Scenarios.Count);
        var first = input.Scenarios[0];
        Assert.Equal(1, first.Ordinal);
        Assert.Equal("  Who, \"Alice\"?\r\nWhy?  ", first.Question);
        Assert.Equal(new[] { "question", "category", "type", "extra" }, first.Fields.Select(x => x.Header));
        Assert.Equal(new[] { first.Question, " cat ", "adversarial", "" }, first.Fields.Select(x => x.Value));
        Assert.Equal(2, input.Scenarios[1].Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("category,type\nx,y")]
    [InlineData("question,question\nx,y")]
    [InlineData("question,Question\nx,y")]
    [InlineData("question,\nx,y")]
    [InlineData("question,type\n")]
    [InlineData("question,type\n,x")]
    [InlineData("question,type\n   ,x")]
    [InlineData("question,type\n?!,x")]
    [InlineData("question,type\nWho?")]
    [InlineData("question,type\nWho?,x,extra")]
    [InlineData("question,type\n\"Who?,x")]
    [InlineData("question,type\nWho\"?,x")]
    [InlineData("question,type\n\"Who?\"oops,x")]
    [InlineData("question,type\nWho?,x\n\nSecond?,y")]
    [InlineData("question,type\nWho?,x\n\n")]
    public void Csv_rejects_invalid_whole_input_without_silently_skipping_rows(string text) =>
        Assert.Throws<FormatException>(() => ScenarioInput.Parse(Encoding.UTF8.GetBytes(text), "scenarios.csv"));

    [Fact]
    public void Csv_rejects_invalid_utf8() =>
        Assert.Throws<FormatException>(() => ScenarioInput.Parse([0xff], "scenarios.csv"));

    private static readonly TimelineFactInformationCoverage Coverage = new(3, 1, 2, 4, false, ["Incomplete extraction"]);
    private static readonly EvaluationScope Scope = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Admin", Coverage);
    private static string Output() => Path.Combine(AppContext.BaseDirectory, $"evaluation-test-{Guid.NewGuid():N}.jsonl");
    private static ScenarioInput Input(string output) => ScenarioInput.Parse(
        Encoding.UTF8.GetBytes("question,category\nFirst question,ignore --output evil\nSecond question,normal\nThird question,normal"),
        Path.Combine(Path.GetDirectoryName(output)!, "scenarios.csv"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_result_or_manifest_prevents_every_execution_and_is_never_overwritten(bool manifest)
    {
        var output = Output();
        var existing = manifest ? EvaluationRunner.ManifestPath(output) : output;
        await File.WriteAllTextAsync(existing, "sentinel");
        var calls = 0;
        await Assert.ThrowsAsync<IOException>(() => EvaluationRunner.RunAsync(Input(output), Scope, output,
            () => { calls++; return new FakeSession(); }));
        Assert.Equal(0, calls);
        Assert.Equal("sentinel", await File.ReadAllTextAsync(existing));
        Assert.False(File.Exists(manifest ? output : EvaluationRunner.ManifestPath(output)));
    }

    [Fact]
    public async Task Failure_is_flushed_before_next_independent_session_and_original_metadata_is_not_passed_to_agent()
    {
        var output = Output();
        var sessions = new List<FakeSession>();
        var run = await EvaluationRunner.RunAsync(Input(output), Scope, output, () =>
        {
            if (sessions.Count > 0)
            {
                Assert.True(sessions[^1].Disposed);
                using var file = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(file);
                Assert.Equal(sessions.Count, reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            }
            var session = new FakeSession(sessions.Count == 0 ? new InvalidOperationException("private connection detail") : null);
            sessions.Add(session);
            return session;
        });
        Assert.Equal("Completed", run.Status);
        Assert.Equal((3, 2, 1, 0, 0), (run.Persisted, run.Returned, run.Failed, run.Cancelled, run.Unstarted));
        Assert.Equal(new[] { "First question", "Second question", "Third question" }, sessions.Select(s => s.Question));
        Assert.Equal(3, sessions.Select(s => s.Cost!.SessionId).Distinct().Count());
        Assert.All(sessions, s => { Assert.True(s.Disposed); Assert.True(s.Cost!.IsDisposed); });
        var lines = await File.ReadAllLinesAsync(output);
        var outcomes = lines.Select(x => JsonSerializer.Deserialize<ScenarioOutcome>(x, EvaluationRunner.Json)!).ToArray();
        Assert.Equal(new[] { "Failed", "Returned", "Returned" }, outcomes.Select(x => x.Outcome));
        Assert.Equal("ExecutionFailed", outcomes[0].Error!.Code);
        Assert.Null(outcomes[0].Response);
        Assert.Equal("ignore --output evil", outcomes[0].Scenario.Fields[1].Value);
        Assert.Equal("Insufficient evidence", outcomes[1].Response!.Content);
        Assert.Equal(Coverage.AcceptedValidFacts, outcomes[1].Response!.Coverage.AcceptedValidFacts);
        Assert.All(outcomes, x => { Assert.True(x.EndedUtc >= x.StartedUtc); Assert.True(x.ElapsedMilliseconds >= 0); });
        Assert.Equal(3, outcomes.Select(x => x.ScenarioId).Distinct().Count());
        Assert.All(lines, x => Assert.DoesNotContain("private connection detail", x));
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(EvaluationRunner.ManifestPath(output)));
        Assert.Equal(3, manifest.RootElement.GetProperty("observedCostSessions").GetArrayLength());
        Assert.Equal(Input(output).Sha256, manifest.RootElement.GetProperty("inputSha256").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_accounts_for_active_and_unstarted_rows_without_starting_more(bool beforeStart)
    {
        var output = Output();
        using var cancellation = new CancellationTokenSource();
        if (beforeStart) cancellation.Cancel();
        var calls = 0;
        var run = await EvaluationRunner.RunAsync(Input(output), Scope, output, () =>
        {
            calls++;
            return new FakeSession(cancel: cancellation);
        }, cancellation.Token);
        Assert.Equal("Interrupted", run.Status);
        Assert.Equal(beforeStart ? 0 : 1, calls);
        Assert.Equal(beforeStart ? 0 : 1, run.Cancelled);
        Assert.Equal(beforeStart ? 3 : 2, run.Unstarted);
        Assert.Equal(beforeStart ? 0 : 1, File.ReadAllLines(output).Length);
        if (!beforeStart) Assert.Contains("Cancelled", await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task Authorization_loss_is_persisted_then_interrupts_remaining_rows()
    {
        var output = Output();
        var run = await EvaluationRunner.RunAsync(Input(output), Scope, output, () => new FakeSession(new UnauthorizedAccessException("secret")));
        Assert.Equal("Interrupted", run.Status);
        Assert.Equal((1, 1, 2), (run.Persisted, run.Failed, run.Unstarted));
        Assert.DoesNotContain("secret", await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task Unsafe_global_failure_on_last_row_still_marks_manifest_interrupted()
    {
        var output = Output();
        var input = ScenarioInput.Parse(Encoding.UTF8.GetBytes("question\nOnly question"), Path.Combine(Path.GetDirectoryName(output)!, "single.csv"));
        var run = await EvaluationRunner.RunAsync(input, Scope, output, () => new FakeSession(new ScopeSafetyException(ScopeFailure.NoEligibleFacts)));
        Assert.Equal("Interrupted", run.Status);
        Assert.Equal((1, 1, 0), (run.Persisted, run.Failed, run.Unstarted));
    }

    [Theory]
    [InlineData("--list-scopes --actor-id bad")]
    [InlineData("--preflight")]
    [InlineData("--unknown value")]
    [InlineData("--input file --input other")]
    public void Cli_rejects_ambiguous_or_incomplete_options(string arguments) =>
        Assert.Throws<ArgumentException>(() => EvaluatorCommand.Parse(arguments.Split(' ')));

    [Fact]
    public void Cli_requires_explicit_scope_and_never_uses_scenario_metadata()
    {
        Assert.True(EvaluatorCommand.Parse(["--list-scopes"]).ListScopes);
        var command = EvaluatorCommand.Parse(["--input", "scenarios.csv", "--output", "result.jsonl",
            "--actor-id", Scope.ActorId.ToString(), "--timeline-id", Scope.TimelineId.ToString(),
            "--expected-project-id", Scope.ProjectId.ToString(), "--preflight"]);
        Assert.True(command.Preflight);
        Assert.Equal(Scope.ActorId, command.ActorId);
        Assert.Equal(Scope.ProjectId, command.ExpectedProjectId);
    }

    [Fact]
    public async Task Capture_retains_returned_messages_before_typed_result_validation_without_exception_payloads()
    {
        var capture = new CapturingAgent(new ReturnedInvalidAgent());
        var response = await capture.RunAsync<TimelineFactInformationResult>("question", CancellationToken.None);
        Assert.ThrowsAny<Exception>(() => _ = response.Result);
        Assert.Equal(3, capture.Messages.Count);
        Assert.True(capture.Messages[1].Contents[0].Failed);
        Assert.Null(capture.Messages[1].Contents[0].Payload);
        var json = JsonSerializer.Serialize(capture.Messages, EvaluationRunner.Json);
        Assert.Contains("not valid json", json);
        Assert.Contains("search_facts_batch", json);
        Assert.DoesNotContain("sensitive exception", json);
        Assert.DoesNotContain("private-header", json);
    }

    [Fact]
    public async Task Structured_capture_forwards_options_and_accumulates_returned_stages_before_lazy_access()
    {
        using var cancellation = new CancellationTokenSource();
        var inner = new StructuredCaptureProbe();
        var capture = new CapturingAgent(inner);
        var options = new StructuredAgentRunOptions
        {
            ChatOptions = new(), SerializerOptions = new(JsonSerializerDefaults.Web)
            { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() },
            ValidateResponse = _ => { }, InvalidResult = () => new InvalidDataException()
        };
        capture.Init("plan", "gpt-4o-mini", "offline", []);
        var plan = await capture.RunAsync<TimelineFactInformationPlan>("original question", options, cancellation.Token);
        Assert.Equal("Search", plan.Result.Decision);
        var first = capture.Messages;
        capture.Init("synthesis", "gpt-4o-mini", "offline", []);
        var synthesis = await capture.RunAsync<TimelineFactInformationResult>("evidence", options, cancellation.Token);
        Assert.ThrowsAny<Exception>(() => _ = synthesis.Result);
        Assert.Single(first); Assert.Equal(2, capture.Messages.Count);
        Assert.Same(options, inner.Options); Assert.Equal(cancellation.Token, inner.Token);
        var original = new TimelineFactProviderException("InvalidResponse", "ResponseSchema");
        inner.Error = original; // Models can be rejected inside the structured adapter before any return.
        Assert.Same(original, await Assert.ThrowsAsync<TimelineFactProviderException>(() =>
            capture.RunAsync<TimelineFactInformationResult>("rejected", options, cancellation.Token)));
        Assert.Equal(2, capture.Messages.Count);
        Assert.Equal("ResponseSchema", capture.FailureDiagnostic!.Reason);
        Assert.DoesNotContain(DiagnosticSecret, JsonSerializer.Serialize(capture.Messages, EvaluationRunner.Json));
        Assert.Empty(new CapturingAgent(new StructuredCaptureProbe()).Messages);
    }

    private sealed class StructuredCaptureProbe : ICustomAgent
    {
        public StructuredAgentRunOptions? Options;
        public CancellationToken Token;
        public Exception? Error;
        private int calls;
        public event EventHandler<FunctionCallEventArgs>? FunctionCalled { add { } remove { } }
        public void Init(string name, string model, string instructions, List<AITool> tools) => Assert.Empty(tools);
        public Task<AgentResponse> RunAsync(string message) => throw new Xunit.Sdk.XunitException("Untyped path");
        public Task<AgentResponse<T>> RunAsync<T>(string message) where T : class => throw new Xunit.Sdk.XunitException("Legacy path");
        public Task<AgentResponse<T>> RunAsync<T>(string message, StructuredAgentRunOptions options, CancellationToken ct) where T : class
        {
            Options = options; Token = ct;
            if (Error is not null) throw Error;
            var text = ++calls == 1 ? "{\"decision\":\"Search\",\"queries\":[\"alice\"],\"refusalReason\":null}" : "not valid json";
            var returned = new ChatMessage(ChatRole.Assistant, text) { AdditionalProperties = new() { ["private-header"] = DiagnosticSecret } };
            var response = new AgentResponse(returned) { RawRepresentation = new { secret = DiagnosticSecret } };
            return Task.FromResult(new AgentResponse<T>(response, options.SerializerOptions));
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Preflight_and_invalid_scope_never_create_results_or_model_sessions(bool authorized, bool preflight)
    {
        var path = Output();
        var csv = Path.ChangeExtension(path, ".csv");
        await File.WriteAllTextAsync(csv, "question,type\nWho owns delivery?,normal");
        var backend = new FakeBackend(authorized);
        using var output = new StringWriter();
        string[] arguments = ["--input", csv, "--output", path, "--actor-id", Scope.ActorId.ToString(),
            "--timeline-id", Scope.TimelineId.ToString(), "--expected-project-id", Scope.ProjectId.ToString()];
        var exit = await ProjectTimeline.SearchFactsEvaluator.Program.ExecuteAsync(preflight ? [..arguments, "--preflight"] : arguments,
            key => key == "ConnectionStrings__ProjectTimeline" ? "Host=localhost;Database=fixture;Username=fixture"
                : key == "OPENAI_API_KEY" && !preflight ? "synthetic-test-key" : throw new InvalidOperationException("Unexpected credential lookup"),
            output, (_, _) => backend);
        Assert.Equal(authorized ? 0 : 2, exit);
        Assert.Equal(1, backend.Inspections);
        Assert.Equal(0, backend.Sessions);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(EvaluationRunner.ManifestPath(path)));
        Assert.DoesNotContain("private database failure", output.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Whole_csv_and_both_output_paths_are_validated_before_backend_creation(bool invalidCsv)
    {
        var path = Output();
        var csv = Path.ChangeExtension(path, ".csv");
        await File.WriteAllTextAsync(csv, "question,type\nValid question,normal\n" + (invalidCsv ? "Missing field" : "Other question,normal"));
        if (!invalidCsv) await File.WriteAllTextAsync(EvaluationRunner.ManifestPath(path), "existing manifest");
        var environmentCalls = 0;
        var backendCalls = 0;
        using var output = new StringWriter();
        var exit = await ProjectTimeline.SearchFactsEvaluator.Program.ExecuteAsync(["--input", csv, "--output", path,
            "--actor-id", Scope.ActorId.ToString(), "--timeline-id", Scope.TimelineId.ToString(), "--expected-project-id", Scope.ProjectId.ToString()],
            _ => { environmentCalls++; throw new InvalidOperationException("Environment must not be read"); }, output,
            (_, _) => { backendCalls++; throw new InvalidOperationException("Backend must not be created"); });
        Assert.Equal(1, exit);
        Assert.Equal(0, environmentCalls);
        Assert.Equal(0, backendCalls);
        Assert.False(File.Exists(path));
        if (!invalidCsv) Assert.Equal("existing manifest", await File.ReadAllTextAsync(EvaluationRunner.ManifestPath(path)));
    }

    [Fact]
    public async Task Discovery_uses_no_provider_credential_and_does_not_execute_sessions()
    {
        var backend = new FakeBackend(true);
        using var output = new StringWriter();
        var exit = await ProjectTimeline.SearchFactsEvaluator.Program.ExecuteAsync(["--list-scopes"],
            key => key == "ConnectionStrings__ProjectTimeline" ? "Host=localhost;Database=fixture;Username=fixture"
                : throw new InvalidOperationException("Unexpected credential lookup"), output, (_, _) => backend);
        Assert.Equal(0, exit);
        Assert.Equal(1, backend.Lists);
        Assert.Equal(0, backend.Sessions);
        Assert.Equal(0, backend.Inspections);
        Assert.Equal("[]", output.ToString().Trim());
    }

    private static void AssertDiagnostic(AgentFailureDiagnostic expected, AgentFailureDiagnostic? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal((expected.Category, expected.ExceptionType, expected.HttpStatus, expected.ProviderCode),
            (actual.Category, actual.ExceptionType, actual.HttpStatus, actual.ProviderCode));
        Assert.NotNull(actual.CausalChain);
    }

    private const string DiagnosticSecret = "PRIVATE_DIAGNOSTIC_SENTINEL";

    [Theory]
    [InlineData("init")]
    [InlineData("text")]
    [InlineData("typed")]
    [InlineData("text-token")]
    [InlineData("typed-token")]
    public async Task Diagnostic_captures_local_json_error_and_rethrows_the_identical_exception(string operation)
    {
        var original = new JsonException(DiagnosticSecret);
        var capture = new CapturingAgent(new DiagnosticAgent(original, failInit: operation == "init"));
        var thrown = operation == "init"
            ? Record.Exception(() => capture.Init("offline", "gpt-4o-mini", "offline", []))
            : await Record.ExceptionAsync(() => operation switch
            {
                "text" => capture.RunAsync("offline"),
                "typed" => capture.RunAsync<TimelineFactInformationResult>("offline"),
                "text-token" => capture.RunAsync("offline", CancellationToken.None),
                _ => capture.RunAsync<TimelineFactInformationResult>("offline", CancellationToken.None)
            });
        Assert.Same(original, thrown);
        AssertDiagnostic(new("JsonSchemaParse", "JsonException"), capture.FailureDiagnostic);
        Assert.DoesNotContain(DiagnosticSecret, JsonSerializer.Serialize(capture.FailureDiagnostic, EvaluationRunner.Json));
    }

    [Theory]
    [InlineData(401, "invalid_api_key", "invalid_api_key")]
    [InlineData(400, "model_not_found", "model_not_found")]
    [InlineData(429, "rate_limit_exceeded", "rate_limit_exceeded")]
    [InlineData(429, "insufficient_quota", "insufficient_quota")]
    [InlineData(503, DiagnosticSecret, "Other")]
    [InlineData(401, null, null)]
    public async Task Diagnostic_extracts_only_allowlisted_provider_fields_from_one_offline_attempt(int status, string? code, string? expected)
    {
        using var handler = new DiagnosticHttp(status, code);
        using var http = new HttpClient(handler);
        var capture = new CapturingAgent(new LocalCustomHarnessAgent(new CustomOpenAIClient("offline-synthetic-key", http,
            isolateHarnessFacilities: true, telemetryOptions: new(false))));
        capture.Init("offline", "gpt-4o-mini", "offline", []);
        var error = await Record.ExceptionAsync(() => capture.RunAsync<TimelineFactInformationResult>("offline", CancellationToken.None));
        Assert.NotNull(error);
        Assert.Equal(1, handler.Calls);
        AssertDiagnostic(new("HttpProvider", "ClientResultException", status, expected), capture.FailureDiagnostic);
        var json = JsonSerializer.Serialize(capture.FailureDiagnostic, EvaluationRunner.Json);
        Assert.DoesNotContain(DiagnosticSecret, json);
        Assert.DoesNotContain("offline-synthetic-key", json);
        Assert.DoesNotContain("https://", json);
        var terminal = TimelineFactFailureContext.Record(new TimelineFactInformationSynthesisException(error), TimelineFactExecutionStage.Model);
        var path = Output();
        await EvaluationRunner.RunAsync(Input(path), Scope, path, () => new OuterFailureSession(terminal));
        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            var row = JsonSerializer.Deserialize<ScenarioOutcome>(line, EvaluationRunner.Json)!;
            Assert.Equal(new("Agent", "SynthesisUnavailable"), row.Error);
            Assert.Equal("Synthesis", row.AgentFailure!.Category);
            Assert.Equal("Model", row.AgentFailure.ExecutionStage);
            Assert.Contains(row.AgentFailure.CausalChain!, node => node.ExceptionType == "ClientResultException"
                && node.HttpStatus == status && node.ProviderCode == expected);
            Assert.DoesNotContain(DiagnosticSecret, line);
            Assert.DoesNotContain("offline-synthetic-key", line);
            Assert.DoesNotContain("https://", line);
        }
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("oversized")]
    [InlineData("deep")]
    [InlineData("message-only")]
    public async Task Diagnostic_does_not_infer_codes_from_unavailable_or_out_of_bounds_response_content(string mode)
    {
        var body = mode switch
        {
            "malformed" => "{" + DiagnosticSecret,
            "oversized" => JsonSerializer.Serialize(new { error = new { code = "invalid_api_key", message = new string('x', 17000) + DiagnosticSecret } }),
            "deep" => "{\"error\":{\"code\":\"invalid_api_key\"},\"ignored\":" + new string('[', 20) + "0" + new string(']', 20) + "}",
            _ => JsonSerializer.Serialize(new { error = new { message = "invalid_api_key " + DiagnosticSecret } })
        };
        using var handler = new DiagnosticHttp(400, null, body);
        using var http = new HttpClient(handler);
        var capture = new CapturingAgent(new LocalCustomHarnessAgent(new CustomOpenAIClient("offline-synthetic-key", http,
            isolateHarnessFacilities: true, telemetryOptions: new(false))));
        capture.Init("offline", "gpt-4o-mini", "offline", []);
        await Assert.ThrowsAsync<ClientResultException>(() => capture.RunAsync("offline", CancellationToken.None));
        Assert.Equal(1, handler.Calls);
        AssertDiagnostic(new("HttpProvider", "ClientResultException", 400), capture.FailureDiagnostic);
        Assert.DoesNotContain(DiagnosticSecret, JsonSerializer.Serialize(capture.FailureDiagnostic, EvaluationRunner.Json));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(400)]
    [InlineData(429)]
    [InlineData(503)]
    public void Diagnostic_http_request_status_is_observed_without_inventing_a_provider_code(int status)
    {
        var error = new HttpRequestException(DiagnosticSecret, null, (HttpStatusCode)status);
        AssertDiagnostic(new("HttpProvider", "HttpRequestException", status), AgentFailureDiagnostic.FromException(error));
    }

    [Fact]
    public void Diagnostic_chain_is_bounded_and_unknown_types_transport_and_cancellation_are_safe()
    {
        AssertDiagnostic(new("Other", "Other"), AgentFailureDiagnostic.FromException(new PrivateDiagnosticException()));
        AssertDiagnostic(new("Transport", "HttpRequestException"), AgentFailureDiagnostic.FromException(new HttpRequestException(DiagnosticSecret)));
        AssertDiagnostic(new("Cancellation", "TaskCanceledException"), AgentFailureDiagnostic.FromException(new TaskCanceledException(DiagnosticSecret)));
        AssertDiagnostic(new("Cancellation", "OperationCanceledException"), AgentFailureDiagnostic.FromException(new OperationCanceledException(DiagnosticSecret)));
        AssertDiagnostic(new("JsonSchemaParse", "FormatException"), AgentFailureDiagnostic.FromException(new FormatException(DiagnosticSecret)));
        Exception chain = new JsonException(DiagnosticSecret);
        for (var i = 0; i < 7; i++) chain = new Exception(DiagnosticSecret, chain);
        AssertDiagnostic(new("JsonSchemaParse", "JsonException"), AgentFailureDiagnostic.FromException(chain));
        var bounded = AgentFailureDiagnostic.FromException(new Exception(DiagnosticSecret, chain));
        AssertDiagnostic(new("Other", "Other"), bounded);
        Assert.Equal(8, bounded.CausalChain!.Count);
        Assert.DoesNotContain(DiagnosticSecret, JsonSerializer.Serialize(bounded, EvaluationRunner.Json));
    }

    [Fact]
    public async Task Diagnostic_row_keeps_normalized_synthesis_error_and_safe_underlying_cause()
    {
        var path = Output();
        var run = await EvaluationRunner.RunAsync(Input(path), Scope, path, () => new NormalizingDiagnosticSession());
        Assert.Equal((3, 3), (run.Persisted, run.Failed));
        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            var row = JsonSerializer.Deserialize<ScenarioOutcome>(line, EvaluationRunner.Json)!;
            Assert.Equal(new("Agent", "SynthesisUnavailable"), row.Error);
            AssertDiagnostic(new("Synthesis", "TimelineFactInformationSynthesisException"), row.AgentFailure);
            Assert.Equal(new("JsonSchemaParse", "JsonException"), row.AgentFailure!.AgentObservation);
            Assert.Empty(row.Messages);
            Assert.Null(row.Response);
            Assert.DoesNotContain(DiagnosticSecret, line);
        }
    }

    [Fact]
    public async Task Diagnostic_success_clears_prior_failure_and_optional_field_is_omitted_for_legacy_sessions()
    {
        var inner = new DiagnosticAgent(new JsonException(DiagnosticSecret));
        var capture = new CapturingAgent(inner);
        await Assert.ThrowsAsync<JsonException>(() => capture.RunAsync("offline"));
        Assert.NotNull(capture.FailureDiagnostic);
        inner.Fail = false;
        await capture.RunAsync("offline");
        Assert.Null(capture.FailureDiagnostic);
        var path = Output();
        await EvaluationRunner.RunAsync(Input(path), Scope, path, () => new FakeSession());
        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            using var row = JsonDocument.Parse(line);
            Assert.False(row.RootElement.TryGetProperty("agentFailure", out _));
            Assert.False(row.RootElement.TryGetProperty("citationDiagnostics", out _));
            var legacy = JsonSerializer.Deserialize<ScenarioOutcome>(line, EvaluationRunner.Json)!;
            Assert.Null(legacy.AgentFailure); Assert.Null(legacy.CitationDiagnostics);
        }
    }

    [Theory]
    [InlineData("lazy", "ResultParsing", "JsonSchemaParse", null)]
    [InlineData("ledger", "Revalidation", "Research", "ToolCallBudgetExhausted")]
    [InlineData("revision", "Revalidation", "Research", "FactRevisionChanged")]
    [InlineData("citation", "Synthesis", "Synthesis", "UnknownFactCitation")]
    [InlineData("context", "Synthesis", "Synthesis", "UnknownContextCitation")]
    [InlineData("blank", "Synthesis", "Synthesis", "MissingContent")]
    [InlineData("null", "ResultParsing", "Synthesis", null)]
    [InlineData("coverage", "Coverage", "Authorization", null)]
    [InlineData("initialization", "Initialization", "InvalidOperation", null)]
    public async Task Terminal_row_captures_late_Search_failures_without_decorator_failure(string kind, string stage, string category, string? reason)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        var fake = new InformationFakeAgent { Result = new() { Content = "Answer", CitedFactIds = [fact.Id] } };
        if (kind == "lazy") fake.Json = "PRIVATE_INVALID_JSON";
        if (kind == "null") fake.Json = "null";
        if (kind == "citation") fake.Result.CitedFactIds = [Guid.NewGuid()];
        if (kind == "context") fake.Result.CitedContextEvidenceIds = [Guid.NewGuid()];
        if (kind == "blank") fake.Result.Content = " ";
        if (kind == "coverage") { f.Actor.IsActive = false; await f.Db.SaveChangesAsync(); }
        if (kind == "revision") fake.Script = async () =>
        {
            using var raw = f.Unguarded();
            raw.Set<Timeline>().Single().FactRevision++;
            await raw.SaveChangesAsync();
        };
        var capture = new CapturingAgent(fake);
        var search = new SearchEntriesAgent(f.Db, new(f.Project.Id, f.Timeline.Id), f.Actor.Id, false,
            () => kind == "initialization" ? throw new InvalidOperationException(DiagnosticSecret) : capture,
            options: kind == "ledger" ? new(maxToolCalls: 1) : null);
        var path = Output();
        var input = ScenarioInput.Parse(Encoding.UTF8.GetBytes("question\nAlice"), Path.Combine(Path.GetDirectoryName(path)!, "single.csv"));
        await EvaluationRunner.RunAsync(input, Scope, path, () => new SearchSession(search, capture));
        var line = Assert.Single(await File.ReadAllLinesAsync(path));
        using var row = JsonDocument.Parse(line);
        var diagnostic = row.RootElement.GetProperty("agentFailure");
        Assert.Equal(stage, diagnostic.GetProperty("executionStage").GetString());
        var chain = diagnostic.GetProperty("causalChain").EnumerateArray().ToArray();
        Assert.Contains(chain, node => node.GetProperty("category").GetString() == category
            && (reason is null || node.GetProperty("reason").GetString() == reason));
        Assert.Null(capture.FailureDiagnostic);
        Assert.DoesNotContain(DiagnosticSecret, line);
        // Returned raw model text remains captured; exception payloads do not.
        if (kind == "lazy") Assert.Contains("PRIVATE_INVALID_JSON", line);
        Assert.Equal(kind switch { "revision" => "EvidenceChanged", "coverage" => "AccessDenied", _ => "SynthesisUnavailable" },
            row.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Terminal_domain_failure_wins_over_supplementary_loop_cancellation()
    {
        var path = Output();
        await EvaluationRunner.RunAsync(Input(path), Scope, path, () => new OuterFailureSession(
            new TimelineFactResearchException("EvidenceReadFailed"), new("Cancellation", "OperationCanceledException")));
        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            using var row = JsonDocument.Parse(line);
            var diagnostic = row.RootElement.GetProperty("agentFailure");
            Assert.Equal("Research", diagnostic.GetProperty("category").GetString());
            Assert.Equal("EvidenceReadFailed", diagnostic.GetProperty("reason").GetString());
            Assert.DoesNotContain(DiagnosticSecret, line);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Session_creation_and_disposal_failures_receive_terminal_diagnostics(bool disposal)
    {
        var path = Output();
        await EvaluationRunner.RunAsync(Input(path), Scope, path, () => disposal
            ? new OuterFailureSession(null, disposeFailure: true) : throw new FormatException(DiagnosticSecret));
        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            var row = JsonSerializer.Deserialize<ScenarioOutcome>(line, EvaluationRunner.Json)!;
            Assert.Equal("Failed", row.Outcome);
            Assert.Equal("JsonSchemaParse", row.AgentFailure!.Category);
            Assert.Null(row.Response);
            Assert.DoesNotContain(DiagnosticSecret, line);
        }
    }

    [Fact]
    public void Diagnostic_domain_subclasses_unknown_codes_and_legacy_payloads_stay_safe()
    {
        var research = AgentFailureDiagnostic.FromException(new TimelineFactResearchException(DiagnosticSecret));
        Assert.Equal("Research", research.Category);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(research, EvaluationRunner.Json));
        Assert.Equal("Other", json.RootElement.GetProperty("reason").GetString());
        Assert.DoesNotContain(DiagnosticSecret, json.RootElement.GetRawText());
        var legacy = JsonSerializer.Deserialize<AgentFailureDiagnostic>("{\"category\":\"Other\",\"exceptionType\":\"Other\"}", EvaluationRunner.Json);
        Assert.Equal(new("Other", "Other"), legacy);
        using var budget = AgentExecutionBudget.Start(new(1, 1, TimeSpan.FromSeconds(60)));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        // Exercise the real budget subtype instead of its OperationCanceledException base.
        var error = Assert.Throws<AgentExecutionBudgetException>(() =>
        {
            using var stopped = AgentExecutionBudget.Start(new(1, 1, TimeSpan.FromSeconds(60)), cancel.Token);
            stopped.ThrowIfStopped();
        });
        Assert.Equal("Budget", AgentFailureDiagnostic.FromException(error).Category);
        var unknown = TimelineFactFailureContext.Record(new TimelineFactInformationSynthesisException((TimelineFactSynthesisFailureReason)int.MaxValue),
            (TimelineFactExecutionStage)int.MaxValue);
        var safe = AgentFailureDiagnostic.FromException(unknown);
        Assert.Equal("Other", safe.Reason);
        Assert.Null(safe.ExecutionStage);
        Assert.Equal("Other", AgentFailureDiagnostic.FromException(new ScopeSafetyException((ScopeFailure)int.MaxValue)).Reason);
    }

    [Theory]
    [InlineData("InvalidQueryBatch")]
    [InlineData("InvalidQuery")]
    [InlineData("EvidenceChanged")]
    [InlineData("UnknownOrInvalidEntryIds")]
    [InlineData("MissingResearchPhase")]
    [InlineData("ToolCallBudgetExhausted")]
    [InlineData("InvalidResearchPhase")]
    [InlineData("SourceContextUnavailable")]
    [InlineData("FactRevisionChanged")]
    [InlineData("EvidenceBudgetExhausted")]
    [InlineData("AccessDenied")]
    [InlineData("ScopeUnavailable")]
    [InlineData("EvidenceReadFailed")]
    public void Known_research_reasons_are_not_hidden_by_InvalidOperationException(string code)
    {
        var diagnostic = AgentFailureDiagnostic.FromException(new TimelineFactResearchException(code));
        Assert.Equal("Research", diagnostic.Category);
        Assert.Equal("TimelineFactResearchException", diagnostic.ExceptionType);
        Assert.Equal(code, diagnostic.Reason);
    }

    private sealed class SearchSession(SearchEntriesAgent search, CapturingAgent capture) : IScenarioSession
    {
        private readonly TimelineFactCitationCapture citations = new();
        public TimelineFactCitationSnapshot? CitationDiagnostics => citations.Snapshot;
        public IReadOnlyList<CapturedMessage> Messages => capture.Messages;
        public AgentCostSnapshot? Cost => null;
        public AgentFailureDiagnostic? AgentFailure => capture.FailureDiagnostic;
        public Task<TimelineFactInformationResponse> RunAsync(string question, CancellationToken ct) => search.RunAsync(question, ct, citations);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    [Theory]
    [InlineData("response")]
    [InlineData("failure")]
    [InlineData("cancel")]
    [InlineData("dispose")]
    public async Task Citation_snapshot_is_serialized_after_disposal_for_every_terminal_outcome(string ending)
    {
        var path = Output();
        var input = ScenarioInput.Parse(Encoding.UTF8.GetBytes("question\nOnly question"), Path.Combine(Path.GetDirectoryName(path)!, "single.csv"));
        using var stop = new CancellationTokenSource();
        var session = new CitationSnapshotSession(ending, stop);
        await EvaluationRunner.RunAsync(input, Scope, path, () => session, stop.Token);
        var line = Assert.Single(await File.ReadAllLinesAsync(path));
        var row = JsonSerializer.Deserialize<ScenarioOutcome>(line, EvaluationRunner.Json)!;
        Assert.Equal(ending == "response" ? "Returned" : ending == "cancel" ? "Cancelled" : "Failed", row.Outcome);
        Assert.Equal<Guid>(session.CitationDiagnostics!.Available!.AvailableFactIds, row.CitationDiagnostics!.Available!.AvailableFactIds);
        Assert.Equal(1, row.CitationDiagnostics.Available.AvailableFactCount);
        Assert.Null(row.CitationDiagnostics.Observed); Assert.Null(row.CitationDiagnostics.FinalLedger);
        Assert.DoesNotContain(DiagnosticSecret, line);
    }

    private sealed class CitationSnapshotSession(string ending, CancellationTokenSource stop) : IScenarioSession
    {
        private bool disposed;
        private readonly TimelineFactCitationSnapshot snapshot = new(new([Guid.NewGuid()], 1, [], 0, [], 0, false, false), null, false, null, false, false);
        public TimelineFactCitationSnapshot? CitationDiagnostics { get { Assert.True(disposed); return snapshot; } }
        public IReadOnlyList<CapturedMessage> Messages => [];
        public AgentCostSnapshot? Cost => null;
        public Task<TimelineFactInformationResponse> RunAsync(string question, CancellationToken ct)
        {
            if (ending == "failure") throw new InvalidOperationException(DiagnosticSecret);
            if (ending == "cancel") { stop.Cancel(); ct.ThrowIfCancellationRequested(); }
            return Task.FromResult(new TimelineFactInformationResponse("Returned", [], [], Coverage));
        }
        public ValueTask DisposeAsync()
        {
            disposed = true;
            if (ending == "dispose") throw new InvalidOperationException(DiagnosticSecret);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class OuterFailureSession(Exception? error, AgentFailureDiagnostic? observation = null, bool disposeFailure = false) : IScenarioSession
    {
        public IReadOnlyList<CapturedMessage> Messages => [];
        public AgentCostSnapshot? Cost => null;
        public AgentFailureDiagnostic? AgentFailure => observation;
        public Task<TimelineFactInformationResponse> RunAsync(string question, CancellationToken ct) => error is null
            ? Task.FromResult(new TimelineFactInformationResponse("Returned", [], [], Coverage)) : throw error;
        public ValueTask DisposeAsync() => disposeFailure ? throw new FormatException(DiagnosticSecret) : ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Full_failure_rows_exclude_unknown_exception_messages_Data_and_ToString()
    {
        var original = new PrivateDiagnosticException();
        original.Data[DiagnosticSecret] = DiagnosticSecret;
        var path = Output();
        await EvaluationRunner.RunAsync(Input(path), Scope, path, () => new OuterFailureSession(original));
        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            var row = JsonSerializer.Deserialize<ScenarioOutcome>(line, EvaluationRunner.Json)!;
            Assert.Equal("Other", row.AgentFailure!.Category);
            Assert.DoesNotContain(DiagnosticSecret, line);
        }
        Assert.Equal(DiagnosticSecret, original.Data[DiagnosticSecret]);
    }

    private sealed class PrivateDiagnosticException() : Exception(DiagnosticSecret)
    {
        public override string ToString() => DiagnosticSecret;
    }
    private sealed class DiagnosticAgent(Exception error, bool failInit = false) : ICustomAgent
    {
        public bool Fail { get; set; } = true;
        public event EventHandler<FunctionCallEventArgs>? FunctionCalled { add { } remove { } }
        public void Init(string name, string model, string instructions, List<AITool> tools) { if (failInit) throw error; }
        public Task<AgentResponse> RunAsync(string message) => RunAsync(message, CancellationToken.None);
        public Task<AgentResponse<T>> RunAsync<T>(string message) where T : class => RunAsync<T>(message, CancellationToken.None);
        public Task<AgentResponse> RunAsync(string message, CancellationToken ct)
        {
            if (Fail) throw error;
            return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, "{}")));
        }
        public async Task<AgentResponse<T>> RunAsync<T>(string message, CancellationToken ct) where T : class =>
            new(await RunAsync(message, ct), JsonSerializerOptions.Default);
    }
    private sealed class NormalizingDiagnosticSession : IScenarioSession
    {
        private readonly CapturingAgent capture = new(new DiagnosticAgent(new JsonException(DiagnosticSecret)));
        public IReadOnlyList<CapturedMessage> Messages => capture.Messages;
        public AgentCostSnapshot? Cost => null;
        public AgentFailureDiagnostic? AgentFailure => capture.FailureDiagnostic;
        public async Task<TimelineFactInformationResponse> RunAsync(string question, CancellationToken ct)
        {
            try { await capture.RunAsync<TimelineFactInformationResult>(question, ct); }
            catch (Exception) { throw new TimelineFactInformationSynthesisException(); }
            throw new InvalidOperationException("Synthetic failure expected");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class DiagnosticHttp(int status, string? code, string? body = null) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var response = new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(body ?? JsonSerializer.Serialize(new { error = new
                    { code, message = DiagnosticSecret, type = DiagnosticSecret }, secret = DiagnosticSecret }), Encoding.UTF8, "application/json")
            };
            response.Headers.Add("x-private", DiagnosticSecret);
            return Task.FromResult(response);
        }
    }

    private sealed class FakeBackend(bool authorized) : IEvaluationBackend
    {
        public int Inspections { get; private set; }
        public int Sessions { get; private set; }
        public int Lists { get; private set; }
        public Task<IReadOnlyList<ScopeCandidate>> ListScopesAsync(CancellationToken ct)
        { Lists++; return Task.FromResult<IReadOnlyList<ScopeCandidate>>([]); }
        public Task<EvaluationScope> InspectAsync(EvaluatorCommand command, CancellationToken ct)
        {
            Inspections++;
            if (!authorized) throw new UnauthorizedAccessException("private database failure");
            return Task.FromResult(Scope);
        }
        public IScenarioSession CreateSession(EvaluatorCommand command) { Sessions++; return new FakeSession(); }
    }

    private sealed class ReturnedInvalidAgent : ICustomAgent
    {
        public event EventHandler<FunctionCallEventArgs>? FunctionCalled { add { } remove { } }
        public void Init(string agentName, string model, string instructions, List<AITool> tools) { }
        public Task<AgentResponse> RunAsync(string message) => throw new NotSupportedException();
        public Task<AgentResponse<T>> RunAsync<T>(string message) where T : class => RunAsync<T>(message, CancellationToken.None);
        public Task<AgentResponse<T>> RunAsync<T>(string message, CancellationToken ct) where T : class
        {
            var call = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("1", "search_facts_batch",
                new Dictionary<string, object?> { ["queries"] = JsonSerializer.SerializeToElement(new[] { "Alice" }) })]);
            call.AdditionalProperties = new() { ["private-header"] = "sensitive exception" };
            var tool = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("1", "not public")
                { Exception = new InvalidOperationException("sensitive exception") }]);
            var result = new AgentResponse([call, tool, new ChatMessage(ChatRole.Assistant, "not valid json")]);
            return Task.FromResult(new AgentResponse<T>(result, JsonSerializerOptions.Default));
        }
    }

    private sealed class FakeSession(Exception? error = null, CancellationTokenSource? cancel = null) : IScenarioSession
    {
        public bool Disposed { get; private set; }
        public string? Question { get; private set; }
        public IReadOnlyList<CapturedMessage> Messages => [new("assistant", [new("text", "returned message")])];
        public AgentCostSnapshot? Cost { get; private set; }
        public async Task<TimelineFactInformationResponse> RunAsync(string question, CancellationToken ct)
        {
            Question = question;
            using var cost = AgentCostSession.Start();
            try
            {
                await Task.Yield();
                cancel?.Cancel();
                ct.ThrowIfCancellationRequested();
                if (error is not null) throw error;
                return new("Insufficient evidence", [], [], Coverage);
            }
            finally { cost.Dispose(); Cost = cost.GetSnapshot(); }
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
