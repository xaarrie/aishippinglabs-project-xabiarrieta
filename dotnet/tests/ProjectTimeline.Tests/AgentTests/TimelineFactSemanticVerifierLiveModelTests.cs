using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.Tests.AgentTests.Support;
using Xunit;

namespace ProjectTimeline.Tests.AgentTests;

[Collection(IsolatedAgentCollection.Name)]
[Trait("Category", "LiveModel")]
public sealed class TimelineFactSemanticVerifierLiveModelTests
{
    [LiveModelFact]
    public async Task Supported_batch_has_complete_assessments_and_source_bound_evidence()
    {
        var client = FactAgentLiveFixtures.CreateClient();
        var source = FactAgentLiveFixtures.VerificationSource;
        var candidates = FactAgentLiveFixtures.SupportedBatch();
        var result = await new TimelineFactSemanticVerifierAgent(client).VerifyAsync(source, candidates, default);
        FactAgentLiveAssertions.SupportedBatch(source, candidates, result);
    }

    [LiveModelFact]
    public Task Rejects_changed_quantity() => RejectAsync("In project Atlas, Nora ordered 25 sensors on 12 March 2027.");

    [LiveModelFact]
    public Task Rejects_changed_date() => RejectAsync("In project Atlas, Nora ordered 24 sensors on 13 March 2027.");

    [LiveModelFact]
    public Task Rejects_changed_actor() => RejectAsync("In project Atlas, Liam ordered 24 sensors on 12 March 2027.");

    [LiveModelFact]
    public Task Rejects_changed_negation() => RejectAsync("In project Atlas, Liam approved shipment S-4.");

    [LiveModelFact]
    public Task Rejects_unsupported_warranty() => RejectAsync("In project Atlas, Nora ordered 24 sensors with a lifetime warranty.");

    private static async Task RejectAsync(string claim)
    {
        var client = FactAgentLiveFixtures.CreateClient();
        var candidates = FactAgentLiveFixtures.SupportedBatch();
        // Keep a supported prefix: the verifier must assess the whole batch, not just the first item.
        candidates[1] = candidates[0] with { Claim = claim };
        var error = await Assert.ThrowsAsync<TimelineFactProviderException>(() =>
            new TimelineFactSemanticVerifierAgent(client).VerifyAsync(FactAgentLiveFixtures.VerificationSource, candidates, default));
        Assert.Equal("InvalidResponse", error.Code);
        Assert.Equal("SemanticUnsupported", error.DiagnosticCode); // Transport/schema/refusal failures do NOT pass.
    }
}
