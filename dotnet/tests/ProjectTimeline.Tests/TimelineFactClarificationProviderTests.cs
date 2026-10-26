using System.Net;
using System.Text;
using System.Text.Json;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.DataAccessLogic;
using System.Text.Json.Nodes;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class TimelineFactClarificationProviderTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(reply(request)); }
    }
    private static HttpResponseMessage Completion(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new {
            id = "offline", model = "gpt-4o-mini", created = 0,
            choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = text } } }
        }), Encoding.UTF8, "application/json")
    };
    private static CustomOpenAIClient Client(Func<HttpRequestMessage, HttpResponseMessage> reply) =>
        new("fake-offline-credential", new HttpClient(new Handler(reply)));

    [Fact]
    public async Task Entity_metadata_is_explicitly_grounded_not_just_claim_text()
    {
        var client = Client(request =>
        {
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var instructions = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            Assert.Contains("EntityKey and EntityType", instructions, StringComparison.Ordinal);
            Assert.Contains("supported", instructions, StringComparison.Ordinal);
            return Completion("{\"claims\":[]}");
        });
        await new TimelineFactExtractionAgent(client).ExtractAsync(new("Delivery", "Alice ordered sensors."), default);
    }

    private static (TimelineEntry Entry, TimelineEntryExtractionInput Input, List<TimelineEntryAnswer> Answers) Bound()
    {
        var entry = new TimelineEntry { Id = Guid.NewGuid(), TimelineId = Guid.NewGuid(), EntryNumber = 17,
            AuthorId = Guid.NewGuid(), Title = "Delivery", Content = "Who ordered the sensors?" };
        var answers = new List<TimelineEntryAnswer> { new(Guid.NewGuid(), entry.Id, Guid.NewGuid(),
            TimelineFactEvidence.Fingerprint(entry), "actor", "Who ordered the sensors?", "Alice ordered 24 sensors.",
            Guid.NewGuid(), DateTime.UtcNow) };
        var job = new TimelineFactJob { Id = Guid.NewGuid(), EntryId = entry.Id, Kind = "Extraction",
            SourceFingerprint = TimelineFactEvidence.Fingerprint(entry) };
        return (entry, TimelineEntryClarificationEvidence.BindToJob(job, entry, answers), answers);
    }
    private static JsonObject Assessment(params string[] ids) => JsonSerializer.SerializeToNode(new { assessments = new[] {
        new { candidateIndex = 0, supported = true, supportScore = 1.0, issues = Array.Empty<string>(), evidenceIds = ids }
    } })!.AsObject();
    private const string Claim = """
        {"claims":[{"claim":"Alice ordered 24 sensors.","entityKey":"alice","entityType":"Person","sourceQuote":"Alice ordered sensors."}]}
        """;
    private static TimelineFactOpenAIProvider Provider(CustomOpenAIClient client) =>
        new(new TimelineFactExtractionAgent(client), new TimelineFactSemanticVerifierAgent(client), client);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Repeated_freeze_preserves_full_immutable_source_identity_with_or_without_answers(bool answered)
    {
        var (entry, input, _) = Bound();
        if (!answered) input = input with { Answers = Array.Empty<TimelineEntryAnswer>(),
            EffectiveFingerprint = TimelineEntryClarificationEvidence.EffectiveFingerprint(entry, []) };
        var source = new TimelineFactExtraction(entry, input);
        var original = TimelineFactEvidence.Fingerprint(entry);
        var effective = input.EffectiveFingerprint;
        // The source owns values, not the mutable ORM entity.
        entry.TimelineId = Guid.NewGuid(); entry.EntryNumber++; entry.AuthorId = Guid.NewGuid();
        entry.Title = "Changed"; entry.Content = "Changed";
        for (var i = 0; i < 3; i++)
        {
            source = source.Freeze();
            Assert.Equal(original, source.Input!.SourceFingerprint);
            Assert.Equal(effective, source.Input.EffectiveFingerprint);
            Assert.Equal(answered ? 1 : 0, source.Input.Answers.Count);
            Assert.Equal("Delivery", source.Title);
            Assert.Equal("Who ordered the sensors?", source.Content);
            Assert.Equal(input.JobId, source.Input.JobId);
        }
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(source, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.False(json.RootElement.TryGetProperty("timelineId", out _));
        Assert.False(json.RootElement.TryGetProperty("entryNumber", out _));
        Assert.False(json.RootElement.TryGetProperty("authorId", out _));
    }

    [Fact]
    public async Task Bound_empty_answer_input_reaches_both_agents_with_identical_valid_context()
    {
        var (entry, input, _) = Bound();
        input = input with { Answers = Array.Empty<TimelineEntryAnswer>(),
            EffectiveFingerprint = TimelineEntryClarificationEvidence.EffectiveFingerprint(entry, []) };
        var source = new TimelineFactExtraction(entry, input);
        string? context = null, spans = null;
        var calls = 0;
        var provider = Provider(Client(request =>
        {
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            using var payload = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            var root = payload.RootElement;
            if (++calls == 1)
            {
                context = root.GetProperty("source").GetRawText();
                spans = root.GetProperty("sourceSpans").GetRawText();
                return Completion(Claim);
            }
            Assert.Equal(context, root.GetProperty("source").GetRawText());
            Assert.Equal(spans, root.GetProperty("sourceSpans").GetRawText());
            return Completion(Assessment(TimelineFactSourceEvidence.Create(source).Last().Id).ToJsonString());
        }));
        var result = await provider.ExtractOutcomeAsync(source, default);
        Assert.Equal(2, calls);
        Assert.Equal("Entry", Assert.Single(result.Candidates).SourceKind);
        Assert.Equal(entry.Content, result.Candidates[0].SourceQuote);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_agents_receive_identical_frozen_QA_and_ids_and_return_literal_answer_provenance(bool mixed)
    {
        var (entry, input, answers) = Bound();
        // Deliberately use a mutable caller list, not just C1's already read-only snapshot.
        var source = new TimelineFactExtraction(entry, input with { Answers = answers });
        answers.Clear();
        string? extractionSource = null, extractionSpans = null;
        var calls = 0;
        var client = Client(request =>
        {
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            using var payload = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            var root = payload.RootElement;
            var prompt = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            Assert.Contains("EntityKey and EntityType", prompt);
            Assert.Contains("never", prompt, StringComparison.OrdinalIgnoreCase);
            var supplied = root.GetProperty("source");
            Assert.Equal(entry.Content, supplied.GetProperty("content").GetString());
            var actual = Assert.Single(supplied.GetProperty("input").GetProperty("answers").EnumerateArray());
            Assert.Equal(input.Answers[0].Question, actual.GetProperty("question").GetString());
            Assert.Equal(input.Answers[0].Answer, actual.GetProperty("answer").GetString());
            var spans = root.GetProperty("sourceSpans").EnumerateArray().ToArray();
            Assert.DoesNotContain(spans, x => x.GetProperty("source").GetString() == "Question");
            calls++;
            if (calls == 1)
            {
                extractionSource = supplied.GetRawText(); extractionSpans = root.GetProperty("sourceSpans").GetRawText();
                return Completion(Claim);
            }
            Assert.Equal(extractionSource, supplied.GetRawText());
            Assert.Equal(extractionSpans, root.GetProperty("sourceSpans").GetRawText());
            var answerId = spans.Single(x => x.GetProperty("sourceKind").GetString() == "Clarification").GetProperty("id").GetString()!;
            var originalId = spans.First().GetProperty("id").GetString()!;
            return Completion(Assessment(mixed ? new[] { originalId, answerId } : new[] { answerId }).ToJsonString());
        });
        var outcome = await Provider(client).ExtractOutcomeAsync(source, default);
        var result = Assert.Single(outcome.Candidates);
        Assert.Equal("Clarification", result.SourceKind);
        Assert.Equal(input.Answers[0].Answer, result.SourceQuote);
        Assert.Equal("alice", result.EntityKey);
        var citation = Assert.Single(outcome.Evidence);
        Assert.Equal(input.Answers[0].Id, citation.Spans.Single(x => x.Id == citation.PrimarySpanId).AnswerId);
        Assert.Equal(mixed ? 2 : 1, citation.Spans.Count);
        Assert.Equal(2, calls);
        Assert.Empty(outcome.Questions);
        Assert.Equal("Who ordered the sensors?", entry.Content);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("foreign-entry")]
    [InlineData("foreign-job")]
    [InlineData("changed-answer")]
    [InlineData("question")]
    public async Task Unknown_or_foreign_or_modified_answer_or_question_spans_fail(string mutation)
    {
        var (entry, input, _) = Bound();
        var source = new TimelineFactExtraction(entry, input);
        TimelineFactExtraction other;
        if (mutation == "foreign-entry") { var b = Bound(); other = new(b.Entry, b.Input); }
        else if (mutation == "foreign-job") other = new(entry, input with { JobId = Guid.NewGuid() });
        else if (mutation == "changed-answer")
        {
            var changed = input.Answers.Select(x => x with { Answer = "Alice ordered 25 sensors." }).ToArray();
            other = new(entry, input with { Answers = changed,
                EffectiveFingerprint = TimelineEntryClarificationEvidence.EffectiveFingerprint(entry, changed) });
        }
        else other = source;
        var badId = mutation is "unknown" or "question" ? mutation : TimelineFactSourceEvidence.Create(other).Last().Id;
        var calls = 0;
        var error = await Assert.ThrowsAsync<TimelineFactProviderException>(() => Provider(Client(_ =>
            Completion(++calls == 1 ? Claim : Assessment(badId).ToJsonString()))).ExtractOutcomeAsync(source, default));
        Assert.Equal("InvalidEvidence", error.DiagnosticCode);
    }

    [Theory]
    [InlineData("entity")]
    [InlineData("claim")]
    [InlineData("quote")]
    public async Task Unsupported_entity_claim_or_rewritten_quote_is_failure_not_question(string detail)
    {
        var (entry, input, _) = Bound();
        var source = new TimelineFactExtraction(entry, input);
        var response = Assessment(TimelineFactSourceEvidence.Create(source).Last().Id);
        response["assessments"]![0]!["supported"] = false;
        response["assessments"]![0]!["issues"] = new JsonArray("Unsupported");
        var claim = JsonNode.Parse(Claim)!;
        claim["claims"]![0]![detail switch { "entity" => "entityKey", "claim" => "claim", _ => "sourceQuote" }] = "Invented unsupported information";
        var calls = 0;
        var error = await Assert.ThrowsAsync<TimelineFactProviderException>(() => Provider(Client(_ => Completion(
            ++calls == 1 ? claim.ToJsonString() : response.ToJsonString()))).ExtractOutcomeAsync(source, default));
        Assert.Equal("SemanticUnsupported", error.DiagnosticCode);
    }

    private static string Questions(TimelineFactExtraction source, string? claims = null) => JsonSerializer.Serialize(new {
        claims = JsonNode.Parse(claims ?? "{\"claims\":[]}")!["claims"],
        questions = new[] { new { key = "actor", text = "Who ordered the sensors?", reason = "AmbiguousReference",
            evidenceIds = new[] { TimelineFactSourceEvidence.Create(source).Last().Id } } }
    });

    [Fact]
    public async Task Valid_neutral_questions_have_typed_outcome_but_legacy_call_cannot_discard_them()
    {
        var source = new TimelineFactExtraction("Delivery", "Someone ordered sensors.");
        var provider = Provider(Client(_ => Completion(Questions(source))));
        var outcome = await provider.ExtractOutcomeAsync(source, default);
        Assert.Empty(outcome.Candidates);
        Assert.Equal(new TimelineEntryQuestion("actor", "Who ordered the sensors?"), Assert.Single(outcome.Questions));
        var legacy = await Assert.ThrowsAsync<TimelineFactClarificationRequiredException>(() => provider.ExtractAsync(source, default));
        Assert.Single(legacy.Outcome.Questions);
        await Assert.ThrowsAsync<TimelineFactClarificationRequiredException>(() =>
            new TimelineFactExtractionAgent(Client(_ => Completion(Questions(source)))).ExtractAsync(source, default));
    }

    [Theory]
    [InlineData("answer", "CandidateShape")]
    [InlineData("unknown-field", "CandidateShape")]
    [InlineData("null", "CandidateShape")]
    [InlineData("too-many", "InvalidQuestions")]
    [InlineData("duplicate", "InvalidQuestions")]
    [InlineData("reason", "InvalidQuestions")]
    [InlineData("statement", "InvalidQuestions")]
    [InlineData("long", "InvalidQuestions")]
    [InlineData("bad-citation", "InvalidEvidence")]
    [InlineData("malformed-claim", "CandidateShape")]
    public async Task Invalid_question_or_candidate_output_never_signals_clarification(string mutation, string diagnostic)
    {
        var source = new TimelineFactExtraction("Delivery", "Someone ordered sensors.");
        var root = JsonNode.Parse(Questions(source))!;
        var question = root["questions"]![0]!;
        switch (mutation)
        {
            case "answer": question["answer"] = "Alice"; break;
            case "unknown-field": question["suggestedFact"] = "Alice ordered sensors"; break;
            case "null": root["questions"] = null; break;
            case "too-many": root["questions"] = new JsonArray(Enumerable.Range(0, 21).Select(_ => question.DeepClone()).ToArray()); break;
            case "duplicate": root["questions"]!.AsArray().Add(question.DeepClone()); break;
            case "reason": question["reason"] = "ProviderError"; break;
            case "statement": question["text"] = "Alice ordered the sensors."; break;
            case "long": question["text"] = new string('x', 2000) + "?"; break;
            case "bad-citation": question["evidenceIds"] = new JsonArray("foreign"); break;
            case "malformed-claim": root["claims"] = new JsonArray(new JsonObject { ["claim"] = "bad" }); break;
        }
        var error = await Assert.ThrowsAsync<TimelineFactProviderException>(() => Provider(Client(_ => Completion(root.ToJsonString())))
            .ExtractOutcomeAsync(source, default));
        Assert.Equal(diagnostic, error.DiagnosticCode);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"claims\":[],\"questions\":[],\"unknown\":true}")]
    [InlineData("{\"claims\":[],\"questions\":[],\"questions\":[]}")]
    public async Task Malformed_or_duplicate_question_envelopes_remain_schema_failures(string json)
    {
        var error = await Assert.ThrowsAsync<TimelineFactProviderException>(() => Provider(Client(_ => Completion(json)))
            .ExtractOutcomeAsync(new("Delivery", "Someone ordered sensors."), default));
        Assert.Equal("InvalidResponse", error.Code);
        Assert.Equal("ResponseSchema", error.DiagnosticCode);
    }

    [Fact]
    public async Task Questions_do_not_bypass_semantic_validation_of_supplied_candidates()
    {
        var source = new TimelineFactExtraction("Delivery", "Someone ordered sensors.");
        var assessment = Assessment(TimelineFactSourceEvidence.Create(source).Last().Id);
        assessment["assessments"]![0]!["supported"] = false;
        var calls = 0;
        var error = await Assert.ThrowsAsync<TimelineFactProviderException>(() => Provider(Client(_ => Completion(++calls == 1
            ? Questions(source, Claim) : assessment.ToJsonString()))).ExtractOutcomeAsync(source, default));
        Assert.Equal("SemanticUnsupported", error.DiagnosticCode);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("hash")]
    [InlineData("source")]
    [InlineData("bounds")]
    [InlineData("null")]
    public void Invalid_snapshot_is_rejected_before_model_calls(string mutation)
    {
        var (entry, input, _) = Bound();
        if (mutation == "foreign") input = input with { Answers = input.Answers.Select(x => x with { EntryId = Guid.NewGuid() }).ToArray() };
        if (mutation == "hash") input = input with { EffectiveFingerprint = "wrong" };
        if (mutation == "source") entry.Content += "changed";
        if (mutation == "bounds") input = input with { Answers = input.Answers.Select(x => x with { Answer = new string('a', 4001) }).ToArray() };
        if (mutation == "null") input = input with { Answers = null! };
        Assert.Throws<TimelineFactProviderException>(() => new TimelineFactExtraction(entry, input));
    }

    private sealed class LegacyProvider : ITimelineFactProvider
    {
        public int Calls;
        public string ExtractionModel => "fake";
        public Task<IReadOnlyList<TimelineFactCandidate>> ExtractAsync(TimelineFactExtraction source, CancellationToken ct)
        { Calls++; return Task.FromResult<IReadOnlyList<TimelineFactCandidate>>([]); }
        public Task<float[]> EmbedAsync(string claim, CancellationToken ct) => throw new NotSupportedException();
    }
    [Fact]
    public async Task Legacy_provider_works_without_answers_but_cannot_silently_ignore_nonempty_input()
    {
        var legacy = new LegacyProvider();
        ITimelineFactProvider provider = legacy;
        Assert.Empty((await provider.ExtractOutcomeAsync(new("Title", "Original"), default)).Candidates);
        var (entry, input, _) = Bound();
        var error = await Assert.ThrowsAsync<TimelineFactProviderException>(() => provider.ExtractOutcomeAsync(new(entry, input), default));
        Assert.Equal("ClarificationInputUnsupported", error.DiagnosticCode);
        Assert.Equal(1, legacy.Calls);
    }

    [Fact]
    public async Task Original_only_evidence_remains_Entry_even_when_answers_exist()
    {
        var (entry, input, _) = Bound();
        var source = new TimelineFactExtraction(entry, input);
        var original = TimelineFactSourceEvidence.Create(source).First(x => x.Source == "Content");
        var calls = 0;
        var outcome = await Provider(Client(_ => Completion(++calls == 1 ? Claim : Assessment(original.Id).ToJsonString())))
            .ExtractOutcomeAsync(source, default);
        Assert.Equal("Entry", Assert.Single(outcome.Candidates).SourceKind);
        Assert.Equal(entry.Content, outcome.Candidates[0].SourceQuote);
        Assert.Null(Assert.Single(outcome.Evidence[0].Spans).AnswerId);
    }

    [Fact]
    public async Task Mixed_primary_citation_is_independent_of_model_order_and_retains_all_answer_identities()
    {
        var (entry, input, answers) = Bound();
        answers.Add(answers[0] with { Id = Guid.NewGuid(), QuestionKey = "quantity", Question = "How many sensors?", Answer = "24 sensors." });
        input = input with { Answers = answers, EffectiveFingerprint = TimelineEntryClarificationEvidence.EffectiveFingerprint(entry, answers) };
        var source = new TimelineFactExtraction(entry, input);
        var ids = TimelineFactSourceEvidence.Create(source).Select(x => x.Id).ToArray();
        async Task<TimelineFactExtractionOutcome> Run(string[] order)
        {
            var calls = 0;
            return await Provider(Client(_ => Completion(++calls == 1 ? Claim : Assessment(order).ToJsonString())))
                .ExtractOutcomeAsync(source, default);
        }
        var forward = await Run(ids);
        var reverse = await Run(ids.Reverse().ToArray());
        Assert.Equal(forward.Candidates[0], reverse.Candidates[0]);
        Assert.Equal(forward.Evidence[0].PrimarySpanId, reverse.Evidence[0].PrimarySpanId);
        Assert.Equal(2, forward.Evidence[0].Spans.Count(x => x.SourceKind == "Clarification"));
    }

    [Fact]
    public void Frozen_source_rejects_record_copy_that_changes_original_content()
    {
        var (entry, input, _) = Bound();
        var changed = new TimelineFactExtraction(entry, input) with { Content = "Altered original" };
        Assert.Throws<TimelineFactProviderException>(() => TimelineFactSourceEvidence.Create(changed));
    }

    [Fact]
    public async Task Bounds_admit_twenty_questions_and_maximum_actual_answer_without_splitting_surrogates()
    {
        var (entry, input, answers) = Bound();
        answers[0] = answers[0] with { Answer = new string('a', 255) + "😀" + new string('a', 3743) };
        input = input with { Answers = answers, EffectiveFingerprint = TimelineEntryClarificationEvidence.EffectiveFingerprint(entry, answers) };
        var source = new TimelineFactExtraction(entry, input);
        var spans = TimelineFactSourceEvidence.Create(source).Where(x => x.AnswerId is not null).ToArray();
        Assert.Equal(4000, string.Concat(spans.Select(x => x.Text)).Length);
        Assert.All(spans, x => { Assert.InRange(x.Length, 1, 256); Assert.False(char.IsHighSurrogate(x.Text[^1])); });
        var root = JsonNode.Parse(Questions(source))!;
        var question = root["questions"]![0]!;
        root["questions"] = new JsonArray(Enumerable.Range(0, 20).Select(i =>
        { var copy = question.DeepClone(); copy["key"] = $"q{i}"; return copy; }).ToArray());
        var outcome = await Provider(Client(_ => Completion(root.ToJsonString()))).ExtractOutcomeAsync(source, default);
        Assert.Equal(20, outcome.Questions.Count);
    }

    [Fact]
    public async Task Provider_and_cancellation_failures_are_not_questions()
    {
        var source = new TimelineFactExtraction("Delivery", "Someone ordered sensors.");
        var error = await Assert.ThrowsAsync<TimelineFactProviderException>(() => Provider(Client(_ => throw new HttpRequestException()))
            .ExtractOutcomeAsync(source, default));
        Assert.Equal("Unavailable", error.Code);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(Client(_ => throw new InvalidOperationException()))
            .ExtractOutcomeAsync(source, cancelled.Token));
    }
}

