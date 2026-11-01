using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectTimeline.UI.WebApi;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.DataAccessLogic;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class TimelineFactGenerationFeedbackTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(reply(request)); }
    }
    private sealed class Fixture
    {
        private readonly string database = Guid.NewGuid().ToString();
        public ProjectTimelineDbContext Db() => new(new DbContextOptionsBuilder<ProjectTimelineDbContext>().UseInMemoryDatabase(database).Options);
        public DbContext Raw()
        {
            using var db = Db();
            return new(new DbContextOptionsBuilder().UseInMemoryDatabase(database).UseModel(db.Model).Options);
        }
        public TimelineFactScope Scope => new(Project.Id, Timeline.Id, Entry.Id);
        public User Actor = new() { Email = "feedback@example.test", DisplayName = "Writer", PasswordHash = "test" };
        public Project Project = new() { Code = "P", Name = "Project" };
        public Timeline Timeline = new() { Code = "T", Name = "Timeline" };
        public TimelineEntry Entry = null!;
        public Guid JobId;
        public int Calls;
        public bool Questions;
        public bool Approved = false;
        public bool TechnicalFailure = false;
        public string? Citation = null;
        public Func<Task>? During = null;
        public List<JsonElement> Payloads = [];
        public TimelineFactProcessing Worker => new(Db, Provider);
        public TimelineFactOpenAIProvider Provider
        {
            get
            {
                var client = new CustomOpenAIClient("fake-offline-credential", new HttpClient(new Handler(request =>
                {
                    using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                    using var payload = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
                    Payloads.Add(payload.RootElement.Clone()); Calls++;
                    if (TechnicalFailure) throw new InvalidOperationException("Offline technical failure");
                    var verification = payload.RootElement.TryGetProperty("candidates", out _);
                    if (verification) During?.Invoke().GetAwaiter().GetResult();
                    var id = payload.RootElement.GetProperty("sourceSpans")[0].GetProperty("id").GetString()!;
                    var response = verification ? JsonSerializer.Serialize(new {
                        assessments = new[] { new { candidateIndex = 0, supported = Approved, supportScore = 0.1,
                            issues = Approved ? Array.Empty<string>() : new[] { "Unsupported" },
                            evidenceIds = Citation is not null ? new[] { Citation } : Approved ? new[] { id } : Array.Empty<string>() } },
                        questions = Questions ? new[] { new { key = "actor", text = "Who owns delivery?", reason = "MissingInformation", evidenceIds = new[] { id } } } : []
                    }) : "{\"claims\":[{\"claim\":\"Alice owns delivery.\",\"entityKey\":\"alice\",\"entityType\":\"Person\",\"sourceQuote\":\"Delivery\"}]}";
                    return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                        id = "offline", model = "gpt-4o-mini", created = 0, choices = new[] { new { index = 0, finish_reason = "stop",
                            message = new { role = "assistant", content = response } } }
                    }), Encoding.UTF8, "application/json") };
                })));
                return new(new TimelineFactExtractionAgent(client), new TimelineFactSemanticVerifierAgent(client), client);
            }
        }
        public async Task Initialize()
        {
            await using var db = Db();
            Project.OwnerId = Actor.Id; Timeline.ProjectId = Project.Id;
            db.AddRange(Actor, Project, Timeline); await db.SaveChangesAsync();
            Entry = await new TimelineService(db).AddEntryAsync(Timeline.Id, Actor.Id, "Delivery", "Delivery is planned.", default);
            JobId = await db.TimelineFactJobs.Select(x => x.Id).SingleAsync();
        }
        public Task<string> Run() => Worker.ProcessAsync(JobId, "feedback-worker");
        public async Task Answer()
        {
            await using var db = Db();
            var row = await db.TimelineEntryClarifications.SingleAsync(x => x.GenerationId == JobId);
            await new TimelineFactApplication(db, () => throw new Exception("Answer saves never construct agents."), Worker)
                .AnswerClarificationAsync(Entry.Id, row.Id, Actor.Id, false, new(JobId, "Alice owns delivery."));
        }
        public async Task Resume() => JobId = await Worker.RequestGenerationAsync(Scope, Actor.Id, false, true, JobId);
    }

    [Theory]
    [InlineData(false, "Failed")]
    [InlineData(true, "AwaitingClarification")]
    public async Task Semantic_outcomes_stop_without_retry_or_partial_proposals(bool questions, string expected)
    {
        var f = new Fixture { Questions = questions }; await f.Initialize();
        Assert.Equal(expected, await f.Run());
        Assert.Equal(expected, await f.Run());
        Assert.Equal(2, f.Calls);
        await using var db = f.Db();
        var job = await db.TimelineFactJobs.SingleAsync();
        Assert.Equal(1, job.Attempts); Assert.Null(job.LeaseOwner); Assert.Null(job.LeaseExpiresAtUtc);
        Assert.Empty(await db.TimelineFacts.ToListAsync()); Assert.Empty(await db.TimelineFactProposals.ToListAsync());
        Assert.Equal(questions ? 1 : 0, await db.TimelineEntryClarifications.CountAsync());
        Assert.Null(job.LastErrorCode); Assert.Null(job.NextAttemptAtUtc);
        Assert.Null(job.ExtractionSupportJson); Assert.Null(job.ExtractionResultHash);
        var feedback = TimelineExtractionFeedbackCodec.Decode(job.ExtractionFeedbackJson!);
        Assert.Equal(1, feedback.Version); Assert.Equal(job.Id, feedback.GenerationId);
        Assert.Equal(job.SourceFingerprint, feedback.SourceFingerprint);
        Assert.Equal(job.EffectiveInputFingerprint, feedback.EffectiveInputFingerprint);
        Assert.False(Assert.Single(feedback.Findings).Supported);
        Assert.Empty(feedback.Findings[0].EvidenceIds);
        var state = await new TimelineFactApplication(db, () => throw new Exception("Read never constructs agents."))
            .ReadAsync(f.Entry.Id, f.Actor.Id, false);
        Assert.Equal(questions ? "AwaitingClarification" : "NeedsCorrection", state.LifecycleState);
        Assert.Equal(1, state.ExtractionFeedback!.UnsupportedCount); Assert.False(state.RefreshRequired);
        Assert.Equal(!questions, state.CanRegenerate);
    }

    [Fact]
    public async Task Explicit_successor_receives_only_exact_predecessor_feedback_outside_evidence_and_freeze_preserves_it()
    {
        var f = new Fixture(); await f.Initialize(); Assert.Equal("Failed", await f.Run());
        var previous = f.JobId;
        string originalInput, feedbackJson;
        await using (var db = f.Db())
        {
            var job = await db.TimelineFactJobs.SingleAsync();
            originalInput = job.ExtractionInputJson!; feedbackJson = job.ExtractionFeedbackJson!;
        }
        await f.Resume();
        await using (var db = f.Db())
        {
            var job = await db.TimelineFactJobs.SingleAsync(x => x.Id == f.JobId);
            var snapshot = await db.ReadExtractionInputAsync(job, f.Entry);
            Assert.Equal(previous, snapshot.PredecessorId); Assert.Null(job.ExtractionFeedbackJson);
            var source = new TimelineFactExtraction(f.Entry, snapshot.Input, snapshot.ExtractionFeedback);
            var ids = TimelineFactSourceEvidence.Create(source).Select(x => x.Id).ToArray();
            var without = new TimelineFactExtraction(f.Entry, snapshot.Input);
            Assert.Equal(ids, TimelineFactSourceEvidence.Create(without).Select(x => x.Id));
            for (var i = 0; i < 3; i++)
            {
                var before = source.ExtractionFeedback!.Value.GetRawText(); source = source.Freeze();
                Assert.Equal(before, source.ExtractionFeedback!.Value.GetRawText());
                Assert.Equal(previous, source.ExtractionFeedback.Value.GetProperty("generationId").GetGuid());
            }
            ITimelineFactProvider legacy = new LegacyProvider();
            var error = await Assert.ThrowsAsync<TimelineFactProviderException>(() => legacy.ExtractOutcomeAsync(source, default));
            Assert.Equal("ClarificationInputUnsupported", error.DiagnosticCode);
        }
        f.Approved = true; Assert.Equal("Completed", await f.Run());
        Assert.Equal(4, f.Calls);
        var extraction = f.Payloads[2]; var verification = f.Payloads[3];
        Assert.Equal(previous, extraction.GetProperty("extractionFeedback").GetProperty("generationId").GetGuid());
        Assert.False(extraction.GetProperty("source").TryGetProperty("extractionFeedback", out _));
        Assert.False(verification.TryGetProperty("extractionFeedback", out _));
        Assert.Equal(extraction.GetProperty("sourceSpans").GetRawText(), verification.GetProperty("sourceSpans").GetRawText());
        await using var verify = f.Db();
        var old = await verify.TimelineFactJobs.SingleAsync(x => x.Id == previous);
        Assert.Equal("Superseded", old.State); Assert.Equal(originalInput, old.ExtractionInputJson); Assert.Equal(feedbackJson, old.ExtractionFeedbackJson);
        Assert.Equal("Generated", (await verify.TimelineFactProposals.SingleAsync()).Status); Assert.Empty(await verify.TimelineFacts.ToListAsync());
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => new TimelineFactService(verify).AcceptAsync(f.Scope, f.JobId, f.Actor.Id, false));
    }

    private sealed class LegacyProvider : ITimelineFactProvider
    {
        public string ExtractionModel => "legacy";
        public Task<IReadOnlyList<TimelineFactCandidate>> ExtractAsync(TimelineFactExtraction source, CancellationToken ct) =>
            throw new Exception("A nonaware provider must not be invoked with feedback.");
        public Task<float[]> EmbedAsync(string claim, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Multiple_human_rounds_save_without_agents_and_restarted_retry_preserves_frozen_input()
    {
        var f = new Fixture { Questions = true }; await f.Initialize();
        for (var round = 0; round < 2; round++)
        {
            Assert.Equal("AwaitingClarification", await f.Run());
            var calls = f.Calls; await f.Answer(); Assert.Equal(calls, f.Calls);
            await f.Resume();
        }
        string frozen;
        await using (var db = f.Db()) frozen = (await db.TimelineFactJobs.SingleAsync(x => x.Id == f.JobId)).ExtractionInputJson!;
        f.TechnicalFailure = true; Assert.Equal("Retry", await f.Run());
        await using (var db = f.Db())
        {
            var job = await db.TimelineFactJobs.SingleAsync(x => x.Id == f.JobId);
            Assert.Equal(frozen, job.ExtractionInputJson); Assert.Null(job.ExtractionFeedbackJson);
            job.NextAttemptAtUtc = null; await db.SaveChangesAsync();
        }
        f.TechnicalFailure = false; f.Questions = false; f.Approved = true;
        Assert.Equal("Completed", await f.Run());
        await using var verify = f.Db();
        var completed = await verify.TimelineFactJobs.SingleAsync(x => x.Id == f.JobId);
        Assert.Equal(frozen, completed.ExtractionInputJson);
        var input = await verify.ReadExtractionInputAsync(completed, f.Entry);
        Assert.Equal(2, input.Input.Answers.Count); Assert.NotNull(input.ExtractionFeedback);
        Assert.Equal(f.Entry.Content, (await verify.TimelineEntries.SingleAsync()).Content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Historical_feedback_ids_never_bind_as_successor_citations_even_for_rejections(bool approved)
    {
        var f = new Fixture(); await f.Initialize(); await f.Run();
        var oldId = f.Payloads[1].GetProperty("sourceSpans")[0].GetProperty("id").GetString();
        await f.Resume(); f.Citation = oldId; f.Approved = approved;
        Assert.Equal("Retry", await f.Run());
        await using var db = f.Db();
        Assert.Null((await db.TimelineFactJobs.SingleAsync(x => x.Id == f.JobId)).ExtractionFeedbackJson);
        Assert.Empty(await db.TimelineFactProposals.ToListAsync());
    }

    [Theory]
    [InlineData("lease", false)]
    [InlineData("expired", true)]
    [InlineData("source", true)]
    [InlineData("revision", false)]
    [InlineData("author", true)]
    public async Task Feedback_rechecks_lease_source_revision_and_author_at_atomic_save(string attack, bool questions)
    {
        var f = new Fixture { Questions = questions }; await f.Initialize();
        f.During = async () => {
            await using var db = f.Db();
            if (attack == "lease") (await db.TimelineFactJobs.SingleAsync()).LeaseOwner = "other-worker";
            if (attack == "expired") (await db.TimelineFactJobs.SingleAsync()).LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
            if (attack == "source") (await db.TimelineEntries.SingleAsync()).Content += " edited";
            if (attack == "revision") await new TimelineService(db).AddEntryAsync(f.Timeline.Id, f.Actor.Id, "Later", "Later source", default);
            if (attack == "author") (await db.Users.SingleAsync()).IsActive = false;
            await db.SaveChangesAsync();
        };
        Assert.Equal(attack is "lease" or "expired" or "source" ? "LostLease" : "Retry", await f.Run());
        await using var verify = f.Db();
        Assert.Null((await verify.TimelineFactJobs.SingleAsync(x => x.Id == f.JobId)).ExtractionFeedbackJson);
        Assert.Empty(await verify.TimelineEntryClarifications.ToListAsync()); Assert.Empty(await verify.TimelineFactProposals.ToListAsync());
    }

    [Theory]
    [InlineData("replace")]
    [InlineData("clear")]
    [InlineData("retry")]
    [InlineData("complete")]
    [InlineData("insert")]
    [InlineData("embedding")]
    public async Task Forged_insert_replacement_clear_retry_and_completion_fail_closed(string attack)
    {
        var f = new Fixture(); await f.Initialize(); await f.Run();
        await using var db = f.Db(); var job = await db.TimelineFactJobs.SingleAsync();
        if (attack == "replace") job.ExtractionFeedbackJson += " ";
        if (attack == "clear") job.ExtractionFeedbackJson = null;
        if (attack == "retry") job.State = "Retry";
        if (attack == "complete") job.State = "Completed";
        if (attack is "insert" or "embedding") db.TimelineFactJobs.Add(new TimelineFactJob {
            EntryId = f.Entry.Id, Kind = attack == "embedding" ? "Embedding" : "Extraction", WorkKey = "forged",
            SourceFingerprint = job.SourceFingerprint, ExtractionFeedbackJson = job.ExtractionFeedbackJson });
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Source_edit_restoration_does_not_search_historical_feedback_by_hash()
    {
        var f = new Fixture(); await f.Initialize(); await f.Run(); var oldId = f.JobId;
        await using (var db = f.Db()) { (await db.TimelineEntries.SingleAsync()).Content = "Changed"; await db.SaveChangesAsync(); }
        await using (var db = f.Db()) { (await db.TimelineEntries.SingleAsync()).Content = f.Entry.Content; await db.SaveChangesAsync(); }
        await f.Resume();
        await using var verify = f.Db();
        var snapshot = await verify.ReadExtractionInputAsync(await verify.TimelineFactJobs.SingleAsync(x => x.Id == f.JobId), f.Entry);
        Assert.Null(snapshot.PredecessorId); Assert.Null(snapshot.ExtractionFeedback); Assert.Empty(snapshot.Input.Answers);
        Assert.NotNull((await verify.TimelineFactJobs.SingleAsync(x => x.Id == oldId)).ExtractionFeedbackJson);
    }

    [Theory]
    [InlineData("feedback")]
    [InlineData("predecessor")]
    [InlineData("output")]
    public async Task Tampered_successor_feedback_or_predecessor_identity_fails_before_models(string attack)
    {
        var f = new Fixture(); await f.Initialize(); await f.Run(); await f.Resume();
        await using (var raw = f.Raw())
        {
            var job = await raw.Set<TimelineFactJob>().SingleAsync(x => x.Id == f.JobId);
            var snapshot = JsonSerializer.Deserialize<TimelineExtractionSnapshot>(job.ExtractionInputJson!)!;
            if (attack == "feedback") snapshot = snapshot with { ExtractionFeedback = snapshot.ExtractionFeedback! with { GenerationId = Guid.NewGuid() } };
            if (attack == "predecessor") snapshot = snapshot with { PredecessorId = null };
            if (attack == "output") (await raw.Set<TimelineFactJob>().SingleAsync(x => x.Id == snapshot.PredecessorId)).ExtractionFeedbackJson = "{}";
            job.ExtractionInputJson = JsonSerializer.Serialize(snapshot); await raw.SaveChangesAsync();
        }
        Assert.Equal("Retry", await f.Run()); Assert.Equal(2, f.Calls);
    }

    [Fact]
    public async Task Approved_predecessor_does_not_reexport_feedback_from_its_own_older_input()
    {
        var f = new Fixture(); await f.Initialize(); await f.Run(); await f.Resume();
        f.Approved = true; Assert.Equal("Completed", await f.Run());
        await f.Resume();
        await using var db = f.Db();
        var snapshot = await db.ReadExtractionInputAsync(await db.TimelineFactJobs.SingleAsync(x => x.Id == f.JobId), f.Entry);
        Assert.Null(snapshot.ExtractionFeedback); // Only predecessor OUTPUT, never recursive historical guidance.
    }

    [Theory]
    [InlineData("version")]
    [InlineData("generation")]
    [InlineData("source")]
    [InlineData("effective")]
    [InlineData("index")]
    [InlineData("empty")]
    [InlineData("disposition")]
    [InlineData("issue")]
    [InlineData("evidence")]
    public async Task Feedback_codec_rejects_incomplete_foreign_or_forged_output(string attack)
    {
        var f = new Fixture(); await f.Initialize(); await f.Run();
        await using var db = f.Db(); var job = await db.TimelineFactJobs.SingleAsync();
        var snapshot = await db.ReadExtractionInputAsync(job, f.Entry);
        var feedback = TimelineExtractionFeedbackCodec.Decode(job.ExtractionFeedbackJson!);
        feedback = attack switch {
            "version" => feedback with { Version = 2 }, "generation" => feedback with { GenerationId = Guid.NewGuid() },
            "source" => feedback with { SourceFingerprint = new string('0', 64) },
            "effective" => feedback with { EffectiveInputFingerprint = new string('0', 64) },
            "index" => feedback with { Findings = [feedback.Findings[0] with { CandidateIndex = 1 }] },
            "empty" => feedback with { Findings = [] }, "disposition" => feedback with { Disposition = "Approved" },
            "issue" => feedback with { Findings = [feedback.Findings[0] with { Issues = ["ModelInstructions"] }] },
            _ => feedback with { Findings = [feedback.Findings[0] with { EvidenceIds = ["foreign"] }] }
        };
        Assert.Throws<TimelineFactConflictException>(() => TimelineExtractionFeedbackCodec.Validate(feedback, job, f.Entry, snapshot.Input));
        Assert.Throws<TimelineFactConflictException>(() => TimelineExtractionFeedbackCodec.Decode(job.ExtractionFeedbackJson! + " "));
        Assert.Throws<TimelineFactConflictException>(() => TimelineExtractionFeedbackCodec.Decode(new string('x', TimelineExtractionFeedbackCodec.MaxJsonLength + 1)));
    }

    [Fact]
    public void Migration_is_one_nullable_column_with_frozen_target_and_snapshot_agreement_offline()
    {
        using var db = new ProjectTimelineDbContext(new DbContextOptionsBuilder<ProjectTimelineDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused", x => x.MigrationsAssembly("ProjectTimeline.Database")).Options);
        var sql = db.GetService<IMigrator>().GenerateScript("20261025233000_ClarificationExtractionInputs", "20261026000100_ExtractionGenerationFeedback");
        Assert.Contains("ADD \"ExtractionFeedbackJson\" text", sql); Assert.DoesNotContain("NOT NULL", sql);
        Assert.DoesNotContain("UPDATE ", sql); Assert.DoesNotContain("DELETE ", sql); Assert.DoesNotContain("DROP ", sql);
        var old = new ProjectTimeline.Database.Migrations.ClarificationExtractionInputs().TargetModel;
        Assert.Null(old.FindEntityType(typeof(TimelineFactJob).FullName!)!.FindProperty("ExtractionFeedbackJson"));
        var target = new ProjectTimeline.Database.Migrations.ExtractionGenerationFeedback().TargetModel;
        var column = target.FindEntityType(typeof(TimelineFactJob).FullName!)!.FindProperty("ExtractionFeedbackJson")!;
        Assert.True(column.IsNullable); Assert.Equal("text", column.GetColumnType());
        Assert.Contains(target.FindEntityType(typeof(TimelineFact).FullName!)!.GetIndexes(), x => x.GetDatabaseName() == "IX_TimelineEntryFacts_FullText");
        Assert.False(db.Database.HasPendingModelChanges()); // Metadata/SQL generation only: no connection or migration apply.
    }
}
