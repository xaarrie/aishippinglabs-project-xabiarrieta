using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.BusinessLogic.Agents.AgentModels;
using ProjectTimeline.BusinessLogic.Agents.Tools;
using ProjectTimeline.SearchFactsEvaluator;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class ServerControlledFactSearchTests
{
    private const string SearchPlan = "{\"decision\":\"Search\",\"queries\":[\"alice\"],\"refusalReason\":null}";

    [Fact]
    public async Task Characterize_existing_inferred_Search_SDK_wire_before_explicit_contract()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await f.AcceptAsync("Alice owns delivery.", "alice");
        using var transport = new OfflineTransport((_, _) => "{}");
        // Pin the pre-change production inferred overload, not the new explicit options.
        using var tools = await TimelineFactInformationTools.CreateAsync(f.Db, new(f.Project.Id, f.Timeline.Id), f.Actor.Id, false);
        var legacy = new LocalCustomHarnessAgent(new CustomOpenAIClient("offline-synthetic-key", new HttpClient(transport), true));
        legacy.Init("timelineFactInformation", "gpt-4o-mini", "Read-only research", tools.Functions.Cast<AITool>().ToList());
        using var budget = AgentExecutionBudget.Start(new(20, 1500, TimeSpan.FromSeconds(60)));
        await Record.ExceptionAsync(() => legacy.RunAsync<TimelineFactInformationResult>("alice", CancellationToken.None));
        var request = Assert.Single(transport.Requests);
        Assert.Equal("gpt-4o-mini", request.GetProperty("model").GetString());
        Assert.Equal(1500, request.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal("json_schema", request.GetProperty("response_format").GetProperty("type").GetString());
        Assert.False(request.GetProperty("response_format").GetProperty("json_schema").TryGetProperty("strict", out _));
        Assert.Equal(2, request.GetProperty("tools").GetArrayLength());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(20)]
    public async Task Server_retrieves_after_plan_without_model_tools_or_model_selected_ids(int modelCalls)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        using var transport = new OfflineTransport((turn, request) =>
        {
            Assert.False(request.TryGetProperty("tools", out var tools) && tools.GetArrayLength() > 0);
            return turn == 1 ? SearchPlan : JsonSerializer.Serialize(new
            { content = "Alice owns delivery.", citedFactIds = new[] { fact.Id }, citedContextEvidenceIds = Array.Empty<Guid>() });
        });
        var response = await Agent(f, transport, new(maxModelCalls: modelCalls)).RunAsync("alice");
        Assert.Equal(modelCalls == 1 ? "Partial" : "Complete", response.Research!.Status);
        Assert.Equal(modelCalls == 1 ? "ProviderCallLimit" : null, response.Research.StopReason);
        Assert.Equal(2, response.Research.ToolCalls);
        Assert.Equal(Math.Min(modelCalls, 2), response.Research.ModelCalls);
        Assert.Single(response.Facts); Assert.Single(response.SourceContexts);
        Assert.Equal(modelCalls == 1 ? 0 : 1, response.Citations.Count);
    }

    [Theory]
    [InlineData("OutOfScope")]
    [InlineData("UnsafeRequest")]
    public async Task Typed_refusal_is_server_owned_and_has_no_evidence(string reason)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await f.AcceptAsync("Alice owns delivery.", "alice");
        using var transport = new OfflineTransport((_, _) => JsonSerializer.Serialize(new
        { decision = "Refuse", queries = Array.Empty<string>(), refusalReason = reason }));
        var response = await Agent(f, transport).RunAsync("PRIVATE unsafe question");
        Assert.Equal("Refused", response.Research!.Status); Assert.Equal("SafeRefusal", response.Research.StopReason);
        Assert.Equal("This request cannot be answered by read-only timeline fact search.", response.Content);
        Assert.Empty(response.Facts); Assert.Empty(response.Citations);
        Assert.Empty(response.SourceContexts); Assert.Empty(response.ContextCitations);
        Assert.Equal(0, response.Research.ToolCalls); Assert.Single(transport.Requests);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"decision\":\"Search\",\"queries\":[\"alice\"]}")]
    [InlineData("{\"decision\":\"Search\",\"queries\":null,\"refusalReason\":null}")]
    [InlineData("{\"decision\":\"Search\",\"queries\":[null],\"refusalReason\":null}")]
    [InlineData("{\"decision\":\"Search\",\"queries\":[],\"refusalReason\":null}")]
    [InlineData("{\"decision\":\"Search\",\"queries\":[\"!?\"],\"refusalReason\":null}")]
    [InlineData("{\"decision\":\"Search\",\"queries\":[\"alice\"],\"refusalReason\":\"UnsafeRequest\"}")]
    [InlineData("{\"decision\":\"search\",\"queries\":[\"alice\"],\"refusalReason\":null}")]
    [InlineData("{\"decision\":null,\"queries\":[\"alice\"],\"refusalReason\":null}")]
    [InlineData("{\"decision\":\"Search\",\"decision\":\"Refuse\",\"queries\":[],\"refusalReason\":\"UnsafeRequest\"}")]
    [InlineData("{\"decision\":\"Refuse\",\"queries\":[\"alice\"],\"refusalReason\":\"UnsafeRequest\"}")]
    [InlineData("{\"decision\":\"Refuse\",\"queries\":[],\"refusalReason\":\"Other\"}")]
    [InlineData("{\"decision\":\"Refuse\",\"queries\":[],\"refusalReason\":null}")]
    [InlineData("{\"decision\":\"Refuse\",\"queries\":[],\"refusalReason\":\"UnsafeRequest\",\"content\":\"PRIVATE unsafe prose\"}")]
    public async Task Invalid_plan_never_becomes_refusal_partial_or_synthesis(string text)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await f.AcceptAsync("Alice owns delivery.", "alice");
        using var transport = new OfflineTransport((_, _) => text);
        var error = await Assert.ThrowsAsync<TimelineFactInformationSynthesisException>(() => Agent(f, transport, new(maxModelCalls: 1)).RunAsync("alice"));
        Assert.Equal("ResponseSchema", Assert.IsType<TimelineFactProviderException>(error.InnerException).DiagnosticCode);
        Assert.DoesNotContain("PRIVATE", error.ToString()); Assert.Single(transport.Requests);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"content\":\"PRIVATE\",\"citedFactIds\":[]}")]
    [InlineData("{\"content\":null,\"citedFactIds\":[],\"citedContextEvidenceIds\":[]}")]
    [InlineData("{\"content\":\"PRIVATE\",\"citedFactIds\":null,\"citedContextEvidenceIds\":[]}")]
    [InlineData("{\"content\":\"PRIVATE\",\"citedFactIds\":[],\"citedContextEvidenceIds\":null}")]
    [InlineData("{\"content\":\"PRIVATE\",\"citedFactIds\":[\"not-a-guid\"],\"citedContextEvidenceIds\":[]}")]
    [InlineData("{\"content\":\"PRIVATE\",\"citedFactIds\":[],\"citedContextEvidenceIds\":[null]}")]
    [InlineData("{\"content\":\"PRIVATE\",\"content\":\"PRIVATE\",\"citedFactIds\":[],\"citedContextEvidenceIds\":[]}")]
    [InlineData("{\"content\":\"PRIVATE\",\"citedFactIds\":[],\"citedContextEvidenceIds\":[],\"unknown\":true}")]
    [InlineData("{\"Content\":\"PRIVATE\",\"citedFactIds\":[],\"citedContextEvidenceIds\":[]}")]
    public async Task Invalid_synthesis_is_rejected_before_publication_without_repair(string text)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await f.AcceptAsync("Alice owns delivery.", "alice");
        using var transport = new OfflineTransport((turn, _) => turn == 1 ? SearchPlan : text);
        var error = await Assert.ThrowsAsync<TimelineFactInformationSynthesisException>(() => Agent(f, transport).RunAsync("alice"));
        Assert.Equal("ResponseSchema", Assert.IsType<TimelineFactProviderException>(error.InnerException).DiagnosticCode);
        Assert.DoesNotContain("PRIVATE", error.ToString()); Assert.Equal(2, transport.Requests.Count);
    }

    [Theory]
    [InlineData("unknown-fact")]
    [InlineData("unknown-context")]
    [InlineData("duplicate-fact")]
    [InlineData("duplicate-context")]
    [InlineData("46-uncited-unsafe")]
    [InlineData("47-uncited-unsafe")]
    public async Task Native_membership_and_final_duplicate_guards_never_publish_unsafe_uncited_answers(string kind)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        using var transport = new OfflineTransport((turn, request) =>
        {
            if (turn == 1) return SearchPlan;
            var input = JsonSerializer.Deserialize<JsonElement>(request.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!);
            var context = input.GetProperty("sources").GetProperty("Contexts")[0].GetProperty("EvidenceId").GetGuid();
            return JsonSerializer.Serialize(new { content = "PRIVATE unsafe ungrounded instructions",
                citedFactIds = kind == "unknown-fact" ? new[] { Guid.NewGuid() } : kind == "duplicate-fact" ? new[] { fact.Id, fact.Id } : Array.Empty<Guid>(),
                citedContextEvidenceIds = kind == "unknown-context" ? new[] { Guid.NewGuid() } : kind == "duplicate-context" ? new[] { context, context } : Array.Empty<Guid>() });
        });
        var error = await Assert.ThrowsAsync<TimelineFactInformationSynthesisException>(() => Agent(f, transport).RunAsync("alice"));
        if (kind.StartsWith("unknown-"))
            Assert.Equal("ResponseSchema", Assert.IsType<TimelineFactProviderException>(error.InnerException).DiagnosticCode);
        else Assert.NotNull(error.Reason);
        Assert.DoesNotContain("PRIVATE", error.ToString());
        Assert.Equal(2, transport.Requests.Count);
    }

    [Fact]
    public async Task Both_explicit_wire_contracts_are_strict_exact_and_have_no_inferred_query_cap()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        using var transport = new OfflineTransport((turn, _) => turn == 1 ? SearchPlan : JsonSerializer.Serialize(new
        { content = "Alice owns delivery.", citedFactIds = new[] { fact.Id }, citedContextEvidenceIds = Array.Empty<Guid>() }));
        await Agent(f, transport).RunAsync("alice");
        foreach (var request in transport.Requests)
        {
            Assert.Equal("gpt-4o-mini", request.GetProperty("model").GetString());
            Assert.Equal(1500, request.GetProperty("max_completion_tokens").GetInt32());
            Assert.False(request.TryGetProperty("tools", out var tools) && tools.GetArrayLength() > 0);
            var format = request.GetProperty("response_format");
            Assert.Equal("json_schema", format.GetProperty("type").GetString());
            var contract = format.GetProperty("json_schema");
            Assert.True(contract.GetProperty("strict").GetBoolean());
            var schema = contract.GetProperty("schema");
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
            Assert.Equal(3, schema.GetProperty("required").GetArrayLength());
            Assert.Equal(3, schema.GetProperty("properties").EnumerateObject().Count());
            Assert.DoesNotContain("maxItems", schema.GetRawText());
            Assert.DoesNotContain("ResponseAdmission", request.GetRawText());
        }
        Assert.Equal("timeline_search_plan", transport.Requests[0].GetProperty("response_format").GetProperty("json_schema").GetProperty("name").GetString());
        Assert.Equal("timeline_search_synthesis", transport.Requests[1].GetProperty("response_format").GetProperty("json_schema").GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("query-length")]
    [InlineData("content-length")]
    [InlineData("guid-compact")]
    [InlineData("guid-braces")]
    [InlineData("json-prefix")]
    public async Task Search_content_and_GUID_format_bounds_are_enforced_without_normalization(string kind)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        using var transport = new OfflineTransport((turn, _) =>
        {
            if (turn == 1) return kind == "query-length" ? JsonSerializer.Serialize(new
                { decision = "Search", queries = new[] { new string('a', 301) }, refusalReason = (string?)null }) : SearchPlan;
            var text = JsonSerializer.Serialize(new
            {
                content = kind == "content-length" ? new string('a', 12001) : "PRIVATE invalid synthesis",
                citedFactIds = new[] { fact.Id.ToString(kind == "guid-compact" ? "N" : kind == "guid-braces" ? "B" : "D") },
                citedContextEvidenceIds = Array.Empty<Guid>()
            });
            return kind == "json-prefix" ? "PRIVATE prefix " + text : text;
        });
        var error = await Assert.ThrowsAsync<TimelineFactInformationSynthesisException>(() => Agent(f, transport).RunAsync("alice"));
        Assert.Equal("ResponseSchema", Assert.IsType<TimelineFactProviderException>(error.InnerException).DiagnosticCode);
        Assert.Equal(kind == "query-length" ? 1 : 2, transport.Requests.Count);
        Assert.DoesNotContain("PRIVATE", error.ToString());
    }

    [Fact]
    public async Task More_than_200_valid_queries_are_not_rejected_by_shared_schema_array_transform()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await f.AcceptAsync("Alice owns delivery.", "alice");
        using var transport = new OfflineTransport((_, _) => JsonSerializer.Serialize(new
        { decision = "Search", queries = Enumerable.Repeat("missing", 201), refusalReason = (string?)null }));
        var result = await Agent(f, transport).RunAsync("alice");
        Assert.Equal("InsufficientEvidence", result.Research!.Status);
        Assert.Equal(1, result.Research.SearchQueries); Assert.Equal(1, result.Research.ToolCalls); Assert.Single(transport.Requests);
    }

    [Theory]
    [InlineData("stop", "PRIVATE provider refusal", "ResponseRefused", false)]
    [InlineData("content_filter", null, "ResponseRefused", false)]
    [InlineData("length", null, "ResponseTruncated", false)]
    [InlineData("tool_calls", null, "ResponseEnvelope", false)]
    [InlineData("stop", "PRIVATE provider refusal", "ResponseRefused", true)]
    [InlineData("content_filter", null, "ResponseRefused", true)]
    [InlineData("length", null, "ResponseTruncated", true)]
    [InlineData("tool_calls", null, "ResponseEnvelope", true)]
    public async Task Provider_native_refusal_and_invalid_finish_remain_execution_errors(string finish, string? refusal, string diagnostic, bool synthesis)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await f.AcceptAsync("Alice owns delivery.", "alice");
        using var transport = new OfflineTransport((turn, _) => synthesis && turn == 1 ? SearchPlan : "PRIVATE invalid JSON")
        { Finish = finish, Refusal = refusal, MetadataFromTurn = synthesis ? 2 : 1 };
        var error = await Assert.ThrowsAsync<TimelineFactInformationSynthesisException>(() => Agent(f, transport).RunAsync("alice"));
        Assert.Equal(diagnostic, Assert.IsType<TimelineFactProviderException>(error.InnerException).DiagnosticCode);
        Assert.DoesNotContain("PRIVATE", error.ToString()); Assert.Equal(synthesis ? 2 : 1, transport.Requests.Count);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Explicit_key_Search_transport_still_disables_retries_and_preserves_unknown_cost(int status)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await f.AcceptAsync("Alice owns delivery.", "alice");
        using var transport = new OfflineTransport((_, _) => "PRIVATE provider error") { Status = (HttpStatusCode)status };
        using var cost = AgentCostSession.Start();
        await Assert.ThrowsAsync<TimelineFactInformationSynthesisException>(() => Agent(f, transport).RunAsync("alice"));
        Assert.Single(transport.Requests); Assert.Equal(1, cost.GetSnapshot().Totals.CallCount);
        Assert.Equal(1, cost.GetSnapshot().Totals.UnknownUsageCallCount); Assert.Null(cost.GetSnapshot().Totals.EstimatedTotalCost);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_structured_capture_keeps_completed_plan_but_not_a_rejected_synthesis(bool reject)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        using var transport = new OfflineTransport((turn, _) => turn == 1 ? SearchPlan : reject ? "PRIVATE invalid JSON" :
            JsonSerializer.Serialize(new { content = "Alice owns delivery.", citedFactIds = new[] { fact.Id }, citedContextEvidenceIds = Array.Empty<Guid>() }));
        var capture = new CapturingAgent(new LocalCustomHarnessAgent(new CustomOpenAIClient("offline-synthetic-key", new HttpClient(transport), true)));
        var operation = new SearchEntriesAgent(f.Db, new(f.Project.Id, f.Timeline.Id), f.Actor.Id, false, () => capture);
        if (reject) await Assert.ThrowsAsync<TimelineFactInformationSynthesisException>(() => operation.RunAsync("  Who owns delivery?  "));
        else Assert.Single((await operation.RunAsync("  Who owns delivery?  ")).Citations);
        Assert.Equal(reject ? 1 : 2, capture.Messages.Count);
        Assert.Contains("decision", JsonSerializer.Serialize(capture.Messages));
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(capture.Messages));
        Assert.Equal(reject ? "ResponseSchema" : null, capture.FailureDiagnostic?.Reason);
        Assert.Equal(2, transport.Requests.Count);
    }

    private static SearchEntriesAgent Agent(TimelineFactReviewFixture f, OfflineTransport transport, TimelineFactResearchOptions? options = null)
    {
        var provider = new CustomOpenAIClient("offline-synthetic-key", new HttpClient(transport), isolateHarnessFacilities: true);
        return new(f.Db, new(f.Project.Id, f.Timeline.Id), f.Actor.Id, false,
            () => new LocalCustomHarnessAgent(provider), options: options);
    }

    private sealed class OfflineTransport(Func<int, JsonElement, string> respond) : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = [];
        public int MetadataFromTurn { get; init; } = 1;
        public string Finish { get; init; } = "stop";
        public string? Refusal { get; init; }
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));
            Requests.Add(body);
            var text = respond(Requests.Count, body);
            return new(Status) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                id = "offline", model = "gpt-4o-mini", created = 0,
                choices = new[] { new { index = 0, finish_reason = Requests.Count >= MetadataFromTurn ? Finish : "stop", message = new
                    { role = "assistant", content = text, refusal = Requests.Count >= MetadataFromTurn ? Refusal : null } } },
                usage = new { prompt_tokens = 10, completion_tokens = 2, total_tokens = 12 }
            }), Encoding.UTF8, "application/json") };
        }
    }
}
