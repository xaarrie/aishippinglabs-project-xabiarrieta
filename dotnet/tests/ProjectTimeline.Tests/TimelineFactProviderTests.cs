using System.Net;
using System.Text;
using System.Text.Json;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class TimelineFactProviderTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(reply(request));
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Claims(string content, string finish = "stop") => Json(new
    { id = "offline", model = "gpt-4o-mini", created = 0, choices = new[] { new { index = 0, finish_reason = finish, message = new { role = "assistant", content } } } });
    // Existing extraction/embedding transport cases get an explicitly supporting fake verifier.
    // Semantic rejection and two-call orchestration are exercised independently, not inferred here.
    private static TimelineFactOpenAIProvider Provider(Func<HttpRequestMessage, HttpResponseMessage> reply) =>
        CreateProvider(new HttpClient(new Handler(request =>
        {
            var text = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var body = JsonDocument.Parse(text);
            if (!body.RootElement.TryGetProperty("response_format", out var format)
                || format.GetProperty("json_schema").GetProperty("name").GetString() != "fact_semantic_assessments") return reply(request);
            using var input = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            var id = input.RootElement.GetProperty("sourceSpans").EnumerateArray().Last().GetProperty("id").GetString();
            return Claims(JsonSerializer.Serialize(new { assessments = input.RootElement.GetProperty("candidates").EnumerateArray().Select(x => new
            {
                candidateIndex = x.GetProperty("candidateIndex").GetInt32(), supported = true, supportScore = 0.5,
                issues = Array.Empty<string>(), evidenceIds = new[] { id }
            }) }));
        })));
    private static TimelineFactOpenAIProvider CreateProvider(HttpClient transport)
    {
        var client = new CustomOpenAIClient("fake-offline-credential", transport);
        return new(new TimelineFactExtractionAgent(client), new TimelineFactSemanticVerifierAgent(client), client);
    }

    [Fact]
    public async Task Structured_claims_allow_approximate_citations_but_require_exact_properties()
    {
        var content = "{\"claims\":[{\"claim\":\"Alice owns delivery\",\"entityKey\":\"alice\",\"entityType\":\"Person\",\"sourceQuote\":\"Alice owns delivery.\"}]}";
        var provider = Provider(request =>
        {
            Assert.Equal("https://api.openai.com/v1/chat/completions", request.RequestUri!.ToString());
            return Claims(content);
        });
        var result = await provider.ExtractAsync(new("Title", "Alice owns delivery."), default);
        Assert.Equal("alice", Assert.Single(result).EntityKey);
        content = content.Replace("Alice owns delivery.", "Unseen evidence");
        Assert.Equal("Alice owns delivery.", Assert.Single(await provider.ExtractAsync(new("Title", "Alice owns delivery."), default)).SourceQuote);
        content = "{\"claims\":[],\"unexpected\":true}";
        await Assert.ThrowsAsync<TimelineFactProviderException>(() => provider.ExtractAsync(new("Title", "Source"), default));
    }

    [Theory]
    [InlineData("{not-json", "stop")]
    [InlineData("{\"claims\":[]}", "length")]
    [InlineData("{\"claims\":[null]}", "stop")]
    [InlineData("{\"claims\":null}", "stop")]
    [InlineData("{\"claims\":[{\"claim\":\"Source\",\"entityKey\":\"x\",\"entityType\":\"Thing\",\"sourceQuote\":\"Source\",\"extra\":true}]}", "stop")]
    [InlineData("{\"claims\":[{\"claim\":null,\"entityKey\":\"x\",\"entityType\":\"Thing\",\"sourceQuote\":\"Source\"}]}", "stop")]
    [InlineData("{\"claims\":[],\"claims\":[]}", "stop")]
    public async Task Malformed_or_truncated_results_are_safe_errors(string content, string finish)
    {
        var provider = Provider(_ => Claims(content, finish));
        var error = await Assert.ThrowsAsync<TimelineFactProviderException>(() => provider.ExtractAsync(new("Title", "Content"), default));
        Assert.Equal("InvalidResponse", error.Code); Assert.Equal("Fact provider failed.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task Detailed_fixture_preserves_each_supported_statement_and_prompt_requires_coverage()
    {
        // A transport/contract fixture, not evidence that a live model finds every statement.
        string[] quotes = [
            "On 12 March 2026, Alice ordered 24 sensors from Acme for EUR 480.",
            "Acme must deliver by 30 April 2026 if the permit is approved.",
            "Bob reports to Alice.",
            "Bob will install 6 sensors per floor on 4 floors.",
            "Installation starts only after delivery and safety approval.",
            "The remaining 2 spare batteries belong to the maintenance team."
        ];
        var candidates = quotes.Select((quote, index) => new {
            claim = quote, entityKey = index == 2 || index == 3 ? "bob" : "project",
            entityType = "Actor", sourceQuote = quote
        }).ToArray();
        var source = new TimelineFactExtraction("Sensor rollout", string.Join("\n", quotes));
        string? sent = null;
        var provider = Provider(request =>
        {
            sent = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Claims(JsonSerializer.Serialize(new { claims = candidates }));
        });
        var result = await provider.ExtractAsync(source, default);
        using (var body = JsonDocument.Parse(sent!))
        {
            var root = body.RootElement;
            var prompt = root.GetProperty("messages")[0].GetProperty("content").GetString()!;
            foreach (var requirement in new[] { "every independent", "quantities", "dates", "actors", "conditions", "relationships",
                "Do not summarize", "no minimum", "Do not invent", "verbatim", "untrusted", "200" })
                Assert.Contains(requirement, prompt, StringComparison.OrdinalIgnoreCase);
            using var payload = JsonDocument.Parse(root.GetProperty("messages")[1].GetProperty("content").GetString()!);
            var input = payload.RootElement.GetProperty("source").Deserialize<TimelineFactExtraction>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Equal(source, input);
            Assert.Equal(12000, root.GetProperty("max_completion_tokens").GetInt32());
            var format = root.GetProperty("response_format");
            Assert.Equal("json_schema", format.GetProperty("type").GetString());
            var contract = format.GetProperty("json_schema");
            Assert.True(contract.GetProperty("strict").GetBoolean());
            var schema = contract.GetProperty("schema");
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
            var item = schema.GetProperty("properties").GetProperty("claims").GetProperty("items");
            Assert.False(item.GetProperty("additionalProperties").GetBoolean());
            Assert.Equal(new[] { "claim", "entityKey", "entityType", "sourceQuote" }, item.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
            Assert.Equal(200, root.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema")
                .GetProperty("properties").GetProperty("claims").GetProperty("maxItems").GetInt32());
        }
        Assert.Equal(quotes, result.Select(x => x.Claim));
        Assert.All(result, x => Assert.NotEmpty(x.SourceQuote));
        Assert.All(result, x => Assert.Contains(x.SourceQuote, source.Content, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{not-json", "stop", "ResponseSchema")]
    [InlineData("{\"claims\":[]}", "length", "ResponseTruncated")]
    [InlineData("{\"claims\":[]}", "content_filter", "ResponseRefused")]
    [InlineData("{\"claims\":[],\"extra\":true}", "stop", "ResponseSchema")]
    [InlineData("{\"claims\":[null]}", "stop", "CandidateShape")]
    public async Task Validation_diagnostics_are_granular_without_payloads(string content, string finish, string diagnostic)
    {
        var error = await Assert.ThrowsAsync<TimelineFactProviderException>(() =>
            Provider(_ => Claims(content, finish)).ExtractAsync(new("Title", "Source"), default));
        Assert.Equal("InvalidResponse", error.Code); // Retry categorization remains compatible.
        Assert.Equal(diagnostic, error.Data["DiagnosticCode"]);
        Assert.DoesNotContain("PRIVATE", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(201, 1)]
    [InlineData(1, 20001)]
    public async Task Oversized_input_is_rejected_without_sending_or_slicing(int titleLength, int contentLength)
    {
        var calls = 0;
        var provider = Provider(_ => { calls++; return Claims("{\"claims\":[]}"); });
        var error = await Assert.ThrowsAsync<TimelineFactProviderException>(() => provider.ExtractAsync(
            new(new string('t', titleLength), new string('c', contentLength)), default));
        Assert.Equal("InvalidResponse", error.Code);
        Assert.Equal("InputLimit", error.Data["DiagnosticCode"]);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Capacity_saturation_rejects_possible_silent_claim_loss()
    {
        var candidates = Enumerable.Range(0, 200).Select(i => new {
            claim = $"Claim {i}", entityKey = "x", entityType = "Thing", sourceQuote = "Source"
        });
        var error = await Assert.ThrowsAsync<TimelineFactProviderException>(() => Provider(_ =>
            Claims(JsonSerializer.Serialize(new { claims = candidates }))).ExtractAsync(new("Title", "Source"), default));
        Assert.Equal("InvalidResponse", error.Code);
        Assert.Equal("CandidateLimit", error.Data["DiagnosticCode"]);
    }

    [Fact]
    public async Task Duplicate_and_invalid_candidate_fields_are_rejected_without_echoing_details()
    {
        var candidate = new { claim = "Source", entityKey = "entity", entityType = "Thing", sourceQuote = "Source" };
        var duplicate = await Assert.ThrowsAsync<TimelineFactProviderException>(() => Provider(_ => Claims(
            JsonSerializer.Serialize(new { claims = new[] { candidate, candidate } }))).ExtractAsync(new("Title", "Source"), default));
        Assert.Equal("DuplicateCandidate", duplicate.DiagnosticCode);
        var invalid = await Assert.ThrowsAsync<TimelineFactProviderException>(() => Provider(_ => Claims(
            JsonSerializer.Serialize(new { claims = new[] { candidate with { entityKey = new string('x', 301) } } })))
            .ExtractAsync(new("Title", "Source"), default));
        Assert.Equal("CandidateShape", invalid.DiagnosticCode);
        Assert.Equal("InvalidResponse", invalid.Code);
    }

    [Theory]
    [InlineData("{not-json", "ResponseEnvelope")]
    [InlineData("{\"choices\":[]}", "ResponseEnvelope")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"{\\\"claims\\\":[]}\"}}]}", "ResponseEnvelope")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"PRIVATE unknown\",\"message\":{\"content\":\"{\\\"claims\\\":[]}\"}}]}", "ResponseEnvelope")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"refusal\":\"PRIVATE refusal\",\"content\":null}}]}", "ResponseRefused")]
    public async Task Envelope_and_refusal_failures_have_safe_diagnostics(string response, string diagnostic)
    {
        var error = await Assert.ThrowsAsync<TimelineFactProviderException>(() => Provider(_ => new(HttpStatusCode.OK) {
            Content = new StringContent(response, Encoding.UTF8, "application/json") }).ExtractAsync(new("Title", "Source"), default));
        Assert.Equal("InvalidResponse", error.Code);
        Assert.Equal(diagnostic, error.DiagnosticCode);
        Assert.DoesNotContain("PRIVATE", error.ToString());
    }

    [Fact]
    public async Task Maximum_input_and_below_capacity_output_are_transmitted_intact()
    {
        var source = new TimelineFactExtraction(new string('t', 200), new string('c', 20000));
        string? sent = null;
        var candidates = Enumerable.Range(0, 199).Select(i => new {
            claim = $"Claim {i}", entityKey = "entity", entityType = "Thing", sourceQuote = "ccc"
        });
        var result = await Provider(request =>
        {
            sent = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Claims(JsonSerializer.Serialize(new { claims = candidates }));
        }).ExtractAsync(source, default);
        using var body = JsonDocument.Parse(sent!);
        using var payload = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        Assert.Equal(source, payload.RootElement.GetProperty("source").Deserialize<TimelineFactExtraction>(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(199, result.Count);
    }

    [Fact]
    public void Diagnostic_vocabulary_cannot_echo_unknown_provider_values()
    {
        var error = new TimelineFactProviderException("PRIVATE category", "PRIVATE payload");
        Assert.Equal("Unavailable", error.Code);
        Assert.Null(error.DiagnosticCode);
        Assert.Empty(error.Data.Keys.Cast<object>());
        Assert.DoesNotContain("PRIVATE", error.ToString());
    }

    [Fact]
    public async Task Embedding_requires_model_provenance_and_expected_dimension()
    {
        var model = "text-embedding-3-small";
        var vector = Enumerable.Repeat(0.01f, 1536).ToArray();
        var provider = Provider(_ => Json(new { model, data = new[] { new { index = 0, embedding = vector } } }));
        Assert.Equal(1536, (await provider.EmbedAsync("A claim", default)).Length);
        model = "other-model";
        Assert.Equal("InvalidResponse", (await Assert.ThrowsAsync<TimelineFactProviderException>(() => provider.EmbedAsync("A claim", default))).Code);
    }

    [Fact]
    public async Task Errors_and_oversized_responses_do_not_expose_provider_payload()
    {
        var provider = Provider(_ => new(HttpStatusCode.TooManyRequests) { Content = new StringContent("PRIVATE provider body") });
        var error = await Assert.ThrowsAsync<TimelineFactProviderException>(() => provider.EmbedAsync("A claim", default));
        Assert.Equal("RateLimited", error.Code); Assert.DoesNotContain("PRIVATE", error.ToString());
        provider = Provider(_ => new(HttpStatusCode.OK) { Content = new StringContent(new string('x', 524289)) });
        Assert.Equal("InvalidResponse", (await Assert.ThrowsAsync<TimelineFactProviderException>(() => provider.EmbedAsync("A claim", default))).Code);
    }
}
