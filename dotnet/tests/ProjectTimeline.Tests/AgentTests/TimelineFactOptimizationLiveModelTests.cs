using Microsoft.EntityFrameworkCore;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.Tests.AgentTests.Support;
using Xunit;

namespace ProjectTimeline.Tests.AgentTests;

[Collection(IsolatedAgentCollection.Name)]
[Trait("Category", "LiveModel")]
public sealed class TimelineFactOptimizationLiveModelTests
{
    [LiveModelFact]
    public async Task Ambiguous_conflict_requires_clarification_then_proposes_supported_replacement_without_writes()
    {
        var client = FactAgentLiveFixtures.CreateClient();
        await using var f = await FactAgentLiveDatabase.CreateAsync();
        var old = await f.AcceptAsync("Nora is the sole owner of Atlas delivery.");
        const string update = "Liam may take over Atlas delivery, but it is undecided whether Liam replaces Nora or merely assists Nora.";
        var entry = await f.EntryAsync(update);
        await f.GenerateAsync(entry, FactAgentLiveFixtures.Candidate(update, "atlas-delivery", "Responsibility"));
        var agent = new TimelineFactOptimizationAgent(new LocalCustomHarnessAgent(client));
        var planner = new TimelineFactReviewPlanner(f.Db, f.Scope(entry), f.Actor.Id, false, agent);
        var revision = f.Timeline.FactRevision;
        var proposals = await f.Db.TimelineFactProposals.CountAsync();
        var first = await planner.PlanAsync();
        Assert.Empty(first.Draft.Invalidations); // No newest-wins rule for an undecided change.
        Assert.Contains(first.Draft.Questions, q => string.IsNullOrWhiteSpace(q.Answer)
            && FactAgentLiveAssertions.Matches(q.Question, "(?:Liam|Nora|Atlas)")
            && FactAgentLiveAssertions.Matches(q.Question, "(?:replac|own|assist|responsib|take over)"));
        Assert.EndsWith("NeedsClarification", first.Status);
        Assert.Equal("Completed", first.Coverage.CurrentExtractionState);
        Assert.Equal(revision, first.ExpectedRevision);
        Assert.All(first.Draft.Additions, a =>
        {
            Assert.Equal("Entry", a.SourceKind);
            Assert.Contains(a.SourceQuote, update, StringComparison.Ordinal);
            Assert.True(FactAgentLiveAssertions.Matches(a.Claim, "(?:may|might|undecided|whether|possib|uncertain)"));
        });
        await AssertReadOnlyAsync(proposals, revision);

        // Only the deterministic service persists the proposal. The target agent has read-only tools.
        var service = new TimelineFactService(f.Db);
        var stored = await service.PrepareAsync(f.Scope(entry), f.Actor.Id, false, first.ExpectedRevision, first.Draft);
        const string answer = "Liam is the sole owner of Atlas delivery. This replaces Nora's previous ownership; invalidate the old Nora ownership fact.";
        var answers = first.Draft.Questions.Select((_, index) => new TimelineFactAnswer(index, answer)).ToArray();
        revision = f.Timeline.FactRevision;
        proposals = await f.Db.TimelineFactProposals.CountAsync();
        var clarified = await planner.PlanAsync(stored.Id, answers);
        var invalidation = Assert.Single(clarified.Draft.Invalidations);
        Assert.Equal(old.Id, invalidation.FactId);
        Assert.False(string.IsNullOrWhiteSpace(invalidation.Reason));
        Assert.NotNull(invalidation.InvalidatingAdditionIndex);
        Assert.InRange(invalidation.InvalidatingAdditionIndex.Value, 0, clarified.Draft.Additions.Count - 1);
        var replacement = clarified.Draft.Additions[invalidation.InvalidatingAdditionIndex.Value];
        Assert.Equal("Clarification", replacement.SourceKind);
        Assert.Contains(replacement.SourceQuote, answer, StringComparison.Ordinal);
        Assert.True(FactAgentLiveAssertions.Matches(replacement.Claim, "Liam"));
        Assert.True(FactAgentLiveAssertions.Matches(replacement.Claim, "Atlas"));
        Assert.True(FactAgentLiveAssertions.Matches(replacement.Claim, "delivery"));
        Assert.True(FactAgentLiveAssertions.Matches(replacement.Claim, "(?:sole|only|exclusive).*own|own.*(?:sole|only|exclusive)"));
        Assert.DoesNotMatch("(?i)may|might|undecided", replacement.Claim);
        Assert.All(clarified.Draft.Questions, q => Assert.Equal(answer, q.Answer));
        Assert.EndsWith("DraftForReview", clarified.Status);
        Assert.Equal(stored.Id, clarified.PreviousProposalId);
        await AssertReadOnlyAsync(proposals, revision);

        async Task AssertReadOnlyAsync(int expectedProposals, long expectedRevision)
        {
            Assert.False(f.Db.ChangeTracker.HasChanges());
            Assert.Equal(expectedProposals, await f.Db.TimelineFactProposals.CountAsync());
            Assert.Equal(expectedRevision, await f.Db.Timelines.AsNoTracking().Where(x => x.Id == f.Timeline.Id).Select(x => x.FactRevision).SingleAsync());
            Assert.True((await f.Db.TimelineFacts.AsNoTracking().SingleAsync()).Valid);
            Assert.Equal(update, (await f.Db.TimelineEntries.AsNoTracking().SingleAsync(x => x.Id == entry.Id)).Content);
        }
    }
}
