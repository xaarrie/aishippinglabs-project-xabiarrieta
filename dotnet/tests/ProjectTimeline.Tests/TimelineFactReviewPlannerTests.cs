using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.AgentModels;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.BusinessLogic.Agents.Models;
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
        Assert.Equal(new[] { "fetch_facts", "search_facts" }, fake.Tools.Select(x => x.Name).Order().ToArray());
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
        var fake = new FactReviewFakeAgent { Result = new() {
            Additions = [new("Bob owns testing.", "bob", "Person", "Bob owns testing.")],
            Invalidations = [new(accepted.Id, "Source update requires explicit review", 0)] } };
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
            var search = Assert.IsAssignableFrom<AIFunction>(fake.Tools.Single(x => x.Name == "search_facts"));
            var fetch = Assert.IsAssignableFrom<AIFunction>(fake.Tools.Single(x => x.Name == "fetch_facts"));
            Assert.DoesNotContain("scope", search.JsonSchema.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("actor", search.JsonSchema.ToString(), StringComparison.OrdinalIgnoreCase);
            var hits = await search.InvokeAsync(new AIFunctionArguments { ["terms"] = "ALIC", ["limit"] = 1 });
            Assert.Equal(accepted.Id, JsonSerializer.SerializeToElement(hits)[0].GetProperty("id").GetGuid());
            var fetched = await fetch.InvokeAsync(new AIFunctionArguments { ["ids"] = new[] { accepted.Id } });
            Assert.Equal(accepted.Id, JsonSerializer.SerializeToElement(fetched)[0].GetProperty("id").GetGuid());
        };
        await Planner(f, fake).PlanAsync();
        Assert.True(accepted.Valid);
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
