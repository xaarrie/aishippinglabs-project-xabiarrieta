using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.EvaluationLabeler;
using ProjectTimeline.SearchFactsJudge;
using Xunit;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace ProjectTimeline.Tests;

[Collection(AgentTelemetryCollection.Name)]
public sealed class SearchResponseJudgeTests
{
    [Theory]
    [InlineData("good")]
    [InlineData("bad")]
    public void Strict_output_accepts_only_the_two_labels(string label)
    {
        var result = SearchResponseJudgment.Parse("{\"reasoning\":\"The answer matches the captured source.\",\"label\":\"" + label + "\"}");
        Assert.Equal(label, result.Label);
        Assert.Equal("The answer matches the captured source.", result.Reasoning);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"reasoning\":\"\",\"label\":\"good\"}")]
    [InlineData("{\"reasoning\":\" \\n\\t\",\"label\":\"bad\"}")]
    [InlineData("{\"reasoning\":\"Grounded\",\"label\":\"Good\"}")]
    [InlineData("{\"reasoning\":\"Grounded\",\"label\":\"unknown\"}")]
    [InlineData("{\"reasoning\":\"Grounded\",\"label\":\"bad\",\"extra\":true}")]
    [InlineData("{\"reasoning\":\"First\",\"reasoning\":\"Second\",\"label\":\"bad\"}")]
    [InlineData("{\"reasoning\":null,\"label\":\"bad\"}")]
    [InlineData("{\"reasoning\":\"Grounded\",\"label\":null}")]
    [InlineData("{\"reasoning\":\"Grounded\"}")]
    [InlineData("{\"reasoning\":\"Grounded\",\"label\":\"bad\",\"label\":\"good\"}")]
    [InlineData("{\"reasoning\":\"Grounded\",\"label\":1}")]
    [InlineData("{\"Reasoning\":\"Grounded\",\"label\":\"bad\"}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("not JSON")]
    [InlineData("```json\n{\"reasoning\":\"Grounded\",\"label\":\"bad\"}\n```")]
    public void Strict_output_rejects_invalid_contracts(string json)
    {
        Assert.Equal("ResponseSchema", Assert.Throws<SearchJudgeException>(() => SearchResponseJudgment.Parse(json)).Code);
    }

    [Fact]
    public void Strict_output_rejects_oversized_reasoning_and_payloads()
    {
        Assert.Equal("ResponseSchema", Assert.Throws<SearchJudgeException>(() => SearchResponseJudgment.Parse(
            JsonSerializer.Serialize(new { reasoning = new string('x', 2001), label = "good" }))).Code);
        Assert.Equal("ResponseLimit", Assert.Throws<SearchJudgeException>(() => SearchResponseJudgment.Parse(new string('x', 16385))).Code);
    }

    private const string Valid = "{\"reasoning\":\"The answer matches the captured source.\",\"label\":\"good\"}";
    private const string Sentinel = "HUMAN_METADATA_MUST_NOT_ENTER_MODEL";
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string RealStem = "C:/Data/formacion/TimelineSpecifications/search-facts-evaluation-citation-allowlists-20261102T062417Z-f35d9311";
    private const string RealHash = "e136e2f53cd7157e791a0a6ae9baf99daa2b751b92433cc49ff9ae99dcb2d2f2";
    private static readonly string[] Header = ["version", "datasetSha256", "runId", "scenarioId", "ordinal", "question",
        "label", "failureCategory", "revision", "updatedUtc"];

    private sealed class Chat(Func<CancellationToken, Task<ChatResponse>> respond) : IChatClient
    {
        public List<ChatMessage[]> Requests { get; } = [];
        public ChatOptions? Options;
        public CancellationToken Token;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToArray()); Options = options; Token = cancellationToken;
            return respond(cancellationToken);
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static ChatResponse Response(string text = Valid) => new(new ChatMessage(ChatRole.Assistant, text))
        { FinishReason = ChatFinishReason.Stop };
    private static Chat Ready(string text = Valid) => new(_ => Task.FromResult(Response(text)));
    private static EvaluationRecord Record(string question = "What is specified?", string run = "run", string scenario = "scenario", bool missingAnswer = false)
    {
        var raw = JsonSerializer.SerializeToElement(new
        {
            runId = run, scenarioId = scenario, outcome = "Returned", expectedAnswer = Sentinel, label = Sentinel,
            scenario = new { ordinal = 1, question, category = Sentinel, type = Sentinel, evaluationComments = Sentinel,
                fields = new[] { new { header = "expectedAnswer", value = Sentinel } } },
            scope = new { role = Sentinel },
            response = missingAnswer ? null : new
            {
                content = "ACTUAL_ANSWER", failureCategory = Sentinel,
                facts = new[] { new { id = "fact-id", claim = "SOURCE_CLAIM", sourceQuote = "SOURCE_QUOTE",
                    clarificationEvidence = "SOURCE_DOMAIN_CLARIFICATION", label = Sentinel } },
                citations = new[] { new { factId = "fact-id", expectedAnswer = Sentinel } },
                sourceContexts = new[] { new { evidenceId = "context-id", text = "SOURCE_CONTEXT", category = Sentinel } },
                research = new { status = "Partial", evidenceTruncated = true, evaluationComments = Sentinel },
                coverage = new { truncated = true, warnings = new[] { "EVIDENCE_LIMIT" }, category = Sentinel }
            },
            messages = new object[]
            {
                new { role = "system", contents = new[] { new { kind = "text", text = Sentinel } } },
                new { role = "assistant", contents = new[] { new { kind = "text", text = Sentinel } } },
                new { role = "assistant", contents = new[] { new { kind = "functionCall", callId = "call-1", name = "search_facts_batch",
                    payload = new { queries = new[] { "SEARCH_QUERY" }, expectedAnswer = Sentinel } } } },
                new { role = "tool", contents = new[] { new { kind = "functionResult", callId = "call-1", failed = false,
                    payload = new { Status = "Ok", Results = new[] { new { Query = "SEARCH_QUERY", Facts = new[] {
                        new { Id = "tool-fact", Claim = "TOOL_SOURCE_CLAIM", ClarificationEvidence = "TOOL_CLARIFICATION", label = Sentinel } },
                        category = Sentinel } }, expectedAnswer = Sentinel } } } }
            },
            cost = new { note = Sentinel }, error = Sentinel, citationDiagnostics = new { comment = Sentinel }
        });
        return new(0, run, scenario, 1, question, raw);
    }

    private static Dataset DatasetFor(EvaluationRecord record) => new("fixture.jsonl", Hash, [record]);
    private static string[] Cells(EvaluationRecord record) => ["1", Hash, "'" + record.RunId, "'" + record.ScenarioId,
        "1", "'" + record.Question, "Bad", "hallucination", "1", "2026-11-02T21:00:00.0000000+00:00"];
    private static string CsvRow(IEnumerable<string> cells) => string.Join(",", cells.Select(s => "\"" + s.Replace("\"", "\"\"") + "\"")) + "\r\n";
    private static string CsvText(params string[][] rows) => CsvRow(Header) + string.Concat(rows.Select(CsvRow));
    private static SearchJudgeInput Input() => SearchJudgeInput.FromRecord(Record());

    [Fact]
    public async Task Structured_request_is_strict_and_each_call_is_isolated()
    {
        var chat = Ready();
        using var judge = new SearchResponseJudge(chat);
        Assert.Equal("gpt-4o-mini", judge.Model);
        Assert.Equal("good", (await judge.JudgeAsync(Input())).Label);
        Assert.Equal("good", (await judge.JudgeAsync(Input())).Label);
        Assert.Equal(2, chat.Requests.Count);
        Assert.All(chat.Requests, messages =>
        {
            var message = Assert.Single(messages);
            Assert.Equal(ChatRole.User, message.Role);
            Assert.DoesNotContain(Sentinel, message.Text);
        });
        var options = Assert.IsType<ChatOptions>(chat.Options);
        // The framework carries the system prompt in ChatOptions.Instructions, not Messages.
        Assert.Contains("calibration", options.Instructions);
        Assert.Contains("specification or documentation questions", options.Instructions);
        Assert.Contains("state-changing action", options.Instructions);
        Assert.Contains("exact entity meaning, fields, and direction of relationships", options.Instructions);
        Assert.Contains("Observed instances never establish universal requirements", options.Instructions);
        Assert.Contains("relevance and entailment", options.Instructions);
        Assert.Contains("Citation membership alone does not establish semantic support", options.Instructions);
        Assert.Contains("untrusted DATA", options.Instructions);
        Assert.IsType<ChatResponseFormatJson>(options.ResponseFormat);
        Assert.True(options.Tools is null || options.Tools.Count == 0);
        Assert.InRange(options.MaxOutputTokens!.Value, 1, 1000);
        var raw = Assert.IsType<OpenAI.Chat.ChatCompletionOptions>(options.RawRepresentationFactory!(null!));
        using var doc = JsonDocument.Parse(System.ClientModel.Primitives.ModelReaderWriter.Write(raw.ResponseFormat).ToString());
        var contract = doc.RootElement.GetProperty("json_schema");
        Assert.True(contract.GetProperty("strict").GetBoolean());
        var schema = contract.GetProperty("schema");
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(new[] { "reasoning", "label" }, schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(new[] { "good", "bad" }, schema.GetProperty("properties").GetProperty("label").GetProperty("enum").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(2, schema.GetProperty("properties").EnumerateObject().Count());
        Assert.True(chat.Token.CanBeCanceled);
    }

    [Theory]
    [InlineData(@"\bUser(?:Roles?)?\b")]
    [InlineData(@"\bAdmin\b")]
    [InlineData(@"(?i)\btimelines?\b")]
    [InlineData(@"(?i)\bentry previews\b")]
    [InlineData(@"(?i)\bdocument formats\b")]
    [InlineData(@"(?i)\bnavigation\b")]
    public async Task System_rubric_excludes_answer_specific_entities_and_workflows(string pattern)
    {
        var chat = Ready();
        using var judge = new SearchResponseJudge(chat);
        await judge.JudgeAsync(Input());
        Assert.DoesNotMatch(pattern, chat.Options!.Instructions!);
    }

    private sealed class OfflineWire : HttpMessageHandler
    {
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // No header capture and no network: only the request body is inspected.
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { id = "offline", model = "gpt-4o-mini", created = 0,
                    choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = Valid } } } }),
                    Encoding.UTF8, "application/json")
            };
        }
    }

    [Fact]
    public async Task Bounded_OpenAI_factory_sends_the_strict_schema_and_safe_input_over_offline_transport()
    {
        var wire = new OfflineWire();
        using var transport = new HttpClient(wire);
        using var judge = SearchResponseJudge.CreateOpenAI("offline-placeholder", transport: transport);
        Assert.Equal("good", (await judge.JudgeAsync(Input())).Label);
        using var body = JsonDocument.Parse(wire.Body!);
        Assert.Equal("gpt-4o-mini", body.RootElement.GetProperty("model").GetString());
        Assert.True(body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.Contains("calibration", wire.Body);
        Assert.Contains("SOURCE_DOMAIN_CLARIFICATION", wire.Body);
        Assert.DoesNotContain(Sentinel, wire.Body);
    }

    [Theory]
    [InlineData("invalid-json", "ResponseSchema")]
    [InlineData("refusal", "ResponseRefused")]
    [InlineData("truncated", "ResponseTruncated")]
    [InlineData("filtered", "ResponseRefused")]
    [InlineData("tool", "ResponseEnvelope")]
    [InlineData("multiple", "ResponseEnvelope")]
    [InlineData("non-assistant", "ResponseEnvelope")]
    [InlineData("missing-finish", "ResponseEnvelope")]
    public async Task Runtime_admission_rejects_bad_envelopes_and_never_forces_bad(string mutation, string code)
    {
        var response = Response(mutation == "invalid-json" ? Sentinel : Valid);
        if (mutation == "truncated") response.FinishReason = ChatFinishReason.Length;
        if (mutation == "filtered") response.FinishReason = ChatFinishReason.ContentFilter;
        if (mutation == "missing-finish") response.FinishReason = null;
        if (mutation == "multiple") response.Messages.Add(new ChatMessage(ChatRole.Assistant, Valid));
        if (mutation == "non-assistant") response.Messages[0].Role = ChatRole.User;
        if (mutation == "tool") response.Messages[0].Contents.Add(new FunctionCallContent("call", "unexpected"));
        if (mutation == "refusal") response.RawRepresentation = System.ClientModel.Primitives.ModelReaderWriter.Read<OpenAI.Chat.ChatCompletion>(
            BinaryData.FromString(JsonSerializer.Serialize(new { id = "offline", model = "gpt-4o-mini", created = 0,
                choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = Valid, refusal = Sentinel } } } })));
        var chat = new Chat(_ => Task.FromResult(response));
        using var judge = new SearchResponseJudge(chat);
        var error = await Assert.ThrowsAsync<SearchJudgeException>(() => judge.JudgeAsync(Input()));
        Assert.Equal(code, error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Sentinel, error.ToString());
        Assert.Single(chat.Requests);
    }

    [Fact]
    public async Task Missing_answer_is_an_error_without_model_execution()
    {
        var chat = Ready();
        using var judge = new SearchResponseJudge(chat);
        var error = await Assert.ThrowsAsync<SearchJudgeException>(() => judge.JudgeAsync(SearchJudgeInput.FromRecord(Record(missingAnswer: true))));
        Assert.Equal("NoAnswer", error.Code);
        Assert.Empty(chat.Requests);
    }

    [Fact]
    public async Task Cancellation_reaches_transport_and_timeout_bounds_even_uncooperative_clients()
    {
        using var cancellation = new CancellationTokenSource();
        var chat = new Chat(ct => { cancellation.Cancel(); return Task.FromCanceled<ChatResponse>(ct); });
        using var judge = new SearchResponseJudge(chat);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => judge.JudgeAsync(Input(), cancellation.Token));
        Assert.True(chat.Token.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => judge.JudgeAsync(Input(), cancellation.Token));
        Assert.Single(chat.Requests);
        var never = new TaskCompletionSource<ChatResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ignoring = new Chat(_ => never.Task);
        using var bounded = new SearchResponseJudge(ignoring, timeout: TimeSpan.FromMilliseconds(30));
        var error = await Assert.ThrowsAsync<SearchJudgeException>(() => bounded.JudgeAsync(Input()));
        Assert.Equal("Timeout", error.Code);
        Assert.True(ignoring.Token.IsCancellationRequested);
        never.SetResult(Response());
        Assert.Throws<ArgumentOutOfRangeException>(() => new SearchResponseJudge(Ready(), timeout: Timeout.InfiniteTimeSpan));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Content_telemetry_is_disabled_and_provider_failures_are_sanitized(bool fail)
    {
        var telemetry = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == AgentTelemetry.NativeSourceName || s.Name == AgentTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => telemetry.Add(string.Join(";", activity.TagObjects.Select(t => t.Value?.ToString())
                .Concat(activity.Events.SelectMany(e => e.Tags.Select(t => t.Value?.ToString())))))
        };
        ActivitySource.AddActivityListener(listener);
        var chat = new Chat(_ => fail ? Task.FromException<ChatResponse>(new InvalidOperationException("ACTUAL_ANSWER " + Sentinel))
            : Task.FromResult(Response()));
        using var judge = new SearchResponseJudge(chat);
        if (fail)
        {
            var error = await Assert.ThrowsAsync<SearchJudgeException>(() => judge.JudgeAsync(Input()));
            Assert.Equal("ProviderFailure", error.Code);
            Assert.DoesNotContain(Sentinel, error.ToString());
        }
        else await judge.JudgeAsync(Input());
        Assert.NotEmpty(telemetry);
        var all = string.Join("\n", telemetry);
        Assert.DoesNotContain("ACTUAL_ANSWER", all);
        Assert.DoesNotContain("SOURCE_QUOTE", all);
        Assert.DoesNotContain(Sentinel, all);
    }

    [Fact]
    public void Join_keeps_annotation_metadata_separate_and_preserves_only_legitimate_evidence()
    {
        var record = Record();
        var cells = Cells(record);
        cells[8] = "987654321";
        var dataset = ReadOnlyLabeledDataset.Join(DatasetFor(record), CsvText(cells), Hash);
        var row = Assert.Single(dataset.Rows);
        Assert.Equal("Bad", row.Human.Label);
        Assert.Equal("hallucination", row.Human.FailureCategory);
        Assert.Equal(987654321, row.Human.Revision);
        Assert.Equal(new SearchResponseIdentity(Hash, "run", "scenario", 1), row.Identity);
        var safe = row.Input.ToJson();
        foreach (var excluded in new[] { Sentinel, "hallucination", "987654321", "updatedUtc", "scenario", "expectedAnswer", "failureCategory" })
            Assert.DoesNotContain(excluded, safe);
        foreach (var retained in new[] { "ACTUAL_ANSWER", "SOURCE_CLAIM", "SOURCE_QUOTE", "SOURCE_CONTEXT",
            "SOURCE_DOMAIN_CLARIFICATION", "TOOL_SOURCE_CLAIM", "TOOL_CLARIFICATION", "SEARCH_QUERY", "EVIDENCE_LIMIT", "Partial" })
            Assert.Contains(retained, safe);
    }

    [Theory]
    [InlineData("ordinary", "normal question", false)]
    [InlineData("'leading", "''Two apostrophes, \"quoted\"\r\nsecond line", true)]
    [InlineData("=formula", "  @question, with comma\nnext line", true)]
    public void Csv_round_trips_quoted_multiline_and_reversible_spreadsheet_metadata(string run, string question, bool escaped)
    {
        var record = Record(question, run, scenario: "'scenario");
        var cells = Cells(record);
        if (!escaped) { cells[2] = record.RunId; cells[5] = record.Question; }
        var row = Assert.Single(ReadOnlyLabeledDataset.Join(DatasetFor(record), CsvText(cells)).Rows);
        Assert.Equal(run, row.Identity.RunId);
        Assert.Equal("'scenario", row.Identity.ScenarioId);
        Assert.Equal(question, row.Input.Question);
    }

    [Theory]
    [InlineData(1, "other-hash", "csv-dataset")]
    [InlineData(2, "other-run", "csv-identity")]
    [InlineData(3, "other-scenario", "csv-identity")]
    [InlineData(4, "2", "csv-record")]
    [InlineData(5, "other-question", "csv-record")]
    [InlineData(6, "Good", "csv-label-category")]
    [InlineData(7, "unknown", "csv-label-category")]
    [InlineData(8, "0", "csv-revision")]
    [InlineData(9, "not-a-time", "csv-time")]
    [InlineData(0, "2", "csv-schema")]
    public void Join_rejects_hash_identity_record_and_annotation_mismatches(int column, string value, string code)
    {
        var record = Record();
        var cells = Cells(record); cells[column] = value;
        Assert.Equal(code, Assert.Throws<LabelerException>(() => ReadOnlyLabeledDataset.Join(DatasetFor(record), CsvText(cells))).Code);
    }

    [Fact]
    public void Join_rejects_duplicates_unescaped_formulas_apostrophe_damage_and_wrong_pinned_hash()
    {
        var record = Record("'original", "=run");
        var data = DatasetFor(record);
        var cells = Cells(record);
        Assert.Equal("csv-duplicate-identity", Assert.Throws<LabelerException>(() => ReadOnlyLabeledDataset.Join(data, CsvText(cells, cells))).Code);
        Assert.Equal("input-duplicate-identity", Assert.Throws<LabelerException>(() => ReadOnlyLabeledDataset.Join(data with { Records = [record, record] }, CsvText(cells))).Code);
        Assert.Equal("csv-dataset", Assert.Throws<LabelerException>(() => ReadOnlyLabeledDataset.Join(data, CsvText(cells), "wrong-hash")).Code);
        cells[2] = "=run";
        Assert.Equal("csv-metadata", Assert.Throws<LabelerException>(() => ReadOnlyLabeledDataset.Join(data, CsvText(cells))).Code);
        cells = Cells(record); cells[5] = record.Question;
        Assert.Equal("csv-record", Assert.Throws<LabelerException>(() => ReadOnlyLabeledDataset.Join(data, CsvText(cells))).Code);
        Assert.Empty(ReadOnlyLabeledDataset.Join(data, CsvText()).Rows);
    }

    // Portable suites skip this read-only integration check when the user's local corpus is absent.
    private sealed class LocalCorpusFactAttribute : FactAttribute
    {
        public LocalCorpusFactAttribute()
        {
            if (!File.Exists(RealStem + ".jsonl") || !File.Exists(RealStem + ".labels.csv")) Skip = "Local calibration corpus unavailable.";
        }
    }

    [LocalCorpusFact]
    public void Real_labeler_corpus_loads_50_responses_and_17_real_annotations_without_writes()
    {
        var source = File.ReadAllBytes(RealStem + ".jsonl");
        var labels = File.ReadAllBytes(RealStem + ".labels.csv");
        var lockExisted = File.Exists(RealStem + ".labels.csv.lock");
        var data = ReadOnlyLabeledDataset.Load(RealStem + ".jsonl", RealStem + ".labels.csv", RealHash);
        Assert.Equal(RealHash, data.DatasetSha256);
        Assert.Equal(50, data.SourceRowCount);
        Assert.Equal(17, data.Rows.Length);
        Assert.Equal(12, data.Rows.Count(r => r.Human.Label == "Good"));
        Assert.Equal(5, data.Rows.Count(r => r.Human.Label == "Bad"));
        Assert.Equal(17, data.Rows.Select(r => (r.Identity.RunId, r.Identity.ScenarioId)).Distinct().Count());
        Assert.Equal(source, File.ReadAllBytes(RealStem + ".jsonl"));
        Assert.Equal(labels, File.ReadAllBytes(RealStem + ".labels.csv"));
        Assert.Equal(lockExisted, File.Exists(RealStem + ".labels.csv.lock"));
    }
}
