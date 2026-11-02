using System.Net;
using System.Reflection;
using Microsoft.Extensions.AI;
using ProjectTimeline.BusinessLogic.Agents.AgentModels;
using System.Text;
using System.Text.Json;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class SearchCitationAllowlistTests
{
    private const string Plan = "{\"decision\":\"Search\",\"queries\":[\"alice\"],\"refusalReason\":null}";

    [Fact]
    public async Task Native_wire_enums_are_exact_projected_fact_and_context_namespaces()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        await f.NewSourceAsync();
        var unemitted = await f.AcceptAsync("Bob owns testing.", "bob");
        var capture = new TimelineFactCitationCapture();
        using var transport = new Wire((turn, request) =>
        {
            if (turn == 1) return Plan;
            var input = Input(request);
            Assert.NotNull(capture.Snapshot.Available); Assert.Null(capture.Snapshot.Observed);
            var facts = input.GetProperty("matches").GetProperty("Results").EnumerateArray()
                .SelectMany(x => x.GetProperty("Facts").EnumerateArray()).Select(x => x.GetProperty("Id").GetGuid()).Distinct().ToArray();
            var contexts = input.GetProperty("sources").GetProperty("Contexts").EnumerateArray()
                .Select(x => x.GetProperty("EvidenceId").GetGuid()).ToArray();
            var properties = request.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema").GetProperty("properties");
            Assert.Equal(facts.Select(x => x.ToString("D")).Order(), Enum(properties, "citedFactIds").Order());
            Assert.Equal(contexts.Select(x => x.ToString("D")).Order(), Enum(properties, "citedContextEvidenceIds").Order());
            Assert.DoesNotContain(unemitted.Id.ToString(), Enum(properties, "citedFactIds"));
            Assert.DoesNotContain(fact.EntryId.ToString(), Enum(properties, "citedContextEvidenceIds"));
            Assert.DoesNotContain(fact.Id.ToString(), Enum(properties, "citedContextEvidenceIds"));
            return JsonSerializer.Serialize(new { content = "Alice owns delivery.", citedFactIds = facts, citedContextEvidenceIds = contexts });
        });
        var response = await Agent(f, transport).RunAsync("alice", default, capture);
        Assert.Single(response.Citations); Assert.Single(response.ContextCitations);
    }

    [Theory]
    [InlineData("entry")]
    [InlineData("fact")]
    [InlineData("unknown")]
    [InlineData("unemitted")]
    public async Task Failed_native_leaf_records_both_namespaces_and_invalid_counts_before_rejection(string kind)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        await f.NewSourceAsync();
        var unemitted = await f.AcceptAsync("Bob owns testing.", "bob");
        var wrong = kind == "entry" ? fact.EntryId : kind == "fact" ? fact.Id : Guid.NewGuid();
        var unknownFact = kind == "unemitted" ? unemitted.Id : Guid.NewGuid();
        var capture = new TimelineFactCitationCapture();
        using var wire = new Wire((turn, _) => turn == 1 ? Plan : JsonSerializer.Serialize(new
        {
            content = "PRIVATE_SOURCE_QUESTION_MODEL_SECRET",
            citedFactIds = new[] { fact.Id.ToString(), unknownFact.ToString(), "PRIVATE_INVALID_SECRET" },
            citedContextEvidenceIds = new[] { wrong.ToString(), wrong.ToString(), "PRIVATE_INVALID_SECRET" }
        }));
        var error = await Assert.ThrowsAsync<TimelineFactInformationSynthesisException>(() => Agent(f, wire).RunAsync("alice", default, capture));
        Assert.Equal("ResponseSchema", Assert.IsType<TimelineFactProviderException>(error.InnerException).DiagnosticCode);
        var snapshot = capture.Snapshot;
        Assert.Equal(new[] { fact.Id }, snapshot.Available!.AvailableFactIds);
        Assert.Single(snapshot.Available.ContextEvidenceIds);
        Assert.Equal(new[] { unknownFact }, snapshot.Observed!.Facts.UnknownIds);
        Assert.Equal(new[] { wrong }, snapshot.Observed.Contexts.UnknownIds);
        Assert.Equal((2, 2, 1), (snapshot.Observed.Facts.CitedOccurrenceCount, snapshot.Observed.Facts.CitedDistinctCount, snapshot.Observed.Facts.InvalidGuidCount));
        Assert.Equal((2, 1, 1), (snapshot.Observed.Contexts.CitedOccurrenceCount, snapshot.Observed.Contexts.CitedDistinctCount, snapshot.Observed.Contexts.InvalidGuidCount));
        Assert.False(snapshot.FinalValidationAttempted); Assert.Null(snapshot.FinalLedger);
        Assert.False(snapshot.PublicationAttempted); Assert.False(snapshot.Published);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(snapshot)); Assert.Equal(2, wire.Calls);
    }

    [Fact]
    public async Task Uppercase_known_GUIDs_are_admitted_and_repeated_validation_is_idempotent()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        var capture = new TimelineFactCitationCapture();
        using var wire = new Wire((turn, request) => turn == 1 ? Plan : JsonSerializer.Serialize(new
        {
            content = "Answer", citedFactIds = new[] { fact.Id.ToString("D").ToUpperInvariant() },
            citedContextEvidenceIds = new[] { Input(request).GetProperty("sources").GetProperty("Contexts")[0].GetProperty("EvidenceId").GetString()!.ToUpperInvariant() }
        }));
        var result = await Agent(f, wire).RunAsync("alice", default, capture);
        Assert.Equal(fact.Id, Assert.Single(result.Citations).FactId);
        var snapshot = capture.Snapshot;
        Assert.Equal((1, 1, 0), (snapshot.Observed!.Facts.CitedOccurrenceCount, snapshot.Observed.Facts.CitedDistinctCount, snapshot.Observed.Facts.InvalidGuidCount));
        Assert.Equal(1, snapshot.Observed.Contexts.CitedOccurrenceCount);
        Assert.Equal<Guid>(snapshot.Available!.AvailableFactIds, snapshot.FinalLedger!.FactIds);
        Assert.Equal<Guid>(snapshot.Available.ContextEvidenceIds, snapshot.FinalLedger.ContextEvidenceIds);
        Assert.True(snapshot.FinalValidationAttempted); Assert.True(snapshot.PublicationAttempted); Assert.True(snapshot.Published);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Zero_context_wire_requires_typed_empty_array_and_records_explicit_omissions(bool invalid)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        var capture = new TimelineFactCitationCapture();
        using var wire = new Wire((turn, request) =>
        {
            if (turn == 1) return Plan;
            var sources = Input(request).GetProperty("sources");
            Assert.Empty(sources.GetProperty("Contexts").EnumerateArray());
            Assert.Equal(fact.EntryId, Assert.Single(sources.GetProperty("OmittedEntryIds").EnumerateArray()).GetGuid());
            var array = request.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema")
                .GetProperty("properties").GetProperty("citedContextEvidenceIds");
            Assert.Equal(0, array.GetProperty("maxItems").GetInt32());
            Assert.Equal("string", array.GetProperty("items").GetProperty("type").GetString());
            Assert.Equal("uuid", array.GetProperty("items").GetProperty("format").GetString());
            Assert.False(array.GetProperty("items").TryGetProperty("enum", out _));
            return JsonSerializer.Serialize(new { content = "Answer", citedFactIds = new[] { fact.Id },
                citedContextEvidenceIds = invalid ? new[] { fact.EntryId } : Array.Empty<Guid>() });
        });
        var operation = Agent(f, wire, new(maxTotalSerializedToolEvidenceChars: 650)).RunAsync("alice", default, capture);
        if (invalid) await Assert.ThrowsAsync<TimelineFactInformationSynthesisException>(() => operation);
        else
        {
            var result = await operation;
            Assert.Empty(result.SourceContexts); Assert.Equal("Partial", result.Research!.Status);
            Assert.Equal<Guid>(capture.Snapshot.Available!.AvailableFactIds, capture.Snapshot.FinalLedger!.FactIds);
            Assert.Empty(capture.Snapshot.FinalLedger.ContextEvidenceIds);
        }
        Assert.True(capture.Snapshot.Available!.EvidenceTruncated);
        Assert.Equal(new[] { fact.EntryId }, capture.Snapshot.Available.OmittedEntryIds);
        Assert.Equal(2, wire.Calls);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("null")]
    [InlineData("{\"content\":\"secret\",\"citedFactIds\":[]}")]
    [InlineData("{\"content\":\"secret\",\"citedFactIds\":[],\"citedContextEvidenceIds\":null}")]
    [InlineData("{\"content\":\"secret\",\"citedFactIds\":[],\"citedFactIds\":[],\"citedContextEvidenceIds\":[]}")]
    public async Task Malformed_or_ambiguous_citation_arrays_do_not_assert_empty_observation(string text)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await f.AcceptAsync("Alice owns delivery.", "alice");
        var capture = new TimelineFactCitationCapture();
        using var wire = new Wire((turn, _) => turn == 1 ? Plan : text);
        await Assert.ThrowsAsync<TimelineFactInformationSynthesisException>(() => Agent(f, wire).RunAsync("alice", default, capture));
        Assert.NotNull(capture.Snapshot.Available); Assert.Null(capture.Snapshot.Observed);
        Assert.False(capture.Snapshot.FinalValidationAttempted);
    }

    [Fact]
    public async Task Content_rejection_still_observes_both_arrays_and_native_refusal_does_not()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        var capture = new TimelineFactCitationCapture();
        using var wire = new Wire((turn, _) => turn == 1 ? Plan : JsonSerializer.Serialize(new
        { content = " ", citedFactIds = new[] { fact.Id }, citedContextEvidenceIds = Array.Empty<Guid>() }));
        await Assert.ThrowsAsync<TimelineFactInformationSynthesisException>(() => Agent(f, wire).RunAsync("alice", default, capture));
        Assert.Equal(new[] { fact.Id }, capture.Snapshot.Observed!.Facts.CitedIds);
        Assert.Empty(capture.Snapshot.Observed.Contexts.CitedIds); Assert.False(capture.Snapshot.FinalValidationAttempted);
        var refusedCapture = new TimelineFactCitationCapture();
        using var refusal = new Wire((turn, _) => turn == 1 ? Plan : "PRIVATE_INVALID_JSON") { Finish = "content_filter" };
        var error = await Assert.ThrowsAsync<TimelineFactInformationSynthesisException>(() => Agent(f, refusal).RunAsync("alice", default, refusedCapture));
        Assert.Equal("ResponseRefused", Assert.IsType<TimelineFactProviderException>(error.InnerException).DiagnosticCode);
        Assert.NotNull(refusedCapture.Snapshot.Available); Assert.Null(refusedCapture.Snapshot.Observed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Typed_bypass_still_hits_unchanged_final_membership_guards(bool context)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        var fake = new InformationFakeAgent { Result = new() { Content = "Answer", CitedFactIds = [fact.Id] } };
        if (context) fake.Result.CitedContextEvidenceIds = [fact.EntryId];
        else fake.Result.CitedFactIds = [Guid.NewGuid()];
        var capture = new TimelineFactCitationCapture();
        var operation = new SearchEntriesAgent(f.Db, new(f.Project.Id, f.Timeline.Id), f.Actor.Id, false, () => fake);
        var error = await Assert.ThrowsAsync<TimelineFactInformationSynthesisException>(() => operation.RunAsync("alice", default, capture));
        Assert.Equal(context ? TimelineFactSynthesisFailureReason.UnknownContextCitation : TimelineFactSynthesisFailureReason.UnknownFactCitation, error.Reason);
        Assert.True(capture.Snapshot.FinalValidationAttempted); Assert.NotNull(capture.Snapshot.FinalLedger);
        Assert.True(capture.Snapshot.PublicationAttempted); Assert.False(capture.Snapshot.Published);
        Assert.Equal(1, context ? capture.Snapshot.Observed!.Contexts.UnknownDistinctCount : capture.Snapshot.Observed!.Facts.UnknownDistinctCount);
    }

    [Fact]
    public async Task Diagnostic_clipping_preserves_exact_counts_and_never_clips_membership_catalog()
    {
        var allowed = Enumerable.Range(0, 160).Select(_ => Guid.NewGuid()).ToArray();
        var unknown = Enumerable.Range(0, 180).Select(_ => Guid.NewGuid()).ToArray();
        var capture = new TimelineFactCitationCapture();
        var options = Options(allowed.ToArray(), [], capture);
        var agent = new LocalCustomHarnessAgent(new CustomOpenAIClient("offline-synthetic-key", new HttpClient(new Wire((_, _) =>
            JsonSerializer.Serialize(new { content = "", citedFactIds = allowed.Concat(unknown).Concat(unknown).Select(x => x.ToString()).Append("PRIVATE_SECRET"),
                citedContextEvidenceIds = new[] { Guid.NewGuid().ToString(), "PRIVATE_SECRET" } }))), true));
        agent.Init("offline", "gpt-4o-mini", "offline", []);
        await Assert.ThrowsAsync<TimelineFactProviderException>(() => agent.RunAsync<TimelineFactInformationResult>("offline", options, default));
        var observed = capture.Snapshot.Observed!;
        Assert.Equal((520, 340, 180, 1), (observed.Facts.CitedOccurrenceCount, observed.Facts.CitedDistinctCount, observed.Facts.UnknownDistinctCount, observed.Facts.InvalidGuidCount));
        Assert.Equal(128, observed.Facts.CitedIds.Length); Assert.Equal(128, observed.Facts.UnknownIds.Length);
        Assert.True(observed.Facts.DiagnosticClipped); Assert.Equal(1, observed.Contexts.InvalidGuidCount);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(capture.Snapshot));
    }

    [Fact]
    public async Task Catalog_and_schema_copy_projection_before_input_mutation_or_disposal_and_empty_facts_remain_typed()
    {
        var fact = Guid.NewGuid(); var context = Guid.NewGuid();
        var options = Options([fact], [context], new());
        using var wire = new Wire((_, request) =>
        {
            var properties = request.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema").GetProperty("properties");
            Assert.Equal(new[] { fact.ToString() }, Enum(properties, "citedFactIds"));
            Assert.Equal(new[] { context.ToString() }, Enum(properties, "citedContextEvidenceIds"));
            return JsonSerializer.Serialize(new { content = "Answer", citedFactIds = new[] { fact }, citedContextEvidenceIds = new[] { context } });
        });
        var agent = new LocalCustomHarnessAgent(new CustomOpenAIClient("offline-synthetic-key", new HttpClient(wire), true));
        agent.Init("offline", "gpt-4o-mini", "offline", []);
        Assert.Single((await agent.RunAsync<TimelineFactInformationResult>("offline", options, default)).Result.CitedFactIds);
        var empty = Options([], [], new());
        var schema = Assert.IsType<ChatResponseFormatJson>(empty.ChatOptions.ResponseFormat).Schema!.Value;
        foreach (var name in new[] { "citedFactIds", "citedContextEvidenceIds" })
        {
            Assert.Contains(name, schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
            var array = schema.GetProperty("properties").GetProperty(name);
            Assert.Equal(0, array.GetProperty("maxItems").GetInt32());
            Assert.Equal("uuid", array.GetProperty("items").GetProperty("format").GetString());
            Assert.Equal("string", array.GetProperty("items").GetProperty("type").GetString());
        }
        Assert.DoesNotContain("\"enum\"", schema.GetRawText());
    }

    // Internal factory is exercised without expanding the production API just for tests.
    private static StructuredAgentRunOptions Options(Guid[] facts, Guid[] contexts, TimelineFactCitationCapture capture)
    {
        using var matches = JsonDocument.Parse(JsonSerializer.Serialize(new { Results = new[] { new { Facts = facts.Select(Id => new { Id }) } }, Truncated = false }));
        using var sources = JsonDocument.Parse(JsonSerializer.Serialize(new { Contexts = contexts.Select(EvidenceId => new { EvidenceId }), OmittedEntryIds = Array.Empty<Guid>(), Truncated = false }));
        var assembly = typeof(SearchEntriesAgent).Assembly;
        var catalog = assembly.GetType("ProjectTimeline.BusinessLogic.Agents.TimelineFactCitationCatalog")!
            .GetMethod("FromProjection", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [matches.RootElement, sources.RootElement]);
        var options = (StructuredAgentRunOptions)assembly.GetType("ProjectTimeline.BusinessLogic.Agents.TimelineFactStructuredResponse")!
            .GetMethod("SearchSynthesisOptions", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [1500, catalog, capture])!;
        Array.Fill(facts, Guid.NewGuid()); Array.Fill(contexts, Guid.NewGuid());
        return options;
    }

    private static string[] Enum(JsonElement properties, string name)
    {
        var items = properties.GetProperty(name).GetProperty("items");
        Assert.Equal("string", items.GetProperty("type").GetString());
        Assert.Equal("uuid", items.GetProperty("format").GetString());
        return items.GetProperty("enum").EnumerateArray().Select(x => x.GetString()!).ToArray();
    }
    private static JsonElement Input(JsonElement request) => JsonSerializer.Deserialize<JsonElement>(
        request.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!);
    private static SearchEntriesAgent Agent(TimelineFactReviewFixture f, Wire transport, TimelineFactResearchOptions? options = null) =>
        new(f.Db, new(f.Project.Id, f.Timeline.Id), f.Actor.Id, false,
            () => new LocalCustomHarnessAgent(new CustomOpenAIClient("offline-synthetic-key", new HttpClient(transport), true)), options: options);

    private sealed class Wire(Func<int, JsonElement, string> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string Finish { get; init; } = "stop";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));
            var text = respond(++Calls, body);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                id = "offline", model = "gpt-4o-mini", created = 0,
                choices = new[] { new { index = 0, finish_reason = Calls == 1 ? "stop" : Finish, message = new { role = "assistant", content = text } } }
            }), Encoding.UTF8, "application/json") };
        }
    }
}
