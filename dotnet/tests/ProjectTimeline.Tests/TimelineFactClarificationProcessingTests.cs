using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.DataAccessLogic;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class TimelineFactClarificationProcessingTests
{
    private sealed class Fixture : ITimelineFactProvider
    {
        private readonly string database = Guid.NewGuid().ToString();
        private readonly Microsoft.EntityFrameworkCore.Storage.InMemoryDatabaseRoot root = new();
        public ProjectTimelineDbContext Db() => new(new DbContextOptionsBuilder<ProjectTimelineDbContext>().UseInMemoryDatabase(database, root).Options);
        public DbContext Raw()
        {
            using var db = Db();
            return new(new DbContextOptionsBuilder().UseInMemoryDatabase(database, root).UseModel(db.Model).Options);
        }
        public User Actor = new() { Email = "clarification-worker@example.test", DisplayName = "Writer", PasswordHash = "test" };
        public Project Project = new() { Code = "P", Name = "Project" };
        public Timeline Timeline = new() { Code = "T", Name = "Timeline" };
        public TimelineEntry Entry = null!;
        public Guid JobId;
        public int Calls;
        public TimelineFactExtraction? Seen;
        public Exception? Error;
        public Func<Task>? During = null;
        public TimelineFactExtractionOutcome Outcome = new([], [new("owner", "Who owns delivery?"), new("date", "When is delivery?")], []);
        public string ExtractionModel => "fake-outcome";
        public TimelineFactScope Scope => new(Project.Id, Timeline.Id, Entry.Id);
        public ITimelineFactProvider? Provider = null;
        public TimelineFactProcessing Worker => new(Db, Provider ?? this);
        public async Task Initialize()
        {
            await using var db = Db();
            Project.OwnerId = Actor.Id; Timeline.ProjectId = Project.Id;
            db.AddRange(Actor, Project, Timeline); await db.SaveChangesAsync();
            Entry = await new TimelineService(db).AddEntryAsync(Timeline.Id, Actor.Id, "Delivery", "Delivery is planned.", default);
            JobId = await db.TimelineFactJobs.Select(x => x.Id).SingleAsync();
        }
        public Task<IReadOnlyList<TimelineFactCandidate>> ExtractAsync(TimelineFactExtraction source, CancellationToken ct) =>
            throw new Exception("Must use the structured outcome API.");
        public async Task<TimelineFactExtractionOutcome> ExtractOutcomeAsync(TimelineFactExtraction source, CancellationToken ct)
        {
            Calls++; Seen = source.Freeze();
            if (During is not null) await During();
            if (Error is not null) throw Error;
            return Outcome;
        }
        public Task<float[]> EmbedAsync(string claim, CancellationToken ct) => throw new Exception("No effective facts.");
        public Task<string> Run(CancellationToken ct = default) => Worker.ProcessAsync(JobId, "worker", ct);
        public async Task Answer(int index, string answer)
        {
            await using var db = Db();
            var rows = await db.TimelineEntryClarifications.Where(x => x.GenerationId == JobId).OrderBy(x => x.Question).ToListAsync();
            await new TimelineEntryClarificationService(db).AnswerAsync(Scope, JobId, rows[index].Id, Actor.Id, false, answer);
        }
        public async Task Resume() => JobId = await Worker.RequestGenerationAsync(Scope, Actor.Id, false, true, JobId);
    }

    [Fact]
    public async Task Questions_pause_atomically_and_repeated_processing_is_free()
    {
        var f = new Fixture(); await f.Initialize();
        Assert.Equal("AwaitingClarification", await f.Run());
        Assert.Equal("AwaitingClarification", await f.Run());
        await using var db = f.Db();
        var job = await db.TimelineFactJobs.SingleAsync();
        Assert.Equal(1, f.Calls); Assert.Equal(1, job.Attempts);
        Assert.Null(job.LeaseOwner); Assert.Null(job.LeaseExpiresAtUtc);
        Assert.Equal(2, await db.TimelineEntryClarifications.CountAsync());
        Assert.Empty(await db.TimelineFactProposals.ToListAsync());
        Assert.Empty(await db.TimelineFacts.ToListAsync());
        Assert.NotNull(f.Seen!.Input);
    }

    [Fact]
    public async Task Partial_answers_cannot_resume_and_full_answers_carry_across_refresh_before_supersession()
    {
        var f = new Fixture(); await f.Initialize();
        Assert.Equal("AwaitingClarification", await f.Run());
        await f.Answer(0, "Friday");
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => f.Resume());
        Assert.Equal("AwaitingClarification", await f.Run());
        await f.Answer(1, "Alice");
        var original = f.JobId;
        await f.Resume();
        f.Outcome = new([], [], []);
        Assert.Equal("Completed", await f.Run());
        Assert.Equal(2, f.Seen!.Input!.Answers.Count);
        Assert.All(f.Seen.Input.Answers, x => Assert.Equal(original, x.GenerationId));
        var fingerprint = f.Seen.Input.EffectiveFingerprint;
        await f.Resume();
        Assert.Equal("Completed", await f.Run());
        Assert.Equal(fingerprint, f.Seen.Input.EffectiveFingerprint);
        Assert.Equal(2, f.Seen.Input.Answers.Count);
        await using var db = f.Db();
        Assert.Equal(f.Entry.Content, (await db.TimelineEntries.SingleAsync()).Content);
        Assert.Empty(await db.TimelineFacts.ToListAsync());
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(reply(request));
    }
    private static HttpResponseMessage Completion(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { id = "offline", model = "gpt-4o-mini", created = 0,
            choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = text } } } }), Encoding.UTF8, "application/json")
    };

    [Fact]
    public async Task Actual_processing_real_provider_passes_identical_persisted_answers_to_both_agents_and_retains_every_support_identity()
    {
        var f = new Fixture(); await f.Initialize(); Assert.Equal("AwaitingClarification", await f.Run());
        await f.Answer(0, "Friday"); await f.Answer(1, "Alice owns delivery."); await f.Resume();
        TimelineExtractionSnapshot snapshot;
        await using (var db = f.Db()) snapshot = await db.ReadExtractionInputAsync(await db.TimelineFactJobs.SingleAsync(x => x.Id == f.JobId), await db.TimelineEntries.SingleAsync());
        Assert.NotEqual(Guid.Empty, snapshot.Source.TimelineId); Assert.NotEqual(Guid.Empty, snapshot.Source.AuthorId); Assert.True(snapshot.Source.EntryNumber > 0);
        string? context = null, spans = null;
        var calls = 0;
        var client = new CustomOpenAIClient("fake-offline-credential", new HttpClient(new Handler(request =>
        {
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            using var payload = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            var source = payload.RootElement.GetProperty("source");
            Assert.Equal(snapshot.Input.EffectiveFingerprint, source.GetProperty("input").GetProperty("effectiveFingerprint").GetString());
            Assert.Equal(2, source.GetProperty("input").GetProperty("answers").GetArrayLength());
            Assert.Contains("EntityKey and EntityType", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!);
            if (++calls == 1)
            {
                context = source.GetRawText(); spans = payload.RootElement.GetProperty("sourceSpans").GetRawText();
                return Completion("{\"claims\":[{\"claim\":\"Alice owns delivery.\",\"entityKey\":\"alice\",\"entityType\":\"Person\",\"sourceQuote\":\"Alice owns delivery.\"}]}");
            }
            Assert.Equal(context, source.GetRawText()); Assert.Equal(spans, payload.RootElement.GetProperty("sourceSpans").GetRawText());
            var ids = payload.RootElement.GetProperty("sourceSpans").EnumerateArray().Select(x => x.GetProperty("id").GetString()!).ToArray();
            return Completion(JsonSerializer.Serialize(new { assessments = new[] { new { candidateIndex = 0, supported = true, supportScore = 1.0, issues = Array.Empty<string>(), evidenceIds = ids } } }));
        })));
        f.Provider = new TimelineFactOpenAIProvider(new TimelineFactExtractionAgent(client), new TimelineFactSemanticVerifierAgent(client), client);
        Assert.Equal("Completed", await f.Run()); Assert.Equal(2, calls);
        await using var verify = f.Db();
        var job = await verify.TimelineFactJobs.SingleAsync(x => x.Id == f.JobId);
        Assert.Equal(JsonSerializer.Serialize(snapshot), job.ExtractionInputJson);
        var support = Assert.Single(JsonSerializer.Deserialize<TimelineExtractionSupport[]>(job.ExtractionSupportJson!)!);
        Assert.Equal(4, support.Spans.Count); // Title, Content and both actual answers.
        Assert.Equal(2, support.Spans.Count(x => x.SourceKind == "Clarification"));
        var addition = await verify.TimelineFactAdditions.SingleAsync();
        Assert.Equal(addition.Id, support.AdditionId); Assert.Equal("Clarification", addition.SourceKind);
        Assert.Empty(await verify.TimelineFacts.ToListAsync()); Assert.Equal("Generated", (await verify.TimelineFactProposals.SingleAsync()).Status);
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => new TimelineFactService(verify).AcceptAsync(f.Scope, job.Id, f.Actor.Id, false));
    }

    [Fact]
    public async Task Reused_provider_keys_across_rounds_remain_unambiguous_without_dropping_answers()
    {
        var f = new Fixture(); await f.Initialize(); await f.Run();
        await f.Answer(0, "Friday"); await f.Answer(1, "Alice"); await f.Resume();
        Assert.Equal("AwaitingClarification", await f.Run());
        await f.Answer(0, "Next Friday"); await f.Answer(1, "Alice Smith"); await f.Resume();
        f.Outcome = new([], [], []); Assert.Equal("Completed", await f.Run());
        Assert.Equal(4, f.Seen!.Input!.Answers.Count);
        Assert.Equal(4, f.Seen.Input.Answers.Select(x => x.QuestionKey).Distinct().Count());
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("citation")]
    [InlineData("semantic")]
    [InlineData("sdk")]
    public async Task Real_provider_technical_schema_citation_and_semantic_failures_do_not_pause(string failure)
    {
        var f = new Fixture(); await f.Initialize(); var calls = 0;
        var client = new CustomOpenAIClient("fake-offline-credential", new HttpClient(new Handler(_ =>
        {
            if (failure == "sdk") throw new InvalidOperationException("Sensitive provider details must not persist.");
            if (failure == "schema") return Completion("{\"claims\":\"wrong type\"}");
            if (++calls == 1) return Completion("{\"claims\":[{\"claim\":\"Delivery is planned.\",\"entityKey\":\"delivery\",\"entityType\":\"Event\",\"sourceQuote\":\"Delivery is planned.\"}]}");
            return Completion(JsonSerializer.Serialize(new { assessments = new[] { new { candidateIndex = 0,
                supported = failure != "semantic", supportScore = failure == "semantic" ? 0.0 : 1.0,
                issues = Array.Empty<string>(), evidenceIds = new[] { "unknown-citation" } } } }));
        })));
        f.Provider = new TimelineFactOpenAIProvider(new TimelineFactExtractionAgent(client), new TimelineFactSemanticVerifierAgent(client), client);
        Assert.Equal("Retry", await f.Run());
        await using var db = f.Db(); var job = await db.TimelineFactJobs.SingleAsync();
        Assert.Contains(job.LastErrorCode, new[] { "InvalidResponse", "Unavailable" });
        Assert.Empty(await db.TimelineEntryClarifications.ToListAsync()); Assert.Empty(await db.TimelineFactProposals.ToListAsync());
    }

    [Theory]
    [InlineData("text")]
    [InlineData("answer-id")]
    [InlineData("job-id")]
    [InlineData("missing-support")]
    [InlineData("primary")]
    public async Task Custom_provider_cannot_forge_or_discard_supporting_answer_identity(string attack)
    {
        var f = new Fixture(); await f.Initialize(); await f.Run();
        await f.Answer(0, "Friday"); await f.Answer(1, "Alice"); await f.Resume();
        f.During = () =>
        {
            var source = f.Seen!;
            var span = TimelineFactSourceEvidence.Create(source).First(x => x.SourceKind == "Clarification");
            var quote = span.Text;
            var primary = span.Id;
            if (attack == "text") span = span with { Text = "Forged" };
            if (attack == "answer-id") span = span with { AnswerId = Guid.NewGuid() };
            if (attack == "job-id") span = span with { Id = "foreign-job" };
            if (attack == "primary") primary = "foreign-primary";
            f.Outcome = new([new(quote, "delivery", "Event", quote, "Clarification")], [],
                attack == "missing-support" ? [] : [new(0, primary, [span])]);
            return Task.CompletedTask;
        };
        Assert.Equal("Retry", await f.Run());
        await using var db = f.Db(); Assert.Empty(await db.TimelineFactProposals.ToListAsync()); Assert.Empty(await db.TimelineFacts.ToListAsync());
    }

    [Fact]
    public async Task Source_edit_then_restoration_never_revives_carried_answers()
    {
        var f = new Fixture(); await f.Initialize(); await f.Run();
        await f.Answer(0, "Friday"); await f.Answer(1, "Alice"); await f.Resume();
        await using (var db = f.Db()) { (await db.TimelineEntries.SingleAsync()).Content = "Changed"; await db.SaveChangesAsync(); }
        await using (var db = f.Db()) { (await db.TimelineEntries.SingleAsync()).Content = f.Entry.Content; await db.SaveChangesAsync(); }
        await f.Resume(); f.Outcome = new([], [], []);
        Assert.Equal("Completed", await f.Run()); Assert.Empty(f.Seen!.Input!.Answers);
        await using var verify = f.Db(); Assert.Equal(2, await verify.TimelineEntryClarifications.CountAsync());
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("job")]
    [InlineData("source")]
    [InlineData("missing")]
    [InlineData("json")]
    [InlineData("predecessor")]
    public async Task Malformed_persisted_input_fails_closed_before_provider(string attack)
    {
        var f = new Fixture(); await f.Initialize();
        await using (var raw = f.Raw())
        {
            var job = await raw.Set<TimelineFactJob>().SingleAsync();
            var snapshot = JsonSerializer.Deserialize<TimelineExtractionSnapshot>(job.ExtractionInputJson!)!;
            if (attack == "hash") job.EffectiveInputFingerprint = new string('0', 64);
            if (attack == "job") job.ExtractionInputJson = JsonSerializer.Serialize(snapshot with { Input = snapshot.Input with { JobId = Guid.NewGuid() } });
            if (attack == "source") job.ExtractionInputJson = JsonSerializer.Serialize(snapshot with { Source = snapshot.Source with { Title = "Other" } });
            if (attack == "missing") job.ExtractionInputJson = null;
            if (attack == "json") job.ExtractionInputJson = "{";
            if (attack == "predecessor") job.ExtractionInputJson = JsonSerializer.Serialize(snapshot with { PredecessorId = Guid.NewGuid() });
            await raw.SaveChangesAsync();
        }
        Assert.Equal("Retry", await f.Run()); Assert.Equal(0, f.Calls);
        await using var db = f.Db(); Assert.Empty(await db.TimelineEntryClarifications.ToListAsync());
    }

    [Theory]
    [InlineData("record")]
    [InlineData("author")]
    [InlineData("time")]
    [InlineData("generation")]
    [InlineData("lineage")]
    public async Task Changed_actual_answer_or_missing_lineage_cannot_ground_processing(string attack)
    {
        var f = new Fixture(); await f.Initialize(); await f.Run();
        await f.Answer(0, "Friday"); await f.Answer(1, "Alice"); await f.Resume();
        await using (var raw = f.Raw())
        {
            var row = await raw.Set<TimelineEntryClarification>().FirstAsync();
            if (attack == "record") row.Answer = "Mallory";
            if (attack == "author") row.AnsweredById = Guid.NewGuid();
            if (attack == "time") row.AnsweredAtUtc = row.AnsweredAtUtc!.Value.AddSeconds(1);
            if (attack == "generation") row.GenerationId = Guid.NewGuid();
            if (attack == "lineage")
            {
                var job = await raw.Set<TimelineFactJob>().SingleAsync(x => x.Id == f.JobId);
                var snapshot = JsonSerializer.Deserialize<TimelineExtractionSnapshot>(job.ExtractionInputJson!)!;
                job.ExtractionInputJson = JsonSerializer.Serialize(snapshot with { PredecessorId = null });
            }
            await raw.SaveChangesAsync();
        }
        Assert.Equal("Retry", await f.Run()); Assert.Equal(1, f.Calls);
        await using var db = f.Db(); Assert.Empty(await db.TimelineFactProposals.ToListAsync());
    }

    [Fact]
    public async Task Completion_revalidates_actual_answers_after_remote_call()
    {
        var f = new Fixture(); await f.Initialize(); await f.Run();
        await f.Answer(0, "Friday"); await f.Answer(1, "Alice"); await f.Resume(); f.Outcome = new([], [], []);
        f.During = async () =>
        {
            await using var raw = f.Raw(); (await raw.Set<TimelineEntryClarification>().FirstAsync()).Answer = "Externally forged";
            await raw.SaveChangesAsync();
        };
        Assert.Equal("Retry", await f.Run()); Assert.Equal(2, f.Calls);
        await using var db = f.Db(); Assert.Empty(await db.TimelineFactProposals.ToListAsync());
        Assert.Equal("InvalidResponse", (await db.TimelineFactJobs.SingleAsync(x => x.Id == f.JobId)).LastErrorCode);
    }

    [Fact]
    public async Task Revision_change_during_question_call_cannot_partially_persist_the_pause()
    {
        var f = new Fixture(); await f.Initialize();
        f.During = async () =>
        {
            await using var db = f.Db();
            await new TimelineService(db).AddEntryAsync(f.Timeline.Id, f.Actor.Id, "Later", "Other entry", default);
        };
        Assert.Equal("Retry", await f.Run());
        await using var verify = f.Db(); Assert.Empty(await verify.TimelineEntryClarifications.ToListAsync());
        Assert.Equal("Retry", (await verify.TimelineFactJobs.SingleAsync(x => x.Id == f.JobId)).State);
    }

    [Theory]
    [InlineData("lease")]
    [InlineData("source")]
    public async Task Losing_lease_or_source_during_call_cannot_pause_or_complete(string attack)
    {
        var f = new Fixture(); await f.Initialize();
        f.During = async () =>
        {
            await using var db = f.Db();
            if (attack == "source") (await db.TimelineEntries.SingleAsync()).Content = "Changed";
            else (await db.TimelineFactJobs.SingleAsync()).LeaseOwner = "other-worker";
            await db.SaveChangesAsync();
        };
        Assert.Equal("LostLease", await f.Run());
        await using var verify = f.Db(); Assert.Empty(await verify.TimelineEntryClarifications.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Answered_input_is_exactly_preserved_through_failure_cancellation_and_new_processor_instance(bool cancel)
    {
        var f = new Fixture(); await f.Initialize(); await f.Run();
        await f.Answer(0, "Friday"); await f.Answer(1, "Alice"); await f.Resume();
        string? frozen;
        await using (var db = f.Db()) frozen = (await db.TimelineFactJobs.SingleAsync(x => x.Id == f.JobId)).ExtractionInputJson;
        using var cancellation = new CancellationTokenSource();
        if (cancel) f.During = () => { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); };
        else f.Error = new TimelineFactProviderException("InvalidResponse", "InvalidEvidence");
        Assert.Equal(cancel ? "Cancelled" : "Retry", await f.Run(cancellation.Token));
        f.During = null; f.Error = null; f.Outcome = new([], [], []);
        await using (var db = f.Db())
        {
            var job = await db.TimelineFactJobs.SingleAsync(x => x.Id == f.JobId);
            Assert.Equal(frozen, job.ExtractionInputJson); job.NextAttemptAtUtc = null; await db.SaveChangesAsync();
        }
        Assert.Equal("Completed", await f.Run());
        await using var verify = f.Db(); var completed = await verify.TimelineFactJobs.SingleAsync(x => x.Id == f.JobId);
        Assert.Equal(2, completed.Attempts); Assert.Equal(frozen, completed.ExtractionInputJson);
        Assert.Equal(2, f.Seen!.Input!.Answers.Count);
    }

    [Fact]
    public async Task Cancellation_after_source_revocation_reports_lost_lease_not_a_successful_release()
    {
        var f = new Fixture(); await f.Initialize(); using var cancellation = new CancellationTokenSource();
        f.During = async () =>
        {
            await using var db = f.Db(); (await db.TimelineEntries.SingleAsync()).Content = "Changed"; await db.SaveChangesAsync();
            cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token);
        };
        Assert.Equal("LostLease", await f.Run(cancellation.Token));
        await using var verify = f.Db(); Assert.Equal("Superseded", (await verify.TimelineFactJobs.SingleAsync()).State);
        Assert.Empty(await verify.TimelineEntryClarifications.ToListAsync());
    }

    [Fact]
    public async Task Legacy_no_answer_pending_job_gets_durable_input_at_first_acquisition()
    {
        var f = new Fixture(); await f.Initialize();
        await using (var raw = f.Raw())
        {
            var job = await raw.Set<TimelineFactJob>().SingleAsync(); job.ExtractionInputJson = null; job.EffectiveInputFingerprint = null;
            await raw.SaveChangesAsync();
        }
        f.Outcome = new([], [], []); Assert.Equal("Completed", await f.Run());
        await using var db = f.Db(); var completed = await db.TimelineFactJobs.SingleAsync();
        Assert.NotNull(completed.ExtractionInputJson); Assert.Equal(completed.SourceFingerprint, completed.EffectiveInputFingerprint);
    }

    [Fact]
    public async Task Cancellation_is_not_a_question_and_input_survives_retry()
    {
        var f = new Fixture(); await f.Initialize(); using var cancellation = new CancellationTokenSource();
        f.During = () => { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); };
        Assert.Equal("Cancelled", await f.Run(cancellation.Token));
        await using var db = f.Db(); var job = await db.TimelineFactJobs.SingleAsync();
        Assert.NotNull(job.ExtractionInputJson); Assert.Equal("Cancelled", job.LastErrorCode);
        Assert.Empty(await db.TimelineEntryClarifications.ToListAsync());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("duplicate")]
    [InlineData("oversized")]
    [InlineData("declarative")]
    [InlineData("evidence")]
    public async Task Malformed_custom_outcomes_never_create_questions(string attack)
    {
        var f = new Fixture(); await f.Initialize();
        f.Outcome = attack switch
        {
            "null" => null!,
            "duplicate" => new([], [new("key", "Who?"), new("key", "When?")], []),
            "oversized" => new([], Enumerable.Range(0, 21).Select(x => new TimelineEntryQuestion(x.ToString(), "Who?")).ToArray(), []),
            "declarative" => new([], [new("key", "A declaration")], []),
            _ => new([], [new("key", "Who?")], [new(0, "unknown", [])])
        };
        Assert.Equal("Retry", await f.Run());
        await using var db = f.Db(); Assert.Equal("InvalidResponse", (await db.TimelineFactJobs.SingleAsync()).LastErrorCode);
        Assert.Empty(await db.TimelineEntryClarifications.ToListAsync());
    }

    [Fact]
    public async Task Frozen_inputs_cannot_be_replaced_and_waiting_jobs_cannot_be_force_released()
    {
        var f = new Fixture(); await f.Initialize();
        await using (var db = f.Db())
        {
            (await db.TimelineFactJobs.SingleAsync()).ExtractionInputJson = "{}";
            await Assert.ThrowsAsync<TimelineFactConflictException>(() => db.SaveChangesAsync());
        }
        await f.Run();
        await using (var db = f.Db())
        {
            var job = await db.TimelineFactJobs.SingleAsync(); job.State = "Retry";
            await Assert.ThrowsAsync<TimelineFactConflictException>(() => db.SaveChangesAsync());
        }
        await using var first = f.Db(); await first.TimelineFactJobs.SingleAsync();
        var row = await first.TimelineEntryClarifications.OrderBy(x => x.Question).FirstAsync();
        await first.StageEntryAnswerAsync(row.Id, f.Actor.Id, "Friday");
        await f.Answer(1, "Alice"); // Competing answer CASes generation and timeline.
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => first.SaveChangesAsync());
    }

    [Fact]
    public void Job_input_migration_is_additive_frozen_and_matches_runtime_model_without_database_access()
    {
        using var db = new ProjectTimelineDbContext(new DbContextOptionsBuilder<ProjectTimelineDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused", x => x.MigrationsAssembly("ProjectTimeline.Database")).Options);
        var sql = db.GetService<IMigrator>().GenerateScript("20261025223000_EntryExtractionClarifications", "20261025233000_ClarificationExtractionInputs");
        Assert.Contains("ADD \"ExtractionInputJson\" text", sql); Assert.Contains("ADD \"ExtractionSupportJson\" text", sql);
        Assert.Contains("ADD \"EffectiveInputFingerprint\" character varying(64)", sql);
        Assert.DoesNotContain("UPDATE ", sql); Assert.DoesNotContain("DELETE ", sql); Assert.DoesNotContain("DROP ", sql);
        var old = new ProjectTimeline.Database.Migrations.EntryExtractionClarifications().TargetModel;
        Assert.Null(old.FindEntityType(typeof(TimelineFactJob).FullName!)!.FindProperty("ExtractionInputJson"));
        var target = new ProjectTimeline.Database.Migrations.ClarificationExtractionInputs().TargetModel;
        Assert.Equal("text", target.FindEntityType(typeof(TimelineFactJob).FullName!)!.FindProperty("ExtractionInputJson")!.GetColumnType());
        Assert.Contains(target.FindEntityType(typeof(TimelineFact).FullName!)!.GetIndexes(), x => x.GetDatabaseName() == "IX_TimelineEntryFacts_FullText");
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData("InvalidResponse")]
    [InlineData("Unavailable")]
    public async Task Technical_failures_never_create_questions(string code)
    {
        var f = new Fixture(); await f.Initialize();
        f.Error = new TimelineFactProviderException(code, "InvalidEvidence");
        Assert.Equal("Retry", await f.Run());
        await using var db = f.Db();
        Assert.Empty(await db.TimelineEntryClarifications.ToListAsync());
        Assert.Equal(code, (await db.TimelineFactJobs.SingleAsync()).LastErrorCode);
    }
}
