using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.Tests.AgentTests.Support;
using Xunit;

namespace ProjectTimeline.Tests.AgentTests;

[Collection(IsolatedAgentCollection.Name)]
[Trait("Category", "LiveModel")]
public sealed class TimelineFactExtractionLiveModelTests
{
    [LiveModelFact]
    public Task Extracts_multiple_independent_claims() => ExtractAsync("multi-claim");

    [LiveModelFact]
    public Task Retains_quantities_and_units() => ExtractAsync("quantities-units");

    [LiveModelFact]
    public Task Retains_dates_conditions_and_negation() => ExtractAsync("date-condition-negation");

    [LiveModelFact]
    public Task Preserves_ambiguity_without_duplicate_padding() => ExtractAsync("ambiguity-duplicate");

    [LiveModelFact]
    public Task Empty_entry_has_no_invented_claims() => ExtractAsync("empty");

    private static async Task ExtractAsync(string caseId)
    {
        var scenario = FactAgentLiveFixtures.Extraction(caseId);
        var client = FactAgentLiveFixtures.CreateClient();
        var agent = new TimelineFactExtractionAgent(client);
        var candidates = await agent.ExtractAsync(scenario.Source, default);
        FactAgentLiveAssertions.Extraction(scenario, candidates);
    }

    [LiveModelFact]
    public async Task Different_entries_reusing_one_client_and_agent_do_not_leak_history()
    {
        var client = FactAgentLiveFixtures.CreateClient();
        var agent = new TimelineFactExtractionAgent(client);
        // The public API consumes one entry per call, not a batch. Reuse the exact agent and client.
        foreach (var id in new[] { "multi-claim", "quantities-units", "empty" })
        {
            var scenario = FactAgentLiveFixtures.Extraction(id);
            var candidates = await agent.ExtractAsync(scenario.Source, default);
            FactAgentLiveAssertions.Extraction(scenario, candidates);
            if (id != "multi-claim")
                FactAgentLiveAssertions.NoPreviousEntryMarkers(candidates);
        }
    }
}
