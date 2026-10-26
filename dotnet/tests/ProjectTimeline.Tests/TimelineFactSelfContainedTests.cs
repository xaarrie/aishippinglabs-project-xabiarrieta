using System.Net;
using System.Text;
using System.Text.Json;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using Xunit;

namespace ProjectTimeline.Tests;

// Curated outputs and fake assessments prove transport/prompt contracts, not live-model coverage.
public sealed class TimelineFactSelfContainedTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(reply(request)); }
    }

    private static HttpResponseMessage Completion(object response) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new {
            id = "offline", model = "gpt-4o-mini", created = 0,
            choices = new[] { new { index = 0, finish_reason = "stop", message = new {
                role = "assistant", content = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)) } } }
        }), Encoding.UTF8, "application/json")
    };

    private static (TimelineFactExtraction Source, TimelineFactCandidate[] Claims) Fixture(bool schema)
    {
        if (!schema)
        {
            const string source = "En el proyecto Atlas, el coche C-7 es rojo. En el proyecto Atlas, el coche C-7 pesa 1200 kg sin carga desde el 12 de marzo de 2026.";
            return (new("Proyecto Atlas", source), [
                new("En el proyecto Atlas, el coche C-7 es rojo.", "atlas/c-7", "Coche", source),
                new("En el proyecto Atlas, el coche C-7 pesa 1200 kg sin carga desde el 12 de marzo de 2026.", "atlas/c-7", "Coche", source)
            ]);
        }
        const string definition = "In project Atlas, billing system table Invoice stores customer invoices for the EU tenant only. " +
            "For the EU tenant in Atlas billing, Invoice.id is uuid NOT NULL PRIMARY KEY with default gen_random_uuid(); " +
            "Invoice.customer_id is uuid NOT NULL and REFERENCES Customer(id); " +
            "Invoice.memo is varchar(120) NULL with default NULL; " +
            "Invoice.amount is decimal(12,2) NOT NULL, measured in EUR, with CHECK (amount >= 0); " +
            "Invoice.due_on is date NULL; Invoice.invoice_no is varchar(30) NOT NULL UNIQUE. " +
            "This Invoice definition applies from 2026-03-12 only when EU billing is enabled.";
        const string scope = "In project Atlas billing, for the EU tenant only, from 2026-03-12 when EU billing is enabled, table Invoice";
        return (new("Atlas billing schema", definition), [
            new($"{scope} stores customer invoices.", "atlas/billing/invoice", "Table", definition),
            new($"{scope} has column id of type uuid NOT NULL PRIMARY KEY with default gen_random_uuid().", "atlas/billing/invoice", "Table", definition),
            new($"{scope} has column customer_id of type uuid NOT NULL, referencing Customer(id).", "atlas/billing/invoice", "Table", definition),
            new($"{scope} has column memo of type varchar(120) NULL with default NULL.", "atlas/billing/invoice", "Table", definition),
            new($"{scope} has column amount of type decimal(12,2) NOT NULL, measured in EUR, with CHECK (amount >= 0).", "atlas/billing/invoice", "Table", definition),
            new($"{scope} has column due_on of type date NULL.", "atlas/billing/invoice", "Table", definition),
            new($"{scope} has column invoice_no of type varchar(30) NOT NULL UNIQUE.", "atlas/billing/invoice", "Table", definition)
        ]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Descriptions_and_schema_components_have_explicit_prompt_contract_and_survive_as_distinct_claims(bool schema)
    {
        var (source, claims) = Fixture(schema);
        var prompts = new List<string>();
        var calls = 0;
        var client = new CustomOpenAIClient("fake-offline-credential", new HttpClient(new Handler(request =>
        {
            Assert.EndsWith("/chat/completions", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var messages = body.RootElement.GetProperty("messages");
            prompts.Add(messages[0].GetProperty("content").GetString()!);
            using var input = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
            calls++;
            if (calls == 1)
            {
                Assert.Equal(source.Content, input.RootElement.GetProperty("source").GetProperty("content").GetString());
                return Completion(new { claims = claims.Select(c => new { c.Claim, c.EntityKey, c.EntityType, c.SourceQuote }) });
            }
            Assert.Equal(2, calls);
            Assert.Equal(source.Content, input.RootElement.GetProperty("source").GetProperty("content").GetString());
            var indexed = input.RootElement.GetProperty("candidates").EnumerateArray().ToArray();
            Assert.Equal(claims.Length, indexed.Length);
            for (var i = 0; i < claims.Length; i++)
            {
                Assert.Equal(i, indexed[i].GetProperty("candidateIndex").GetInt32());
                Assert.Equal(claims[i].Claim, indexed[i].GetProperty("candidate").GetProperty("claim").GetString());
            }
            var ids = input.RootElement.GetProperty("sourceSpans").EnumerateArray()
                .Where(x => x.GetProperty("source").GetString() == "Content").Select(x => x.GetProperty("id").GetString()).ToArray();
            return Completion(new { assessments = claims.Select((_, i) => new {
                candidateIndex = i, supported = true, supportScore = 0.0, issues = Array.Empty<string>(), evidenceIds = ids
            }) });
        })));
        var result = await new TimelineFactOpenAIProvider(new(client), new(client), client).ExtractAsync(source, default);
        Assert.Equal(2, calls);
        foreach (var phrase in new[] { "descriptions", "definitions", "attributes", "car", "color", "table", "columns", "nullability", "keys", "defaults", "constraints",
            "self-contained", "project", "system", "scope", "original names and language", "4000", "same entity", "outside knowledge" })
            Assert.Contains(phrase, prompts[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("it/this/its", prompts[0]);
        foreach (var phrase in new[] { "descriptions", "definitions", "schema attributes", "self-contained", "scope", "conditional", "entire claim", "no numeric acceptance threshold", "outside knowledge" })
            Assert.Contains(phrase, prompts[1], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(claims, result);
        Assert.Single(result.Select(x => x.EntityKey).Distinct());
        Assert.Equal(claims.Length, result.Select(x => x.Claim).Distinct().Count());
        Assert.All(result, x => Assert.InRange(x.Claim.Length, 1, 4000));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unsupported_definition_or_lost_condition_rejects_whole_batch_despite_high_score(bool lostCondition)
    {
        var (source, claims) = Fixture(true);
        claims[^1] = claims[^1] with { Claim = lostCondition
            ? "Atlas billing table Invoice always has column invoice_no of type varchar(30) NOT NULL UNIQUE."
            : "Atlas billing table Invoice has an index on due_on." };
        var calls = 0;
        var client = new CustomOpenAIClient("fake-offline-credential", new HttpClient(new Handler(_ =>
        {
            calls++;
            if (calls == 1) return Completion(new { claims = claims.Select(c => new { c.Claim, c.EntityKey, c.EntityType, c.SourceQuote }) });
            var ids = TimelineFactSourceEvidence.Create(source).Where(x => x.Source == "Content").Select(x => x.Id).ToArray();
            return Completion(new { assessments = claims.Select((_, i) => new {
                candidateIndex = i, supported = i != claims.Length - 1, supportScore = 1.0,
                issues = i == claims.Length - 1 ? new[] { "Unsupported" } : Array.Empty<string>(), evidenceIds = ids
            }) });
        })));
        var error = await Assert.ThrowsAsync<TimelineFactProviderException>(() =>
            new TimelineFactOpenAIProvider(new(client), new(client), client).ExtractAsync(source, default));
        Assert.Equal("SemanticUnsupported", error.DiagnosticCode);
        Assert.Equal(2, calls);
    }
}
