using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.BusinessLogic.Agents.AgentModels;
using ProjectTimeline.DataAccessLogic;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class TimelineFactStrictSchemaTests
{
    // Exercise the native agent and SDK serialization, not a typed-agent/options spy.
    private sealed class StrictProvider(string responseKind) : HttpMessageHandler
    {
        public JsonElement Request { get; private set; }
        public string? Rejection { get; private set; }
        public string? EvidenceId { get; private set; }
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Request = body.RootElement.Clone();
            Assert.EndsWith("/chat/completions", request.RequestUri!.AbsolutePath);
            var format = Request.GetProperty("response_format");
            Assert.Equal("json_schema", format.GetProperty("type").GetString());
            var contract = format.GetProperty("json_schema");
            Assert.Equal("entry_claims", contract.GetProperty("name").GetString());
            Assert.True(contract.GetProperty("strict").GetBoolean());
            Rejection = StrictSchemaError(contract.GetProperty("schema"));
            if (Rejection is not null)
                return Json(HttpStatusCode.BadRequest, new { error = new {
                    message = $"Invalid schema for response_format 'entry_claims': {Rejection}",
                    type = "invalid_request_error", param = "response_format", code = "invalid_json_schema"
                } });

            using var payload = JsonDocument.Parse(Request.GetProperty("messages").EnumerateArray()
                .Last(x => x.GetProperty("role").GetString() == "user").GetProperty("content").GetString()!);
            // Use the ID actually supplied to the model, never an invented/recomputed citation.
            EvidenceId = payload.RootElement.GetProperty("sourceSpans").EnumerateArray().Last()
                .GetProperty("id").GetString();
            var result = new JsonObject { ["claims"] = new JsonArray() };
            if (responseKind != "legacy") result["questions"] = new JsonArray();
            if (responseKind == "question")
                result["questions"]!.AsArray().Add(JsonSerializer.SerializeToNode(new {
                    key = "actor", text = "Who ordered the sensors?", reason = "AmbiguousReference",
                    evidenceIds = new[] { EvidenceId }
                }));
            return Json(HttpStatusCode.OK, new {
                id = "offline", model = "gpt-4o-mini", created = 0,
                choices = new[] { new { index = 0, finish_reason = "stop",
                    message = new { role = "assistant", content = result.ToJsonString() } } }
            });
        }
    }

    private sealed class OptimizerStrictProvider(TimelineFactReviewResult result, bool toolTurn, string finish = "stop") : HttpMessageHandler
    {
        public JsonElement Request { get; private set; }
        public string? Rejection { get; private set; }
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Request = body.RootElement.Clone();
            Assert.EndsWith("/chat/completions", request.RequestUri!.AbsolutePath);
            var format = Request.GetProperty("response_format");
            Assert.Equal("json_schema", format.GetProperty("type").GetString());
            var contract = format.GetProperty("json_schema");
            Assert.True(contract.GetProperty("strict").GetBoolean());
            Assert.Equal("timeline_fact_review", contract.GetProperty("name").GetString());
            Rejection = StrictSchemaError(contract.GetProperty("schema"));
            if (Rejection is not null)
                return Json(HttpStatusCode.BadRequest, new { error = new {
                    message = $"Invalid optimizer response schema: {Rejection}",
                    type = "invalid_request_error", param = "response_format", code = "invalid_json_schema"
                } });
            if (toolTurn && Calls == 1)
                return Json(HttpStatusCode.OK, new {
                    id = "offline-tool", model = "gpt-4o-mini", created = 0,
                    choices = new[] { new { index = 0, finish_reason = "tool_calls", message = new {
                        role = "assistant", tool_calls = new[] { new { id = "context-read", type = "function", function = new {
                            name = "fetch_fact_contexts", arguments = JsonSerializer.Serialize(new { ids = new[] { result.Invalidations[0].FactId } })
                        } } }
                    } } }
                });
            if (toolTurn)
            {
                var toolResult = Request.GetProperty("messages").EnumerateArray().Last(x => x.GetProperty("role").GetString() == "tool");
                Assert.Contains(result.Invalidations[0].FactId.ToString(), toolResult.GetProperty("content").GetString());
            }
            return Json(HttpStatusCode.OK, new {
                id = "offline-optimizer", model = "gpt-4o-mini", created = 0,
                choices = new[] { new { index = 0, finish_reason = finish, message = new {
                    role = "assistant", content = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                } } }
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Optimizer_native_harness_sends_strict_nested_assessment_schema_and_roundtrips_result(bool toolTurn)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        await f.NewSourceAsync();
        f.Entry.Content = "Bob owns delivery instead of Alice.";
        await f.Db.SaveChangesAsync();
        var replacement = new TimelineFactCandidate(f.Entry.Content, "bob", "Person", f.Entry.Content);
        var assessment = TimelineFactContextReconciliationTests.Assessment(fact, replacement, "ExplicitSupersession");
        using var handler = new OptimizerStrictProvider(new() {
            Additions = [replacement], Invalidations = [new(fact.Id, "Explicit delivery ownership change", 0)],
            InvalidationAssessments = [assessment]
        }, toolTurn);
        using var http = new HttpClient(handler);
        var client = new CustomOpenAIClient("fake-offline-credential", http, isolateHarnessFacilities: true);
        var optimizer = new TimelineFactOptimizationAgent(new LocalCustomHarnessAgent(client));
        TimelineFactReviewPlan? plan = null;
        var failure = await Record.ExceptionAsync(async () => plan = await new TimelineFactReviewPlanner(
            f.Db, f.Scope, f.Actor.Id, false, optimizer).PlanAsync());
        Assert.Equal(toolTurn ? 2 : 1, handler.Calls);
        Assert.True(handler.Rejection is null, $"Fake HTTP 400 (param=response_format): {handler.Rejection}");
        Assert.Null(failure);
        var schema = handler.Request.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema");
        Assert.Null(StrictSchemaError(schema));
        var properties = schema.GetProperty("properties");
        var assessments = properties.EnumerateObject().Single(x => x.Name.Equals("InvalidationAssessments", StringComparison.OrdinalIgnoreCase));
        var item = assessments.Value.GetProperty("items");
        // Resolve an SDK $ref when used, then check every assessment field, not merely the root schema.
        if (item.TryGetProperty("$ref", out var reference))
        {
            var path = reference.GetString()!.Split('/').Skip(1);
            item = path.Aggregate(schema, (node, name) => node.GetProperty(name));
        }
        Assert.Equal(new[] { "explanation", "factId", "historicalClaim", "historicalQuote", "historicalSourceFingerprint",
            "invalidatingAdditionIndex", "relationship", "replacementClaim", "replacementQuote", "sameScope", "sharedScope" },
            item.GetProperty("properties").EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.NotNull(plan);
        Assert.Equal(replacement, Assert.Single(plan.Draft.Additions));
        Assert.Equal(fact.Id, Assert.Single(plan.Draft.Invalidations).FactId);
        Assert.True(fact.Valid);
        Assert.False(f.Db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData("length")]
    [InlineData("content_filter")]
    public async Task Optimizer_strict_harness_rejects_nonfinal_provider_metadata(string finish)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        using var handler = new OptimizerStrictProvider(new(), false, finish);
        using var http = new HttpClient(handler);
        var client = new CustomOpenAIClient("fake-offline-credential", http, isolateHarnessFacilities: true);
        var optimizer = new TimelineFactOptimizationAgent(new LocalCustomHarnessAgent(client));
        var error = await Assert.ThrowsAsync<TimelineFactConflictException>(() => new TimelineFactReviewPlanner(
            f.Db, f.Scope, f.Actor.Id, false, optimizer).PlanAsync());
        Assert.Equal("Invalid structured fact review response.", error.Message);
        Assert.Equal(1, handler.Calls);
        Assert.Null(handler.Rejection);
        Assert.False(f.Db.ChangeTracker.HasChanges());
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object value) => new(status)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private static string? StrictSchemaError(JsonElement node, string path = "$")
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("properties", out var properties))
            {
                if (!node.TryGetProperty("additionalProperties", out var additional) || additional.ValueKind != JsonValueKind.False)
                    return $"{path}: additionalProperties must be false";
                var names = properties.EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
                var required = node.TryGetProperty("required", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal) : [];
                if (!names.SetEquals(required))
                    return $"{path}: required must cover all properties; missing [{string.Join(",", names.Except(required))}]";
            }
            foreach (var property in node.EnumerateObject())
            {
                var error = StrictSchemaError(property.Value, $"{path}.{property.Name}");
                if (error is not null) return error;
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in node.EnumerateArray())
            {
                var error = StrictSchemaError(item, $"{path}[{index++}]");
                if (error is not null) return error;
            }
        }
        return null;
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("question")]
    [InlineData("legacy")]
    public async Task Native_outbound_schema_is_strict_and_results_preserve_optional_inbound_questions(string responseKind)
    {
        using var handler = new StrictProvider(responseKind);
        using var http = new HttpClient(handler);
        var client = new CustomOpenAIClient("fake-offline-credential", http);
        TimelineFactExtractionOutcome? outcome = null;
        var failure = await Record.ExceptionAsync(async () => outcome = await new TimelineFactExtractionAgent(client)
            .ExtractOutcomeAsync(new("Delivery", "Someone ordered sensors."), default));

        Assert.Equal(1, handler.Calls);
        Assert.True(handler.Rejection is null, $"Fake HTTP 400 (param=response_format): {handler.Rejection}");
        Assert.Null(failure);
        var schema = handler.Request.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema");
        Assert.Null(StrictSchemaError(schema));
        Assert.Contains("questions", schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        var question = schema.GetProperty("properties").GetProperty("questions").GetProperty("items");
        Assert.Equal(new[] { "evidenceIds", "key", "reason", "text" },
            question.GetProperty("required").EnumerateArray().Select(x => x.GetString()).OrderBy(x => x).ToArray());
        Assert.NotNull(outcome);
        Assert.Empty(outcome.Candidates);
        Assert.False(string.IsNullOrEmpty(handler.EvidenceId));
        if (responseKind == "question")
            Assert.Equal(new TimelineEntryQuestion("actor", "Who ordered the sensors?"), Assert.Single(outcome.Questions));
        else
            Assert.Empty(outcome.Questions);
    }

    [Theory]
    [InlineData("root-required")]
    [InlineData("nested-required")]
    [InlineData("nested-additional")]
    public void Strict_validator_rejects_root_and_nested_contract_gaps(string mutation)
    {
        var schema = JsonNode.Parse("""
            {"type":"object","properties":{"questions":{"type":"array","items":{
              "type":"object","properties":{"text":{"type":"string"}},"required":["text"],"additionalProperties":false
            }}},"required":["questions"],"additionalProperties":false}
            """)!;
        Assert.Null(StrictSchemaError(JsonSerializer.SerializeToElement(schema)));
        var question = schema["properties"]!["questions"]!["items"]!;
        if (mutation == "root-required") schema["required"] = new JsonArray();
        if (mutation == "nested-required") question["required"] = new JsonArray();
        if (mutation == "nested-additional") question["additionalProperties"] = true;
        Assert.NotNull(StrictSchemaError(JsonSerializer.SerializeToElement(schema)));
    }
}
