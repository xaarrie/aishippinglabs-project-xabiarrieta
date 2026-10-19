using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ProjectTimeline.BusinessLogic.Agents.AgentModels;
using ProjectTimeline.DataAccessLogic;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class TimelineFactSemanticVerifierTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(reply(request)); }
    }

    private static HttpResponseMessage Completion(string content, string finish = "stop", string? refusal = null) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new {
            id = "offline", model = "gpt-4o-mini", created = 0,
            choices = new[] { new { index = 0, finish_reason = finish, message = new { role = "assistant", content, refusal } } }
        }), Encoding.UTF8, "application/json")
    };

    private static readonly TimelineFactExtraction Source = new("Delivery", "Alice ordered 24 sensors on 12 March 2026. Bob did not approve delivery.");
    private static readonly TimelineFactCandidate[] Candidates = [
        new("Alice ordered two dozen sensors on 12 March 2026.", "alice", "Person", "Alice placed an order for 24 sensors on March 12, 2026."),
        new("Bob withheld delivery approval.", "bob", "Person", "Bob did not authorize delivery.")
    ];

    private static CustomOpenAIClient Client(Func<HttpRequestMessage, HttpResponseMessage> reply) =>
        new("fake-offline-credential", new HttpClient(new Handler(reply)));

    private static JsonObject Valid(TimelineFactExtraction? source = null)
    {
        var id = TimelineFactSourceEvidence.Create(source ?? Source).Last().Id;
        return JsonSerializer.SerializeToNode(new { assessments = new[] {
            new { candidateIndex = 0, supported = true, supportScore = 0.0, issues = Array.Empty<string>(), evidenceIds = new[] { id } },
            new { candidateIndex = 1, supported = true, supportScore = 1.0, issues = Array.Empty<string>(), evidenceIds = new[] { id } }
        } })!.AsObject();
    }

    [Fact]
    public async Task Supported_paraphrase_gets_independent_assessment_and_original_evidence_without_score_threshold()
    {
        var source = new TimelineFactExtraction("Delivery", "Alice ordered 24 sensors on 12 March 2026.");
        var calls = 0;
        var client = new CustomOpenAIClient("fake-offline-credential", new HttpClient(new Handler(request =>
        {
            calls++;
            if (calls == 1) return Completion("""
                {"claims":[{"claim":"Alice ordered two dozen sensors on 12 March 2026.","entityKey":"alice","entityType":"Person","sourceQuote":"Alice placed an order for 24 sensors on March 12, 2026."}]}
                """);
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            using var input = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            Assert.Equal(source.Content, input.RootElement.GetProperty("source").GetProperty("content").GetString());
            var id = input.RootElement.GetProperty("sourceSpans").EnumerateArray().Single(x => x.GetProperty("source").GetString() == "Content").GetProperty("id").GetString();
            return Completion(JsonSerializer.Serialize(new { assessments = new[] { new { candidateIndex = 0, supported = true,
                supportScore = 0.0, issues = Array.Empty<string>(), evidenceIds = new[] { id } } } }));
        })));
        var provider = new TimelineFactOpenAIProvider(new TimelineFactExtractionAgent(client), new TimelineFactSemanticVerifierAgent(client), client);
        var result = Assert.Single(await provider.ExtractAsync(source, default));
        Assert.Equal(2, calls);
        Assert.Equal(source.Content, result.SourceQuote);
        Assert.Equal("Alice ordered two dozen sensors on 12 March 2026.", result.Claim);
        Assert.Equal("alice", result.EntityKey);
    }

    [Theory]
    [InlineData("Numeric", "Alice ordered 25 sensors on 12 March 2026.")]
    [InlineData("Date", "Alice ordered 24 sensors on 13 March 2026.")]
    [InlineData("Actor", "Bob ordered 24 sensors on 12 March 2026.")]
    [InlineData("Negation", "Bob approved delivery.")]
    [InlineData("Unsupported", "Alice ordered 24 sensors with a lifetime warranty.")]
    public async Task High_score_cannot_override_a_blocker_or_negative_assessment(string issue, string claim)
    {
        // The fake supplies the contradiction finding; this does not test real model reasoning.
        var response = Valid();
        var first = response["assessments"]![0]!;
        first["supportScore"] = 1.0;
        first["issues"] = new JsonArray(issue);
        var input = new[] { Candidates[0] with { Claim = claim }, Candidates[1] };
        var error = await Assert.ThrowsAsync<TimelineFactProviderException>(() => new TimelineFactSemanticVerifierAgent(
            Client(_ => Completion(response.ToJsonString()))).VerifyAsync(Source, input, default));
        Assert.Equal("SemanticUnsupported", error.DiagnosticCode);
        first["issues"] = new JsonArray();
        first["supported"] = false;
        error = await Assert.ThrowsAsync<TimelineFactProviderException>(() => new TimelineFactSemanticVerifierAgent(
            Client(_ => Completion(response.ToJsonString()))).VerifyAsync(Source, input, default));
        Assert.Equal("SemanticUnsupported", error.DiagnosticCode);
        Assert.DoesNotContain(claim, error.ToString());
    }

    [Theory]
    [InlineData("missing-index")]
    [InlineData("duplicate-index")]
    [InlineData("negative-index")]
    [InlineData("wrong-index")]
    [InlineData("wrong-count")]
    [InlineData("null-item")]
    [InlineData("missing-supported")]
    [InlineData("missing-score")]
    [InlineData("missing-issues")]
    [InlineData("missing-evidence")]
    [InlineData("unknown-field")]
    [InlineData("wrong-case-field")]
    [InlineData("root-field")]
    [InlineData("unknown-flag")]
    [InlineData("numeric-flag")]
    [InlineData("lowercase-flag")]
    [InlineData("duplicate-flag")]
    [InlineData("null-issues")]
    [InlineData("null-evidence")]
    [InlineData("low-score")]
    [InlineData("high-score")]
    [InlineData("string-score")]
    public async Task Malformed_assessments_fail_closed(string mutation)
    {
        var response = Valid();
        var list = response["assessments"]!.AsArray();
        var first = list[0]!.AsObject();
        switch (mutation)
        {
            case "missing-index": first.Remove("candidateIndex"); break;
            case "duplicate-index": list[1]!["candidateIndex"] = 0; break;
            case "negative-index": first["candidateIndex"] = -1; break;
            case "wrong-index": first["candidateIndex"] = 2; break;
            case "wrong-count": list.RemoveAt(1); break;
            case "null-item": list[0] = null; break;
            case "missing-supported": first.Remove("supported"); break;
            case "missing-score": first.Remove("supportScore"); break;
            case "missing-issues": first.Remove("issues"); break;
            case "missing-evidence": first.Remove("evidenceIds"); break;
            case "unknown-field": first["PRIVATE"] = "PRIVATE model text"; break;
            case "wrong-case-field": first.Remove("supported"); first["Supported"] = true; break;
            case "root-field": response["PRIVATE"] = true; break;
            case "unknown-flag": first["issues"] = new JsonArray("PRIVATE"); break;
            case "numeric-flag": first["issues"] = new JsonArray(0); break;
            case "lowercase-flag": first["issues"] = new JsonArray("numeric"); break;
            case "duplicate-flag": first["issues"] = new JsonArray("Numeric", "Numeric"); break;
            case "null-issues": first["issues"] = null; break;
            case "null-evidence": first["evidenceIds"] = null; break;
            case "low-score": first["supportScore"] = -0.01; break;
            case "high-score": first["supportScore"] = 1.01; break;
            case "string-score": first["supportScore"] = "NaN"; break;
        }
        var error = await Assert.ThrowsAsync<TimelineFactProviderException>(() => new TimelineFactSemanticVerifierAgent(
            Client(_ => Completion(response.ToJsonString()))).VerifyAsync(Source, Candidates, default));
        Assert.Equal("MalformedAssessment", error.DiagnosticCode);
        Assert.DoesNotContain("PRIVATE", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e309")]
    [InlineData("null")]
    public async Task Nonfinite_or_null_scores_are_rejected(string score)
    {
        var text = Valid().ToJsonString().Replace("\"supportScore\":0", "\"supportScore\":" + score);
        var error = await Assert.ThrowsAsync<TimelineFactProviderException>(() => new TimelineFactSemanticVerifierAgent(
            Client(_ => Completion(text))).VerifyAsync(Source, Candidates, default));
        Assert.Equal("MalformedAssessment", error.DiagnosticCode);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("mixed-fields")]
    [InlineData("other-source")]
    [InlineData("null-id")]
    [InlineData("too-many")]
    [InlineData("altered-offset")]
    public async Task Invalid_evidence_ids_cannot_become_literal_evidence(string mutation)
    {
        var response = Valid();
        var spans = TimelineFactSourceEvidence.Create(Source);
        var ids = mutation switch {
            "empty" => new JsonArray(),
            "unknown" => new JsonArray("PRIVATE forged source text"),
            "duplicate" => new JsonArray(spans[1].Id, spans[1].Id),
            "mixed-fields" => new JsonArray(spans[0].Id, spans[1].Id),
            "other-source" => new JsonArray(TimelineFactSourceEvidence.Create(Source with { Content = "Different source" })[1].Id),
            "null-id" => new JsonArray((JsonNode?)null),
            "too-many" => new JsonArray(Enumerable.Repeat(spans[1].Id, 82).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            _ => new JsonArray(spans[1].Id + "1")
        };
        response["assessments"]![0]!["evidenceIds"] = ids;
        var error = await Assert.ThrowsAsync<TimelineFactProviderException>(() => new TimelineFactSemanticVerifierAgent(
            Client(_ => Completion(response.ToJsonString()))).VerifyAsync(Source, Candidates, default));
        Assert.Equal("InvalidEvidence", error.DiagnosticCode);
        Assert.DoesNotContain("PRIVATE", error.ToString());
    }

    [Theory]
    [InlineData("{not-json", "stop", null, "MalformedAssessment")]
    [InlineData("{\"assessments\":[]}", "length", null, "ResponseTruncated")]
    [InlineData("{}", "content_filter", null, "ResponseRefused")]
    [InlineData("{}", "stop", "PRIVATE refusal", "ResponseRefused")]
    [InlineData("{}", "tool_calls", null, "ResponseEnvelope")]
    [InlineData("{\"assessments\":[],\"assessments\":[]}", "stop", null, "MalformedAssessment")]
    public async Task Verifier_transport_and_schema_failures_are_safe(string text, string finish, string? refusal, string diagnostic)
    {
        var error = await Assert.ThrowsAsync<TimelineFactProviderException>(() => new TimelineFactSemanticVerifierAgent(
            Client(_ => Completion(text, finish, refusal))).VerifyAsync(Source, Candidates, default));
        Assert.Equal(diagnostic, error.DiagnosticCode);
        Assert.DoesNotContain("PRIVATE", error.ToString());
    }

    [Fact]
    public async Task Typed_result_is_transient_and_schema_and_prompt_cover_full_untrusted_batch()
    {
        var client = Client(request =>
        {
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var root = body.RootElement;
            var prompt = root.GetProperty("messages")[0].GetProperty("content").GetString()!;
            foreach (var phrase in new[] { "entire claim", "approximate", "untrusted data", "not instructions", "numbers", "dates", "actors",
                "negation", "not merely", "Never drop", "calibrated", "no numeric acceptance threshold", "entire original" })
                Assert.Contains(phrase, prompt, StringComparison.OrdinalIgnoreCase);
            using var input = JsonDocument.Parse(root.GetProperty("messages")[1].GetProperty("content").GetString()!);
            Assert.Equal(Source, input.RootElement.GetProperty("source").Deserialize<TimelineFactExtraction>(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var indexed = input.RootElement.GetProperty("candidates").EnumerateArray().ToArray();
            Assert.Equal(2, indexed.Length);
            for (var i = 0; i < Candidates.Length; i++)
            {
                Assert.Equal(i, indexed[i].GetProperty("candidateIndex").GetInt32());
                Assert.Equal(Candidates[i], indexed[i].GetProperty("candidate").Deserialize<TimelineFactCandidate>(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }
            var contract = root.GetProperty("response_format").GetProperty("json_schema");
            Assert.True(contract.GetProperty("strict").GetBoolean());
            var item = contract.GetProperty("schema").GetProperty("properties").GetProperty("assessments").GetProperty("items");
            Assert.False(item.GetProperty("additionalProperties").GetBoolean());
            Assert.Equal(new[] { "candidateIndex", "supported", "supportScore", "issues", "evidenceIds" }, item.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
            Assert.Equal(new[] { "Numeric", "Date", "Actor", "Negation", "Unsupported" }, item.GetProperty("properties").GetProperty("issues").GetProperty("items").GetProperty("enum").EnumerateArray().Select(x => x.GetString()));
            Assert.Equal(12000, root.GetProperty("max_completion_tokens").GetInt32());
            var result = Valid();
            var list = result["assessments"]!.AsArray();
            var first = list[0]!.DeepClone(); list[0] = list[1]!.DeepClone(); list[1] = first;
            return Completion(result.ToJsonString());
        });
        var typed = await new TimelineFactSemanticVerifierAgent(client).VerifyAsync(Source, Candidates, default);
        Assert.IsType<TimelineFactSemanticVerificationResult>(typed);
        Assert.Equal(new[] { 1.0, 0.0 }, typed.Assessments.Select(x => x.SupportScore));
        using var db = new ProjectTimelineDbContext(new DbContextOptionsBuilder<ProjectTimelineDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Assert.All(db.Model.GetEntityTypes(), entity => Assert.DoesNotContain(entity.GetProperties(), p => p.Name.Contains("SupportScore") || p.Name.Contains("Verified")));
        Assert.DoesNotContain(typeof(TimelineFactCandidate).GetProperties(), p => p.Name.Contains("Score"));
    }

    [Fact]
    public async Task Empty_cancelled_and_oversized_input_do_not_send_verifier_requests()
    {
        var calls = 0;
        var client = Client(_ => { calls++; return Completion("{}"); });
        var verifier = new TimelineFactSemanticVerifierAgent(client);
        Assert.Empty((await verifier.VerifyAsync(Source, [], default)).Assessments);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verifier.VerifyAsync(Source, Candidates, new CancellationToken(true)));
        await Assert.ThrowsAsync<TimelineFactProviderException>(() => verifier.VerifyAsync(Source with { Content = new string('x', 20001) }, Candidates, default));
        // Per-field shape bounds alone do not authorize an unbounded aggregate verifier request.
        var huge = Enumerable.Range(0, 199).Select(i => Candidates[0] with { Claim = i + new string('x', 3900), SourceQuote = new string('q', 20000) }).ToArray();
        var error = await Assert.ThrowsAsync<TimelineFactProviderException>(() => verifier.VerifyAsync(Source, huge, default));
        Assert.Equal("InputLimit", error.DiagnosticCode);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Cancellation_during_second_call_does_not_return_an_extraction_prefix()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var client = Client(_ =>
        {
            calls++;
            if (calls == 1) return Completion(JsonSerializer.Serialize(new { claims = Candidates.Select(c => new { c.Claim, c.EntityKey, c.EntityType, c.SourceQuote }) }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            cancellation.Cancel();
            return Completion(Valid().ToJsonString());
        });
        var provider = new TimelineFactOpenAIProvider(new(client), new(client), client);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ExtractAsync(Source, cancellation.Token));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Source_spans_are_lossless_stable_bounded_and_do_not_split_unicode_or_fields()
    {
        var source = new TimelineFactExtraction("Title 😀", new string('x', 255) + "😀\r\n" + new string('z', 19741));
        var spans = TimelineFactSourceEvidence.Create(source);
        Assert.Equal(spans, TimelineFactSourceEvidence.Create(source));
        Assert.InRange(spans.Count, 1, 81);
        foreach (var field in new[] { "Title", "Content" })
        {
            var original = field == "Title" ? source.Title : source.Content;
            var selected = spans.Where(x => x.Source == field).ToArray();
            Assert.Equal(original, string.Concat(selected.Select(x => x.Text)));
            var position = 0;
            foreach (var span in selected)
            {
                Assert.Equal(position, span.Start); Assert.InRange(span.Length, 1, 256);
                Assert.Equal(original.Substring(span.Start, span.Length), span.Text);
                Assert.False(char.IsHighSurrogate(span.Text[^1]));
                Assert.False(char.IsLowSurrogate(span.Text[0]));
                position += span.Length;
            }
        }
        Assert.Empty(TimelineFactSourceEvidence.Create(new("", "")));
        var unusual = TimelineFactSourceEvidence.Create(new("\ud800", ""));
        Assert.Equal("\ud800", Assert.Single(unusual).Text);
        Assert.NotEqual(unusual[0].Id, TimelineFactSourceEvidence.Create(new("\ud801", ""))[0].Id);
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("ar-SA");
            Assert.Equal(spans, TimelineFactSourceEvidence.Create(source));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }

    [Fact]
    public async Task Raw_extractor_admits_approximate_citation_for_verifier_without_early_literal_rejection()
    {
        var calls = 0;
        var client = Client(_ =>
        {
            calls++;
            return Completion(JsonSerializer.Serialize(new { claims = Candidates.Select(c => new { c.Claim, c.EntityKey, c.EntityType, c.SourceQuote }) }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        });
        var result = await new TimelineFactExtractionAgent(client).ExtractAsync(Source, default);
        Assert.Equal(Candidates, result);
        Assert.Equal(1, calls);
        Assert.All(result, candidate => Assert.DoesNotContain(candidate.SourceQuote, Source.Content, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("sourcekind")]
    [InlineData("shape")]
    public async Task Verifier_cannot_admit_invalid_candidate_shape_or_sourcekind(string mutation)
    {
        var input = mutation switch {
            "duplicate" => new[] { Candidates[0], Candidates[0] },
            "sourcekind" => new[] { Candidates[0] with { SourceKind = "Clarification" } },
            _ => new[] { Candidates[0] with { EntityKey = "" } }
        };
        var calls = 0;
        var client = Client(_ => { calls++; return Completion(Valid().ToJsonString()); });
        await Assert.ThrowsAsync<TimelineFactProviderException>(() => new TimelineFactSemanticVerifierAgent(client).VerifyAsync(Source, input, default));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Empty_extraction_makes_only_one_call_and_never_invokes_verifier()
    {
        var calls = 0;
        var client = Client(_ => { calls++; return Completion("{\"claims\":[]}"); });
        Assert.Empty(await new TimelineFactOpenAIProvider(new(client), new(client), client).ExtractAsync(Source, default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Title_only_evidence_is_reconstructed_from_title_without_content_concatenation()
    {
        var id = TimelineFactSourceEvidence.Create(Source)[0].Id;
        var calls = 0;
        var client = Client(_ => ++calls == 1
            ? Completion("{\"claims\":[{\"claim\":\"Delivery\",\"entityKey\":\"delivery\",\"entityType\":\"Thing\",\"sourceQuote\":\"Delivery title\"}]}")
            : Completion(JsonSerializer.Serialize(new { assessments = new[] { new { candidateIndex = 0, supported = true, supportScore = 0.0,
                issues = Array.Empty<string>(), evidenceIds = new[] { id } } } })));
        Assert.Equal(Source.Title, Assert.Single(await new TimelineFactOpenAIProvider(new(client), new(client), client).ExtractAsync(Source, default)).SourceQuote);
    }

    [Theory]
    [InlineData("duplicate-property")]
    [InlineData("oversized-response")]
    [InlineData("bad-envelope")]
    public async Task Verifier_rejects_duplicate_properties_large_output_and_missing_raw_envelope(string mutation)
    {
        var text = Valid().ToJsonString();
        if (mutation == "duplicate-property") text = text.Replace("\"supported\":true", "\"supported\":true,\"supported\":true");
        if (mutation == "oversized-response") text = new string('x', 524289);
        var client = Client(_ => mutation == "bad-envelope"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[]}", Encoding.UTF8, "application/json") }
            : Completion(text));
        var error = await Assert.ThrowsAsync<TimelineFactProviderException>(() => new TimelineFactSemanticVerifierAgent(client).VerifyAsync(Source, Candidates, default));
        Assert.Equal(mutation switch { "duplicate-property" => "MalformedAssessment", "oversized-response" => "ResponseLimit", _ => "ResponseEnvelope" }, error.DiagnosticCode);
    }

    [Fact]
    public async Task Multiple_selected_spans_produce_contiguous_original_envelope_not_concatenation()
    {
        var source = new TimelineFactExtraction("Unselected title", new string('a', 256) + new string('b', 256) + "tail");
        var spans = TimelineFactSourceEvidence.Create(source).Where(x => x.Source == "Content").ToArray();
        var calls = 0;
        var client = Client(_ => ++calls == 1
            ? Completion("{\"claims\":[{\"claim\":\"Whole claim\",\"entityKey\":\"x\",\"entityType\":\"Thing\",\"sourceQuote\":\"Approximate citation\"}]}")
            : Completion(JsonSerializer.Serialize(new { assessments = new[] { new { candidateIndex = 0, supported = true, supportScore = 0.01,
                issues = Array.Empty<string>(), evidenceIds = new[] { spans[2].Id, spans[0].Id } } } })));
        var result = Assert.Single(await new TimelineFactOpenAIProvider(new(client), new(client), client).ExtractAsync(source, default));
        Assert.Equal(source.Content, result.SourceQuote);
        Assert.Contains(new string('b', 256), result.SourceQuote);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_provider_path_persists_one_grounded_batch_or_no_facts_and_never_executes_embeddings(bool rejectSecond)
    {
        var name = Guid.NewGuid().ToString(); var root = new InMemoryDatabaseRoot();
        ProjectTimelineDbContext Db() => new(new DbContextOptionsBuilder<ProjectTimelineDbContext>().UseInMemoryDatabase(name, root).Options);
        var actor = new User { Email = "semantic@example.test", DisplayName = "Author", PasswordHash = "test" };
        var project = new Project { Code = "P", Name = "Project", OwnerId = actor.Id };
        var timeline = new Timeline { Code = "T", Name = "Timeline", ProjectId = project.Id };
        var entry = new TimelineEntry { TimelineId = timeline.Id, AuthorId = actor.Id, EntryNumber = 1, Title = Source.Title, Content = Source.Content };
        var job = TimelineFactProcessing.NewExtractionJob(entry);
        await using (var seed = Db()) { seed.AddRange(actor, project, timeline, entry, job); await seed.SaveChangesAsync(); }
        var calls = 0;
        var client = Client(request =>
        {
            Assert.EndsWith("/chat/completions", request.RequestUri!.AbsolutePath); // An embedding call fails this fixture.
            calls++;
            if (calls == 1) return Completion(JsonSerializer.Serialize(new { claims = Candidates.Select(c => new { c.Claim, c.EntityKey, c.EntityType, c.SourceQuote }) }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            Assert.Equal(2, calls);
            var result = Valid();
            if (rejectSecond) result["assessments"]![1]!["supported"] = false;
            return Completion(result.ToJsonString());
        });
        var diagnostics = new List<string>();
        var provider = new TimelineFactOpenAIProvider(new(client), new(client), client);
        Assert.Equal(rejectSecond ? "Retry" : "Completed", await new TimelineFactProcessing(Db, provider, validationDiagnostic: diagnostics.Add).ProcessAsync(job.Id, "offline"));
        Assert.Equal(2, calls);
        await using var db = Db();
        Assert.Empty(await db.TimelineFactProposals.ToListAsync());
        Assert.Empty(await db.TimelineFactAdditions.ToListAsync());
        var facts = await db.TimelineFacts.ToListAsync();
        var savedJob = await db.TimelineFactJobs.SingleAsync(x => x.Id == job.Id);
        if (rejectSecond)
        {
            Assert.Empty(facts); Assert.Single(await db.TimelineFactJobs.ToListAsync());
            Assert.Null(savedJob.ExtractionResultHash); Assert.Equal(new[] { "SemanticUnsupported" }, diagnostics);
        }
        else
        {
            Assert.Equal(2, facts.Count);
            Assert.All(facts, fact => {
                Assert.Equal(Source.Content, fact.SourceQuote); Assert.Null(fact.CreationReviewId);
                Assert.Equal(job.Id, fact.ExtractionJobId); Assert.Equal(job.SourceFingerprint, fact.SourceFingerprint);
                Assert.Equal(TimelineFactEvidence.CandidateKey(fact), fact.CandidateKey); Assert.Null(fact.EmbeddedAtUtc);
            });
            Assert.Equal(TimelineFactEvidence.ResultHash(facts), savedJob.ExtractionResultHash);
            var ungrounded = facts.Select(f => new TimelineFact { Claim = f.Claim, EntityKey = f.EntityKey, EntityType = f.EntityType,
                SourceQuote = Candidates.Single(c => c.Claim == f.Claim).SourceQuote, SourceKind = f.SourceKind }).ToArray();
            Assert.NotEqual(TimelineFactEvidence.ResultHash(ungrounded), savedJob.ExtractionResultHash);
            Assert.Equal(2, await db.TimelineFactJobs.CountAsync(x => x.Kind == "Embedding" && x.State == "Pending"));
        }
    }
}

