using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.DataAccessLogic;
using ProjectTimeline.FactsInitializer;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class TimelineFactClarificationReviewTests
{
    private sealed class Fixture : ITimelineFactProvider
    {
        private readonly string database = Guid.NewGuid().ToString();
        private readonly InMemoryDatabaseRoot root = new();
        public ProjectTimelineDbContext Db() => new(new DbContextOptionsBuilder<ProjectTimelineDbContext>().UseInMemoryDatabase(database, root).Options);
        public DbContext Raw()
        {
            using var db = Db();
            return new(new DbContextOptionsBuilder().UseInMemoryDatabase(database, root).UseModel(db.Model).Options);
        }
        public User Actor = new() { Email = "review@example.test", DisplayName = "Writer", PasswordHash = "test" };
        public Project Project = new() { Code = "P", Name = "Project" };
        public Timeline Timeline = new() { Code = "T", Name = "Timeline" };
        public TimelineEntry Entry = null!;
        public Guid JobId;
        public Guid AnswerId;
        public Guid QuestionGeneration;
        public int Calls;
        public TimelineFactExtraction? Seen;
        public TimelineFactExtractionOutcome? Returned;
        public string AnswerText = "Alice owns delivery.";
        public bool AskAgain;
        public string ExtractionModel => "fake";
        public TimelineFactScope Scope => new(Project.Id, Timeline.Id, Entry.Id);
        public TimelineFactProcessing Worker => new(Db, this);
        public async Task Initialize(bool legacy = false)
        {
            await using var db = Db();
            Project.OwnerId = Actor.Id; Timeline.ProjectId = Project.Id;
            var role = new UserRole { RoleName = "User" }; Actor.IDUserRole = role.Id;
            db.AddRange(role, Actor, Project, Timeline); await db.SaveChangesAsync();
            if (legacy)
            {
                Entry = new() { TimelineId = Timeline.Id, AuthorId = Actor.Id, EntryNumber = 1, Title = "Delivery", Content = "Delivery is planned." };
                db.Add(Entry); await db.SaveChangesAsync();
            }
            else
            {
                Entry = await new TimelineService(db).AddEntryAsync(Timeline.Id, Actor.Id, "Delivery", "Delivery is planned.", default);
                JobId = await db.TimelineFactJobs.Select(x => x.Id).SingleAsync();
            }
        }
        public Task<IReadOnlyList<TimelineFactCandidate>> ExtractAsync(TimelineFactExtraction source, CancellationToken ct) =>
            throw new TimelineFactClarificationRequiredException(new([], [new("owner", "Who owns delivery?")], []));
        public Task<TimelineFactExtractionOutcome> ExtractOutcomeAsync(TimelineFactExtraction source, CancellationToken ct)
        {
            Calls++; Seen = source;
            var spans = TimelineFactSourceEvidence.Create(source);
            var answer = spans.FirstOrDefault(x => x.SourceKind == "Clarification");
            Returned = answer is null || AskAgain ? new([], [new("owner", "Who owns delivery?")], [])
                : new([new("Alice owns delivery.", "alice", "Person", answer.Text, "Clarification")], [],
                    [new(0, answer.Id, [spans[0], answer])]);
            return Task.FromResult(Returned);
        }
        public Task<float[]> EmbedAsync(string claim, CancellationToken ct) => throw new Exception("No embeddings");
        public async Task Generate(bool complete = true)
        {
            Assert.Equal("AwaitingClarification", await Worker.ProcessAsync(JobId, "test"));
            QuestionGeneration = JobId;
            await using (var db = Db())
            {
                AnswerId = await db.TimelineEntryClarifications.Select(x => x.Id).SingleAsync();
                await new TimelineEntryClarificationService(db).AnswerAsync(Scope, JobId, AnswerId, Actor.Id, false, AnswerText);
            }
            JobId = await Worker.RequestGenerationAsync(Scope, Actor.Id, false, true, JobId);
            if (complete) Assert.Equal("Completed", await Worker.ProcessAsync(JobId, "test"));
        }
        public FactReviewFakeAgent Fake(string quote = "paraphrased") => new() { Result = new() {
            Additions = [new("Alice owns delivery.", "alice", "Person", quote, "Clarification")] } };
        public Task<TimelineFactReviewPlan> Plan(ProjectTimelineDbContext db, FactReviewFakeAgent fake) =>
            new TimelineFactReviewPlanner(db, Scope, Actor.Id, false, new TimelineFactOptimizationAgent(fake)).PlanAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_wrappers_forward_complete_frozen_answer_input_and_outcome_through_two_checks(bool retry)
    {
        var f = new Fixture(); await f.Initialize(); await f.Generate();
        var source = f.Seen!;
        var checks = 0;
        var type = (retry ? typeof(TimelineFactRetryRunner) : typeof(FactInitializer))
            .GetNestedType(retry ? "GuardedProvider" : "CheckedProvider", BindingFlags.NonPublic)!;
        var wrapper = (ITimelineFactProvider)Activator.CreateInstance(type, f, (Func<CancellationToken, Task>)(_ => { checks++; return Task.CompletedTask; }))!;
        var calls = f.Calls;
        var result = await wrapper.ExtractOutcomeAsync(source, default);
        Assert.Same(source, f.Seen); Assert.Same(f.Returned, result);
        Assert.Single(result.Candidates); Assert.Empty(result.Questions); Assert.Equal(2, Assert.Single(result.Evidence).Spans.Count);
        Assert.Equal(calls + 1, f.Calls); Assert.Equal(2, checks);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task Both_wrappers_preserve_pre_and_post_denial_and_invocation_counts(bool retry, int denyAt)
    {
        var f = new Fixture(); await f.Initialize();
        await using var db = f.Db();
        var job = await db.TimelineFactJobs.SingleAsync();
        var source = new TimelineFactExtraction(f.Entry, (await db.ReadExtractionInputAsync(job, f.Entry)).Input);
        var checks = 0;
        var type = (retry ? typeof(TimelineFactRetryRunner) : typeof(FactInitializer))
            .GetNestedType(retry ? "GuardedProvider" : "CheckedProvider", BindingFlags.NonPublic)!;
        var wrapper = (ITimelineFactProvider)Activator.CreateInstance(type, f, (Func<CancellationToken, Task>)(_ => {
            if (++checks == denyAt) throw new UnauthorizedAccessException(); return Task.CompletedTask;
        }))!;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => wrapper.ExtractOutcomeAsync(source, default));
        Assert.Equal(denyAt, checks); Assert.Equal(denyAt - 1, f.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_runners_report_waiting_without_retrying_or_creating_facts(bool retry)
    {
        var f = new Fixture(); await f.Initialize(legacy: !retry);
        if (retry)
        {
            await using var raw = f.Raw();
            (await raw.Set<TimelineFactJob>().SingleAsync()).State = "Retry";
            await raw.SaveChangesAsync();
        }
        var results = new List<EntryResult>();
        if (retry) await new TimelineFactRetryRunner(f.Db, () => f, f.ExtractionModel).RunAsync(new(f.Actor.Id, f.Timeline.Id, true), results.Add);
        else await new FactInitializer(f.Db, () => f).RunAsync(new(f.Actor.Id, true), results.Add);
        Assert.Equal("AwaitingClarification", Assert.Single(results).Outcome);
        await using var db = f.Db();
        var job = await db.TimelineFactJobs.SingleAsync();
        Assert.Equal("AwaitingClarification", job.State); Assert.Equal(1, job.Attempts); Assert.Equal(1, f.Calls);
        Assert.Single(await db.TimelineEntryClarifications.ToListAsync());
        Assert.Empty(await db.TimelineFactProposals.ToListAsync()); Assert.Empty(await db.TimelineFacts.ToListAsync());
    }

    [Fact]
    public async Task Retry_runner_forwards_actual_answers_and_all_support_without_embeddings()
    {
        var f = new Fixture(); await f.Initialize(); await f.Generate(complete: false);
        await using (var raw = f.Raw())
        {
            (await raw.Set<TimelineFactJob>().SingleAsync(x => x.Id == f.JobId)).State = "Retry";
            await raw.SaveChangesAsync();
        }
        var results = new List<EntryResult>();
        await new TimelineFactRetryRunner(f.Db, () => f, f.ExtractionModel).RunAsync(new(f.Actor.Id, f.Timeline.Id, true), results.Add);
        Assert.Equal("Saved", Assert.Single(results).Outcome);
        Assert.Equal(f.AnswerText, Assert.Single(f.Seen!.Input!.Answers).Answer);
        await using var db = f.Db();
        var job = await db.TimelineFactJobs.SingleAsync(x => x.Id == f.JobId);
        Assert.Equal(2, Assert.Single(JsonSerializer.Deserialize<TimelineExtractionSupport[]>(job.ExtractionSupportJson!)!).Spans.Count);
        Assert.Equal(1, job.Attempts); Assert.Equal(2, f.Calls); Assert.Empty(await db.TimelineFacts.ToListAsync());
    }

    [Theory]
    [InlineData("actual-answer")]
    [InlineData("actual-question")]
    [InlineData("answer-entry")]
    [InlineData("answer-generation")]
    [InlineData("answer-time")]
    [InlineData("support-text")]
    [InlineData("support-id")]
    [InlineData("addition-id")]
    [InlineData("support-index")]
    [InlineData("effective-hash")]
    [InlineData("ledger-hash")]
    [InlineData("superseded")]
    public async Task Corrupted_actual_records_or_full_support_fail_before_review_model(string attack)
    {
        var f = new Fixture(); await f.Initialize(); await f.Generate();
        await using (var raw = f.Raw())
        {
            var row = await raw.Set<TimelineEntryClarification>().SingleAsync();
            var job = await raw.Set<TimelineFactJob>().SingleAsync(x => x.Id == f.JobId);
            var support = JsonSerializer.Deserialize<TimelineExtractionSupport[]>(job.ExtractionSupportJson!)!;
            switch (attack)
            {
                case "actual-answer": row.Answer = "Mallory owns delivery."; break;
                case "actual-question": row.Question = "Different question?"; break;
                case "answer-entry": row.EntryId = Guid.NewGuid(); break;
                case "answer-generation": row.GenerationId = Guid.NewGuid(); break;
                case "answer-time": row.AnsweredAtUtc = row.AnsweredAtUtc!.Value.AddSeconds(1); break;
                case "support-text": support[0] = support[0] with { Spans = [support[0].Spans[0] with { Text = "Forged" }] }; break;
                case "support-id": support[0] = support[0] with { PrimarySpanId = "foreign" }; break;
                case "addition-id": support[0] = support[0] with { AdditionId = Guid.NewGuid() }; break;
                case "support-index": support[0] = support[0] with { CandidateIndex = 1 }; break;
                case "effective-hash": job.EffectiveInputFingerprint = "wrong"; break;
                case "ledger-hash": job.ExtractionResultHash = "wrong"; break;
                case "superseded": job.State = "Superseded"; break;
            }
            job.ExtractionSupportJson = JsonSerializer.Serialize(support);
            await raw.SaveChangesAsync();
        }
        await using var db = f.Db(); var fake = f.Fake("Alice owns delivery.");
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => f.Plan(db, fake));
        Assert.Null(fake.Message); Assert.Empty(await db.TimelineFacts.ToListAsync());
    }

    [Theory]
    [InlineData("model")]
    [InlineData("prepare")]
    [InlineData("accept")]
    public async Task Actual_proof_is_rechecked_after_model_and_at_both_persistence_boundaries(string stage)
    {
        var f = new Fixture(); await f.Initialize(); await f.Generate();
        async Task Tamper()
        {
            await using var raw = f.Raw();
            (await raw.Set<TimelineEntryClarification>().SingleAsync()).Answer = "Replaced";
            await raw.SaveChangesAsync();
        }
        await using var db = f.Db(); var fake = f.Fake();
        if (stage == "model")
        {
            fake.Script = Tamper;
            await Assert.ThrowsAsync<TimelineFactConflictException>(() => f.Plan(db, fake));
        }
        else
        {
            var plan = await f.Plan(db, fake);
            var service = new TimelineFactService(db);
            if (stage == "prepare")
            {
                await Tamper();
                await Assert.ThrowsAsync<TimelineFactConflictException>(() => service.PrepareAsync(f.Scope, f.Actor.Id, false, plan.ExpectedRevision, plan.Draft));
            }
            else
            {
                var pending = await service.PrepareAsync(f.Scope, f.Actor.Id, false, plan.ExpectedRevision, plan.Draft);
                await Tamper();
                await Assert.ThrowsAsync<TimelineFactConflictException>(() => service.AcceptAsync(f.Scope, pending.Id, f.Actor.Id, false));
            }
        }
        Assert.Empty(await db.TimelineFacts.ToListAsync());
    }

    [Theory]
    [InlineData("claim")]
    [InlineData("key")]
    [InlineData("type")]
    [InlineData("kind")]
    [InlineData("answer")]
    public async Task Model_cannot_restore_changed_identity_or_replace_actual_answer(string change)
    {
        var f = new Fixture(); await f.Initialize(); await f.Generate();
        await using var db = f.Db(); var fake = f.Fake();
        var candidate = fake.Result.Additions[0];
        fake.Result.Additions[0] = change switch {
            "claim" => candidate with { Claim = "Mallory owns delivery." },
            "key" => candidate with { EntityKey = "mallory" },
            "type" => candidate with { EntityType = "Team" },
            "kind" => candidate with { SourceKind = "Entry" },
            _ => candidate
        };
        if (change == "answer") fake.Result.Questions.Add(new("Who owns delivery?", "Mallory owns delivery."));
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => f.Plan(db, fake));
    }

    [Fact]
    public async Task Repeated_question_text_retains_distinct_durable_actual_answers_across_review_rounds()
    {
        var f = new Fixture(); await f.Initialize(); await f.Generate(complete: false);
        f.AskAgain = true;
        Assert.Equal("AwaitingClarification", await f.Worker.ProcessAsync(f.JobId, "test"));
        await using (var answer = f.Db())
        {
            var row = await answer.TimelineEntryClarifications.SingleAsync(x => x.GenerationId == f.JobId);
            await new TimelineEntryClarificationService(answer).AnswerAsync(f.Scope, f.JobId, row.Id, f.Actor.Id, false, "Alice Smith owns delivery.");
        }
        f.JobId = await f.Worker.RequestGenerationAsync(f.Scope, f.Actor.Id, false, true, f.JobId);
        f.AskAgain = false;
        Assert.Equal("Completed", await f.Worker.ProcessAsync(f.JobId, "test"));
        await using var db = f.Db();
        var first = await f.Plan(db, f.Fake());
        Assert.Equal(2, first.Draft.Questions.Count);
        Assert.Equal(2, first.Draft.Questions.Select(x => x.ExtractionAnswerId).Distinct().Count());
        Assert.Single(first.Draft.Questions.Select(x => x.Question).Distinct());
        var pending = await new TimelineFactService(db).PrepareAsync(f.Scope, f.Actor.Id, false, first.ExpectedRevision, first.Draft);
        var next = await new TimelineFactReviewPlanner(db, f.Scope, f.Actor.Id, false, new TimelineFactOptimizationAgent(f.Fake())).PlanAsync(pending.Id);
        Assert.Equal(first.Draft.Questions, next.Draft.Questions);
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => new TimelineFactReviewPlanner(db, f.Scope, f.Actor.Id, false,
            new TimelineFactOptimizationAgent(f.Fake())).PlanAsync(pending.Id, [new(0, "Replaced actual answer")]));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("scope")]
    [InlineData("revision")]
    [InlineData("generation")]
    [InlineData("reference")]
    public async Task Normal_preparation_rejects_changed_scope_revision_or_extraction_reference(string change)
    {
        var f = new Fixture(); await f.Initialize(); await f.Generate();
        await using var db = f.Db();
        var plan = await f.Plan(db, f.Fake());
        var draft = plan.Draft;
        var scope = f.Scope;
        if (change == "scope") scope = scope with { EntryId = Guid.NewGuid() };
        else if (change == "reference") draft = draft with { Questions = [draft.Questions[0] with { ExtractionAnswerId = Guid.NewGuid() }] };
        else
        {
            await using var raw = f.Raw();
            if (change == "source") (await raw.Set<TimelineEntry>().SingleAsync()).Content += " edited";
            if (change == "revision") (await raw.Set<Timeline>().SingleAsync()).FactRevision++;
            if (change == "generation") (await raw.Set<TimelineFactJob>().SingleAsync(x => x.Id == f.JobId)).State = "Superseded";
            await raw.SaveChangesAsync();
        }
        if (change == "scope") await Assert.ThrowsAsync<KeyNotFoundException>(() => new TimelineFactService(db).PrepareAsync(scope, f.Actor.Id, false, plan.ExpectedRevision, draft));
        else await Assert.ThrowsAsync<TimelineFactConflictException>(() => new TimelineFactService(db).PrepareAsync(scope, f.Actor.Id, false, plan.ExpectedRevision, draft));
        Assert.Empty(await db.TimelineFacts.ToListAsync());
    }

    [Fact]
    public async Task Ambiguous_exact_identity_never_repairs_clarification_quote()
    {
        var f = new Fixture { AnswerText = new string('x', 256) + "Alice owns delivery." };
        await f.Initialize(); await f.Generate();
        await using (var raw = f.Raw())
        {
            var job = await raw.Set<TimelineFactJob>().SingleAsync(x => x.Id == f.JobId);
            var addition = await raw.Set<TimelineFactAddition>().SingleAsync();
            var second = new TimelineFactAddition { ProposalId = addition.ProposalId, Claim = addition.Claim,
                EntityKey = addition.EntityKey, EntityType = addition.EntityType, SourceKind = addition.SourceKind, SourceQuote = "Alice owns delivery." };
            raw.Add(second);
            var span = TimelineFactSourceEvidence.Create(f.Seen!).Last(x => x.SourceKind == "Clarification");
            var support = JsonSerializer.Deserialize<TimelineExtractionSupport[]>(job.ExtractionSupportJson!)!.ToList();
            support.Add(new(1, span.Id, [new(span.Id, span.Source, span.Start, span.Length, span.Text, span.SourceKind, span.AnswerId)], second.Id));
            job.ExtractionSupportJson = JsonSerializer.Serialize(support);
            await raw.SaveChangesAsync();
            job.ExtractionResultHash = TimelineFactEvidence.ResultHash((await raw.Set<TimelineFactAddition>().ToListAsync()).Select(TimelineFactEvidence.CandidateEvidence));
            await raw.SaveChangesAsync();
        }
        await using var db = f.Db(); var fake = f.Fake();
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => f.Plan(db, fake));
        Assert.NotNull(fake.Message); // Full proof is valid; ambiguous identity is not repaired.
    }

    [Fact]
    public async Task Generated_answers_flow_through_normal_review_and_explicit_acceptance_with_durable_identity()
    {
        var f = new Fixture(); await f.Initialize();
        Guid protectedId;
        string protectedEvidence;
        await using (var earlier = f.Db())
        {
            var entry = new TimelineEntry { TimelineId = f.Timeline.Id, AuthorId = f.Actor.Id, EntryNumber = 0,
                Title = "Protected", Content = "Bob owns testing." };
            earlier.Add(entry); await earlier.SaveChangesAsync();
            var scope = new TimelineFactScope(f.Project.Id, f.Timeline.Id, entry.Id);
            var earlierService = new TimelineFactService(earlier);
            var proposal = await earlierService.PrepareAsync(scope, f.Actor.Id, false, await earlier.Timelines.Select(x => x.FactRevision).SingleAsync(),
                new([new("Bob owns testing.", "bob", "Person", entry.Content)], [], []));
            await earlierService.AcceptAsync(scope, proposal.Id, f.Actor.Id, false);
            var protectedFact = await earlier.TimelineFacts.SingleAsync();
            protectedId = protectedFact.Id; protectedEvidence = JsonSerializer.Serialize(protectedFact);
        }
        await f.Generate();
        await using var db = f.Db();
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => new TimelineFactService(db).AcceptAsync(f.Scope, f.JobId, f.Actor.Id, false));
        var fake = f.Fake();
        var plan = await f.Plan(db, fake);
        var question = Assert.Single(plan.Draft.Questions);
        Assert.Equal("Alice owns delivery.", question.Answer);
        var json = JsonSerializer.SerializeToElement(question);
        Assert.Equal(f.AnswerId, json.GetProperty("ExtractionAnswerId").GetGuid());
        Assert.Equal(f.JobId, json.GetProperty("ExtractionGenerationId").GetGuid());
        Assert.Contains("Alice owns delivery.", fake.Message);
        Assert.Equal("Alice owns delivery.", Assert.Single(plan.Draft.Additions).SourceQuote);
        var service = new TimelineFactService(db);
        var pending = await service.PrepareAsync(f.Scope, f.Actor.Id, false, plan.ExpectedRevision, plan.Draft);
        Assert.Single(await db.TimelineFacts.ToListAsync());
        await service.AcceptAsync(f.Scope, pending.Id, f.Actor.Id, false);
        var fact = await db.TimelineFacts.SingleAsync(x => x.EntryId == f.Entry.Id);
        Assert.Equal("Clarification", fact.SourceKind);
        Assert.Contains(f.AnswerId.ToString(), fact.ClarificationEvidence);
        Assert.Contains(f.JobId.ToString(), fact.ClarificationEvidence);
        Assert.Equal(f.Entry.Content, (await db.TimelineEntries.SingleAsync(x => x.Id == f.Entry.Id)).Content);
        Assert.Equal(protectedEvidence, JsonSerializer.Serialize(await db.TimelineFacts.SingleAsync(x => x.Id == protectedId)));
    }
}
