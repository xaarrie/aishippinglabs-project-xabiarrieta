using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.AgentModels;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.BusinessLogic.Agents.Models;
using ProjectTimeline.BusinessLogic.Agents.Tools;
using ProjectTimeline.DataAccessLogic;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class TimelineFactReviewPlannerTests
{
    private static TimelineFactReviewPlanner Planner(TimelineFactReviewFixture f, FactReviewFakeAgent fake)
        => new(f.Db, f.Scope, f.Actor.Id, false, new TimelineFactOptimizationAgent(fake));

    [Fact]
    public async Task Planner_keeps_source_pending_candidates_and_incomplete_coverage_without_writes()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var accepted = await f.AcceptAsync("Alice owns delivery.", "alice");
        var entry = new TimelineEntry { TimelineId = f.Timeline.Id, AuthorId = f.Actor.Id, EntryNumber = 3,
            Title = f.Entry.Title, Content = f.Entry.Content };
        f.Db.Add(entry); await f.Db.SaveChangesAsync();
        await PendingGenerationProvider.GenerateAsync(f.Db, entry, f.Actor.Id, f.Project.Id);
        var pending = await f.Db.TimelineFactProposals.SingleAsync(x => x.EntryId == entry.Id);
        var fake = new FactReviewFakeAgent { Result = new() { Additions = [new("Bob owns testing.", "bob", "Person", "Bob owns testing.")] } };
        var revision = f.Timeline.FactRevision;
        var count = await f.Db.TimelineFactProposals.CountAsync();
        var result = await new TimelineFactReviewPlanner(f.Db, new(f.Project.Id, f.Timeline.Id, entry.Id), f.Actor.Id, false,
            new TimelineFactOptimizationAgent(fake)).PlanAsync();
        var json = JsonDocument.Parse(fake.Message!).RootElement;
        Assert.Equal(entry.Content, json.GetProperty("original_entry").GetProperty("Content").GetString());
        Assert.Equal(entry.Title, json.GetProperty("original_entry").GetProperty("Title").GetString());
        Assert.Contains(json.GetProperty("seed_facts").EnumerateArray(), x => x.GetProperty("Id").GetGuid() == accepted.Id);
        Assert.DoesNotContain(json.GetProperty("seed_facts").EnumerateArray(), x => x.GetProperty("CreationReviewId").GetGuid() == pending.Id);
        Assert.Equal("alice", Assert.Single(json.GetProperty("pending_extraction_candidates").EnumerateArray()).GetProperty("EntityKey").GetString());
        Assert.Equal(revision, result.ExpectedRevision);
        Assert.Equal(TimelineFactEvidence.Fingerprint(entry), result.SourceFingerprint);
        Assert.Equal(count, await f.Db.TimelineFactProposals.CountAsync());
        Assert.False(f.Db.ChangeTracker.HasChanges());
        Assert.Equal(new[] { "fetch_fact_contexts", "fetch_facts", "search_facts_batch" }, fake.Tools.Select(x => x.Name).Order().ToArray());
        Assert.Equal(typeof(TimelineFactReviewResult), fake.ResponseType);
    }

    [Theory]
    [InlineData("quote")]
    [InlineData("answer")]
    [InlineData("invalidation")]
    public async Task Rejects_model_fabricated_evidence_or_unknown_invalidation(string kind)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fake = new FactReviewFakeAgent();
        if (kind == "quote") fake.Result.Additions.Add(new("Invented", "x", "Person", "Not in source"));
        if (kind == "answer") fake.Result.Questions.Add(new("Who owns release?", "Invented user answer"));
        if (kind == "invalidation") fake.Result.Invalidations.Add(new(Guid.NewGuid(), "Contradiction"));
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => Planner(f, fake).PlanAsync());
        Assert.Empty(await f.Db.TimelineFactProposals.ToListAsync());
    }

    [Fact]
    public async Task Clarifications_use_stored_questions_and_actual_verbatim_answers_preserving_unanswered_questions()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var previous = await new TimelineFactService(f.Db).PrepareAsync(f.Scope, f.Actor.Id, false, f.Timeline.FactRevision,
            new([], [], [new("Who owns release?", null), new("Which milestone?", null)]));
        const string answer = "  Carol owns release.\nNot Dave.  ";
        var fake = new FactReviewFakeAgent { Result = new() { Additions = [new("Carol owns release.", "carol", "Person", "Carol owns release.", "Clarification")] } };
        var plan = await Planner(f, fake).PlanAsync(previous.Id, [new(0, answer)]);
        Assert.Equal(answer, plan.Draft.Questions[0].Answer);
        Assert.Null(plan.Draft.Questions[1].Answer);
        Assert.EndsWith("NeedsClarification", plan.Status);
        var json = JsonDocument.Parse(fake.Message!).RootElement;
        Assert.Equal(answer, json.GetProperty("user_answers")[0].GetProperty("Answer").GetString());
        Assert.Equal(previous.Id, json.GetProperty("previous_proposal").GetProperty("Id").GetGuid());
        Assert.Equal(f.Entry.Content, json.GetProperty("original_entry").GetProperty("Content").GetString());
        Assert.Equal(previous.Id, plan.PreviousProposalId);
        Assert.Equal("Pending", previous.Status);
        var prepared = await new TimelineFactService(f.Db).PrepareAsync(f.Scope, f.Actor.Id, false, plan.ExpectedRevision, plan.Draft);
        Assert.Equal(plan.SourceFingerprint, prepared.SourceFingerprint);
        Assert.Equal(answer, JsonSerializer.Deserialize<TimelineFactQuestion[]>(prepared.QuestionsJson)![0].Answer);
    }

    [Theory]
    [InlineData("fabricatedAnswer")]
    [InlineData("fabricatedQuote")]
    [InlineData("unknownIndex")]
    [InlineData("duplicateIndex")]
    [InlineData("noProposal")]
    [InlineData("foreignProposal")]
    [InlineData("staleProposal")]
    public async Task Rejects_untrusted_clarification_rounds(string kind)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var previous = await new TimelineFactService(f.Db).PrepareAsync(f.Scope, f.Actor.Id, false, f.Timeline.FactRevision,
            new([], [], [new("Who owns release?", null)]));
        var fake = new FactReviewFakeAgent();
        Guid? id = previous.Id;
        IReadOnlyList<TimelineFactAnswer> answers = [new(0, "Carol owns release.")];
        if (kind == "fabricatedAnswer") fake.Result.Questions.Add(new("Who owns release?", "Dave owns release."));
        if (kind == "fabricatedQuote") fake.Result.Additions.Add(new("Dave owns release.", "dave", "Person", "Dave owns release.", "Clarification"));
        if (kind == "unknownIndex") answers = [new(99, "Carol")];
        if (kind == "duplicateIndex") answers = [new(0, "Carol"), new(0, "Dave")];
        if (kind == "noProposal") id = null;
        if (kind == "foreignProposal") id = Guid.NewGuid();
        if (kind == "staleProposal") await f.PrepareAsync("Alice owns delivery.", "alice");
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => Planner(f, fake).PlanAsync(id, answers));
    }

    [Fact]
    public async Task Existing_fact_invalidation_is_validated_and_preparable_without_mutation()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var accepted = await f.AcceptAsync("Alice owns delivery.", "alice");
        await f.NewSourceAsync();
        f.Entry.Content = "Bob now owns delivery instead of Alice.";
        await f.Db.SaveChangesAsync();
        var replacement = new TimelineFactCandidate(f.Entry.Content, "bob", "Person", f.Entry.Content);
        var fake = new FactReviewFakeAgent { Result = new() {
            Additions = [replacement],
            Invalidations = [new(accepted.Id, "Explicit delivery ownership supersession", 0)],
            InvalidationAssessments = [TimelineFactContextReconciliationTests.Assessment(accepted, replacement, "ExplicitSupersession")] } };
        var plan = await Planner(f, fake).PlanAsync();
        Assert.Equal(accepted.Id, Assert.Single(plan.Draft.Invalidations).FactId);
        Assert.True(accepted.Valid);
        Assert.Equal(0, Assert.Single(plan.Draft.Invalidations).InvalidatingAdditionIndex);
        Assert.Contains("InvalidatingAdditionIndex", fake.Instructions);
        Assert.Contains("ask a clarification Question", fake.Instructions);
        Assert.Equal(1, await f.Db.TimelineFactProposals.CountAsync());
        var proposal = await new TimelineFactService(f.Db).PrepareAsync(f.Scope, f.Actor.Id, false, plan.ExpectedRevision, plan.Draft);
        Assert.Equal(plan.SourceFingerprint, proposal.SourceFingerprint);
        Assert.Equal(f.Entry.Id, proposal.EntryId); Assert.NotEqual(accepted.EntryId, proposal.EntryId);
        Assert.True(accepted.Valid);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Failed")]
    [InlineData("Processing")]
    public async Task Extraction_not_completed_is_reported_without_inventing_context(string state)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        f.Db.Add(new TimelineFactJob { EntryId = f.Entry.Id, State = state, WorkKey = $"extraction:{f.Entry.Id:N}",
            SourceFingerprint = TimelineFactEvidence.Fingerprint(f.Entry) });
        await f.Db.SaveChangesAsync();
        var plan = await Planner(f, new()).PlanAsync();
        Assert.Equal(state, plan.Coverage.CurrentExtractionState);
        Assert.Equal(1, plan.Coverage.EntriesWithoutCompletedExtraction);
        Assert.StartsWith("IncompleteHistory:", plan.Status);
        Assert.Empty(plan.Draft.Additions);
        Assert.Equal(state, (await f.Db.TimelineFactJobs.SingleAsync()).State);
    }

    [Fact]
    public async Task Actual_sdk_tools_are_scope_free_read_only_and_allow_bounded_followup()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var accepted = await f.AcceptAsync("Alice owns delivery.", "alice");
        var fake = new FactReviewFakeAgent();
        fake.Script = async () =>
        {
            var search = Assert.IsAssignableFrom<AIFunction>(fake.Tools.Single(x => x.Name == "search_facts_batch"));
            var fetch = Assert.IsAssignableFrom<AIFunction>(fake.Tools.Single(x => x.Name == "fetch_facts"));
            Assert.DoesNotContain("scope", search.JsonSchema.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("actor", search.JsonSchema.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("array", search.JsonSchema.GetProperty("properties").GetProperty("queries").GetProperty("type").GetString());
            var hits = await search.InvokeAsync(new AIFunctionArguments { ["queries"] = new[] { "ALICE", "ALIC" }, ["limit"] = 1 });
            var groups = JsonSerializer.SerializeToElement(hits);
            Assert.Equal(2, groups.GetArrayLength());
            Assert.Equal("ALICE", groups[0].GetProperty("query").GetString());
            Assert.Equal(accepted.Id, groups[0].GetProperty("hits")[0].GetProperty("id").GetGuid());
            Assert.Equal("ALIC", groups[1].GetProperty("query").GetString());
            Assert.Empty(groups[1].GetProperty("hits").EnumerateArray());
            Assert.Contains("Reuse seed_facts", fake.Instructions);
            Assert.Contains("distinct query strings", fake.Instructions);
            Assert.Contains($"up to {TimelineFactReviewTools.MaximumBatchQueries} queries", fake.Instructions);
            Assert.Contains($"{TimelineFactReviewTools.MaximumCalls} counted backend-read attempts total", fake.Instructions);
            Assert.Contains($"{TimelineFactReviewTools.MaximumCalls - 1} remaining at model start", fake.Instructions);
            Assert.Contains("not a count of SDK tool invocations", fake.Instructions);
            Assert.Contains($"1..{TimelineFactReviewTools.MaximumBatchQueries} query strings", search.Description);
            var fetched = await fetch.InvokeAsync(new AIFunctionArguments { ["ids"] = new[] { accepted.Id } });
            Assert.Equal(accepted.Id, JsonSerializer.SerializeToElement(fetched)[0].GetProperty("id").GetGuid());
        };
        await Planner(f, fake).PlanAsync();
        Assert.True(accepted.Valid);
        Assert.False(f.Db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData("success")]
    [InlineData("provider")]
    [InlineData("tool")]
    [InlineData("cancel")]
    public async Task Reused_planner_owns_fresh_tools_and_exactly_fifty_nine_remaining_reads_after_each_outcome(string ending)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        using var cancel = new CancellationTokenSource();
        var fake = new FactReviewFakeAgent();
        AIFunction? previousFetch = null;
        var independent = false;
        fake.Script = async () =>
        {
            var fetch = Assert.IsAssignableFrom<AIFunction>(fake.Tools.Single(x => x.Name == "fetch_facts"));
            Assert.NotSame(previousFetch, fetch);
            previousFetch = fetch;
            var search = Assert.IsAssignableFrom<AIFunction>(fake.Tools.Single(x => x.Name == "search_facts_batch"));
            // The actual maximum batch consumes twenty counted reads, not one SDK invocation.
            await search.InvokeAsync(new AIFunctionArguments
            {
                ["queries"] = Enumerable.Range(0, TimelineFactReviewTools.MaximumBatchQueries).Select(i => "query " + i).ToArray()
            });
            var remaining = TimelineFactReviewTools.MaximumCalls - 1 - TimelineFactReviewTools.MaximumBatchQueries;
            UnauthorizedAccessException? backendFailure = null;
            for (var i = 0; i < remaining; i++)
            {
                if (!independent && ending == "tool" && i == remaining - 1)
                {
                    f.Actor.IsActive = false;
                    await f.Db.SaveChangesAsync();
                    try { backendFailure = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await fetch.InvokeAsync(new() { ["ids"] = Array.Empty<Guid>() })); }
                    finally { f.Actor.IsActive = true; await f.Db.SaveChangesAsync(); }
                }
                else await fetch.InvokeAsync(new() { ["ids"] = Array.Empty<Guid>() });
            }
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await fetch.InvokeAsync(new() { ["ids"] = Array.Empty<Guid>() }));
            if (backendFailure is not null) throw backendFailure;
            if (!independent && ending == "provider") throw new InvalidDataException("offline model failure");
            if (!independent && ending == "cancel") cancel.Cancel();
        };
        var planner = Planner(f, fake);
        var error = await Record.ExceptionAsync(() => planner.PlanAsync(ct: cancel.Token));
        if (ending == "provider") Assert.IsType<InvalidDataException>(error);
        else if (ending == "tool") Assert.IsType<UnauthorizedAccessException>(error);
        else if (ending == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.Null(error);
        independent = true;
        await planner.PlanAsync();
        Assert.False(f.Db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData("duplicateInvalidation")]
    [InlineData("oversizedAdditions")]
    [InlineData("oversizedQuestions")]
    [InlineData("unsupportedKind")]
    [InlineData("emptyClaim")]
    [InlineData("blankReason")]
    public async Task Bounded_schema_rejects_malformed_model_changes(string kind)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fake = new FactReviewFakeAgent();
        var addition = new TimelineFactCandidate("Alice owns delivery.", "alice", "Person", "Alice owns delivery.");
        if (kind == "duplicateInvalidation") fake.Result.Invalidations = [new(Guid.Empty, "x"), new(Guid.Empty, "x")];
        if (kind == "oversizedAdditions") fake.Result.Additions = Enumerable.Repeat(addition, 201).ToList();
        if (kind == "oversizedQuestions") fake.Result.Questions = Enumerable.Range(0, 101).Select(i => new TimelineFactQuestion("Q" + i, null)).ToList();
        if (kind == "unsupportedKind") fake.Result.Additions.Add(addition with { SourceKind = "History" });
        if (kind == "emptyClaim") fake.Result.Additions.Add(addition with { Claim = "" });
        if (kind == "blankReason") fake.Result.Invalidations.Add(new(Guid.Empty, " "));
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => Planner(f, fake).PlanAsync());
    }

    [Fact]
    public async Task Concurrent_revision_change_stops_planning_without_saving_a_proposal()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fake = new FactReviewFakeAgent { Script = async () =>
        {
            await using var raw = f.Unguarded();
            var timeline = await raw.Set<Timeline>().SingleAsync(); timeline.FactRevision++;
            await raw.SaveChangesAsync();
        } };
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => Planner(f, fake).PlanAsync());
        Assert.Empty(await f.Db.TimelineFactProposals.ToListAsync());
    }

    [Fact]
    public async Task Exact_generated_identity_reuses_only_quote_preserving_response_order_and_questions()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await PendingGenerationProvider.GenerateAsync(f.Db, f.Entry, f.Actor.Id, f.Project.Id);
        var fake = new FactReviewFakeAgent { Result = new() { Additions = [
            new("New literal fact", "bob", "Person", "Bob owns testing."),
            new("Alice owns delivery.", "alice", "Person", "Paraphrased evidence")],
            Questions = [new("Which milestone?", null)] } };
        var plan = await Planner(f, fake).PlanAsync();
        Assert.Equal(fake.Result.Additions[0], plan.Draft.Additions[0]);
        Assert.Equal(new("Alice owns delivery.", "alice", "Person", "Alice owns delivery."), plan.Draft.Additions[1]);
        Assert.Equal("Which milestone?", Assert.Single(plan.Draft.Questions).Question);
        Assert.Equal("Generated", (await f.Db.TimelineFactProposals.SingleAsync()).Status);
        Assert.Empty(await f.Db.TimelineFacts.ToListAsync());
        Assert.False(f.Db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verified_generation_does_not_relax_actual_clarification_evidence(bool literalAnswer)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await PendingGenerationProvider.GenerateAsync(f.Db, f.Entry, f.Actor.Id, f.Project.Id);
        var previous = await new TimelineFactService(f.Db).PrepareAsync(f.Scope, f.Actor.Id, false, f.Timeline.FactRevision,
            new([], [], [new("Who owns delivery?", null)]));
        var quote = literalAnswer ? "The user confirms Alice." : "Alice owns delivery.";
        var fake = new FactReviewFakeAgent { Result = new() { Additions = [
            new("Alice owns delivery.", "alice", "Person", quote, "Clarification")] } };
        Task<TimelineFactReviewPlan> Plan() => Planner(f, fake).PlanAsync(previous.Id, [new(0, "The user confirms Alice.")]);
        if (literalAnswer) Assert.Equal(quote, Assert.Single((await Plan()).Draft.Additions).SourceQuote);
        else await Assert.ThrowsAsync<TimelineFactConflictException>(Plan);
    }

    [Theory]
    [InlineData("claim")]
    [InlineData("key")]
    [InlineData("type")]
    [InlineData("kind")]
    [InlineData("case")]
    [InlineData("space")]
    public async Task Nonexact_identity_never_repairs_invalid_quote(string change)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await PendingGenerationProvider.GenerateAsync(f.Db, f.Entry, f.Actor.Id, f.Project.Id);
        var candidate = new TimelineFactCandidate("Alice owns delivery.", "alice", "Person", "Paraphrased evidence");
        candidate = change switch {
            "claim" => candidate with { Claim = "Alice owns testing." },
            "key" => candidate with { EntityKey = "bob" },
            "type" => candidate with { EntityType = "Team" },
            "kind" => candidate with { SourceKind = "Clarification" },
            "case" => candidate with { EntityKey = "Alice" },
            _ => candidate with { Claim = "Alice owns delivery. " }
        };
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => Planner(f,
            new() { Result = new() { Additions = [candidate] } }).PlanAsync());
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("row")]
    [InlineData("deleted")]
    [InlineData("missingHash")]
    [InlineData("jobState")]
    [InlineData("jobFingerprint")]
    [InlineData("jobEntry")]
    [InlineData("jobKey")]
    [InlineData("revision")]
    [InlineData("lease")]
    [InlineData("competingJob")]
    public async Task Corrupt_current_generation_fails_before_model_even_for_literal_new_addition(string change)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await PendingGenerationProvider.GenerateAsync(f.Db, f.Entry, f.Actor.Id, f.Project.Id);
        await using (var raw = f.Unguarded())
        {
            var job = await raw.Set<TimelineFactJob>().SingleAsync();
            var addition = await raw.Set<TimelineFactAddition>().SingleAsync();
            switch (change)
            {
                case "hash": job.ExtractionResultHash = "wrong"; break;
                case "row": addition.Claim += " changed"; break;
                // Simulate a missing row without a destructive test operation.
                case "deleted": addition.ProposalId = Guid.NewGuid(); break;
                case "missingHash": job.ExtractionResultHash = null; break;
                case "jobState": job.State = "Superseded"; break;
                case "jobFingerprint": job.SourceFingerprint = "wrong"; break;
                case "jobEntry": job.EntryId = Guid.NewGuid(); break;
                case "jobKey": job.WorkKey = "wrong"; break;
                case "revision": job.ExtractionRevision++; break;
                case "lease": job.LeaseOwner = "other"; break;
                case "competingJob": raw.Add(new TimelineFactJob { EntryId = f.Entry.Id, Kind = "Extraction", State = "Pending" }); break;
            }
            await raw.SaveChangesAsync();
        }
        var fake = new FactReviewFakeAgent { Result = new() { Additions = [new("New", "bob", "Person", "Bob owns testing.")] } };
        var error = await Assert.ThrowsAsync<TimelineFactConflictException>(() => Planner(f, fake).PlanAsync());
        Assert.Equal("Generated extraction evidence is invalid.", error.Message);
        Assert.Null(fake.Message);
    }

    [Theory]
    [InlineData("ambiguous")]
    [InlineData("unsupported")]
    [InlineData("stale")]
    [InlineData("superseded")]
    public async Task Ambiguous_stale_or_unsupported_stored_evidence_cannot_repair_quote(string change)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await PendingGenerationProvider.GenerateAsync(f.Db, f.Entry, f.Actor.Id, f.Project.Id);
        await using (var raw = f.Unguarded())
        {
            var addition = await raw.Set<TimelineFactAddition>().SingleAsync();
            if (change == "ambiguous") raw.Add(new TimelineFactAddition { ProposalId = addition.ProposalId,
                Claim = addition.Claim, EntityKey = addition.EntityKey, EntityType = addition.EntityType,
                SourceKind = addition.SourceKind, SourceQuote = "Alice owns" });
            if (change == "unsupported") addition.SourceQuote = "Not literal source";
            if (change == "stale") (await raw.Set<TimelineEntry>().SingleAsync()).Content += " edited";
            if (change == "superseded") (await raw.Set<TimelineFactProposal>().SingleAsync()).Status = "Superseded";
            await raw.SaveChangesAsync();
            var job = await raw.Set<TimelineFactJob>().SingleAsync();
            job.ExtractionResultHash = TimelineFactEvidence.ResultHash((await raw.Set<TimelineFactAddition>().ToListAsync())
                .Select(TimelineFactEvidence.CandidateEvidence));
            await raw.SaveChangesAsync();
        }
        var fake = new FactReviewFakeAgent { Result = new() { Additions = [new("Alice owns delivery.", "alice", "Person", "Paraphrase")] } };
        var error = await Assert.ThrowsAsync<TimelineFactConflictException>(() => Planner(f, fake).PlanAsync());
        Assert.Equal("Addition quote is not in source or actual user answers.", error.Message);
        Assert.NotNull(fake.Message);
    }

    [Theory]
    [InlineData(200, false)]
    [InlineData(200, true)]
    [InlineData(202, false)]
    public async Task Hash_covers_entire_set_and_bounds_fail_closed(int count, bool partialHash)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await PendingGenerationProvider.GenerateAsync(f.Db, f.Entry, f.Actor.Id, f.Project.Id);
        await using (var raw = f.Unguarded())
        {
            var job = await raw.Set<TimelineFactJob>().SingleAsync();
            for (var i = 1; i < count; i++) raw.Add(new TimelineFactAddition { ProposalId = job.Id,
                Claim = "Claim " + i, EntityKey = "alice", EntityType = "Person", SourceQuote = "Alice owns delivery." });
            await raw.SaveChangesAsync();
            var rows = await raw.Set<TimelineFactAddition>().OrderByDescending(x => x.Id).ToListAsync();
            job.ExtractionResultHash = TimelineFactEvidence.ResultHash(rows.Take(partialHash ? 1 : count)
                .Select(TimelineFactEvidence.CandidateEvidence));
            await raw.SaveChangesAsync();
        }
        var fake = new FactReviewFakeAgent { Result = new() { Additions = [new("Alice owns delivery.", "alice", "Person", "Paraphrase")] } };
        if (count <= 200 && !partialHash)
            Assert.Equal("Alice owns delivery.", Assert.Single((await Planner(f, fake).PlanAsync()).Draft.Additions).SourceQuote);
        else
        {
            await Assert.ThrowsAsync<TimelineFactConflictException>(() => Planner(f, fake).PlanAsync());
            Assert.Null(fake.Message);
        }
    }

    [Fact]
    public async Task Missing_extraction_is_explicit_not_a_claim_of_no_conflicts()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var result = await Planner(f, new()).PlanAsync();
        Assert.Equal(1, result.Coverage.EntriesWithoutCompletedExtraction);
        Assert.Equal("AbsentRequiresExplicitBackfill", result.Coverage.CurrentExtractionState);
        Assert.Equal("IncompleteHistory:NoChangesProposed", result.Status);
    }
}

internal sealed class FactReviewFakeAgent : ICustomAgent
{
    public TimelineFactReviewResult Result { get; set; } = new();
    public List<AITool> Tools { get; private set; } = [];
    public string? Message { get; private set; }
    public string Instructions { get; private set; } = "";
    public Type? ResponseType { get; private set; }
    public Func<Task>? Script { get; set; }
    public event EventHandler<FunctionCallEventArgs>? FunctionCalled { add { } remove { } }
    public void Init(string agentName, string model, string instructions, List<AITool> tools)
    { Tools = tools; Instructions = instructions; }
    public Task<AgentResponse> RunAsync(string message) => throw new NotSupportedException();
    public async Task<AgentResponse<T>> RunAsync<T>(string message) where T : class
    {
        Message = message; ResponseType = typeof(T);
        if (Script is not null) await Script();
        return new AgentResponse<T>(new AgentResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(Result))), JsonSerializerOptions.Default);
    }
}
