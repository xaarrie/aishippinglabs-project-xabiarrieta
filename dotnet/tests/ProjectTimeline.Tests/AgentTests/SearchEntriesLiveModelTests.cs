using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.Tests.AgentTests.Support;
using Xunit;

namespace ProjectTimeline.Tests.AgentTests;

[Collection(IsolatedAgentCollection.Name)]
[Trait("Category", "LiveModel")]
public sealed class SearchEntriesLiveModelTests
{
    [LiveModelFact]
    public Task Answers_from_accepted_scoped_evidence_with_sources_and_coverage() => SearchAsync("answerable");

    [LiveModelFact]
    public Task Matching_but_insufficient_evidence_does_not_invent_a_budget() => SearchAsync("insufficient");

    [LiveModelFact]
    public Task Unrelated_foreign_scope_facts_cannot_supply_an_answer() => SearchAsync("unrelated");

    private static async Task SearchAsync(string caseId)
    {
        var client = FactAgentLiveFixtures.CreateClient();
        await using var f = await FactAgentLiveDatabase.CreateAsync();
        var accepted = await f.AcceptAsync("Nora owns Atlas delivery.");
        var foreign = await f.AcceptAsync("Orion has a violet badge and a budget of 9000 euros.", f.ForeignTimeline);
        await f.EntryAsync("Atlas delivery has a budget of 9000 euros. This raw entry is not accepted evidence.");
        var agent = new SearchEntriesAgent(f.Db, new(f.Project.Id, f.Timeline.Id), f.Actor.Id, false,
            () => new LocalCustomHarnessAgent(client));
        var question = caseId switch
        {
            "answerable" => "Who owns Atlas delivery?",
            "insufficient" => "What is the budget for Atlas delivery?",
            "unrelated" => "What is Orion's badge color?",
            _ => throw new ArgumentOutOfRangeException(nameof(caseId))
        };
        var result = await agent.RunAsync(question);
        Assert.DoesNotContain(result.Facts, x => x.Id == foreign.Id);
        Assert.DoesNotContain(result.Citations, x => x.FactId == foreign.Id);
        Assert.Equal(1, result.Coverage.AcceptedValidFacts);
        Assert.Equal(2, result.Coverage.Entries);
        if (caseId == "unrelated")
        {
            // Production's deterministic no-hit path intentionally avoids synthesis/model calls.
            Assert.Empty(result.Facts);
            Assert.Empty(result.Citations);
            Assert.Contains("No accepted matching facts", result.Content, StringComparison.Ordinal);
            Assert.NotEmpty(result.Coverage.Warnings);
        }
        else
        {
            FactAgentLiveAssertions.GroundedSearch(result, accepted.Id, accepted.EntryId, accepted.CreationReviewId);
            if (caseId == "answerable")
            {
                Assert.True(FactAgentLiveAssertions.Matches(result.Content, "Nora"));
                Assert.True(FactAgentLiveAssertions.Matches(result.Content, "(?:own|responsib|charge)"));
            }
            else
            {
                Assert.True(FactAgentLiveAssertions.Matches(result.Content, "budget"));
                Assert.True(FactAgentLiveAssertions.Matches(result.Content, "(?:not|no |unknown|insufficient|unavailable|cannot|can't|doesn't)"));
            }
        }
        Assert.False(f.Db.ChangeTracker.HasChanges());
    }
}
