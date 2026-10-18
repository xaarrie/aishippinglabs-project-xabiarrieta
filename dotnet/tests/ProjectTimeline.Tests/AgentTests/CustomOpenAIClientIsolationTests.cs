using Microsoft.Extensions.AI;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.Tracking;
using ProjectTimeline.Tests.AgentTests.Support;
using System.Text.Json;
using Xunit;

namespace ProjectTimeline.Tests.AgentTests;

[Collection(IsolatedAgentCollection.Name)]
public sealed class CustomOpenAIClientIsolationTests
{
    [Fact]
    public void Factory_supports_explicit_isolation_and_credential_free_injected_transport()
    {
        Assert.Contains(typeof(CustomOpenAIClient).GetConstructors(), constructor =>
            constructor.GetParameters().Select(parameter => parameter.ParameterType)
                .SequenceEqual([typeof(IChatClient), typeof(bool)]));
        Assert.Contains(typeof(CustomOpenAIClient).GetConstructors(), constructor =>
            constructor.GetParameters().Select(parameter => parameter.ParameterType)
                .SequenceEqual([typeof(bool)]));
    }

    [Fact]
    public void Null_injected_transport_is_rejected_without_consulting_credentials()
        => Assert.Throws<ArgumentNullException>(() => new CustomOpenAIClient((IChatClient)null!, isolateHarnessFacilities: true));

    [Fact]
    public async Task All_factories_use_injected_transport_and_isolated_harness_has_only_explicit_tools()
    {
        using var transport = new HarnessScriptedTransport([]);
        var provider = new CustomOpenAIClient(transport, isolateHarnessFacilities: true);
        Assert.Same(transport, provider.GetChatClient("not-a-provider-model"));
        await provider.CreateAgent("not-a-provider-model", "plain", "plain instructions", []).RunAsync("wire control");
        Assert.Equal(1, transport.Requests);

        using var harnessTransport = new HarnessScriptedTransport([]);
        var harnessProvider = new CustomOpenAIClient(harnessTransport, isolateHarnessFacilities: true);
        var tool = AIFunctionFactory.Create(() => "synthetic", "explicit_context");
        await harnessProvider.CreateHarnessAgent("not-a-provider-model", "isolated", "isolated instructions", [tool])
            .RunAsync("wire control");
        Assert.Equal(1, harnessTransport.Requests);
        var options = Assert.Single(harnessTransport.Options);
        Assert.Equal(["explicit_context"], options.Tools!.OfType<AIFunction>().Select(function => function.Name));
        Assert.Single(options.Tools!); // No memory, skills, todo, mode, file access or hosted web tools.
        Assert.DoesNotContain("agent-file-memory", options.Instructions ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SKILL.md", options.Instructions ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("list", 1)]
    [InlineData("first-fetch", 2)]
    [InlineData("later-fetch", 3)]
    [InlineData("success", 3)]
    public async Task Actual_production_harness_receives_preloaded_context_or_makes_zero_transport_requests(string point, int reads)
    {
        var fixture = ConversationFixtures.Get("travel-detailed-conditions");
        var repository = fixture.CreateRepository();
        var history = fixture.Entries.Single(entry => entry.Sequence == 20).Id;
        var failure = new InvalidOperationException("Synthetic private driver detail; never expose this.");
        if (point == "list") repository.IdListFailure = failure;
        else if (point != "success")
            repository.EntryFailures[point == "first-fetch" ? history : fixture.CurrentEntryId] = failure;
        // No scripted history calls: the actual optimizer must prepare context before the harness exists.
        using var transport = new HarnessScriptedTransport([]);
        var provider = new CustomOpenAIClient(transport, isolateHarnessFacilities: true);
        var tracker = new FunctionCallTracker();
        var optimizer = new OptimizeEntryAgent(repository.SelectAnchor(fixture.TimelineId, fixture.CurrentEntryId),
            new LocalCustomHarnessAgent(provider), tracker);
        const string original = "Synthetic wire control — not a semantic evaluation.";

        if (point == "success")
        {
            var response = await optimizer.RunAsync(original);
            Assert.Equal(HarnessScriptedTransport.WireDraft.Entry.Title, response.Result.Entry.Title);
            Assert.Equal(1, transport.Requests);
            var options = Assert.Single(transport.Options);
            Assert.Empty(options.Tools ?? []);
            var userMessage = Assert.Single(Assert.Single(transport.Messages), message => message.Role == ChatRole.User);
            using var document = JsonDocument.Parse(userMessage.Text);
            Assert.Equal(original, document.RootElement.GetProperty("original_message").GetString());
            var entries = document.RootElement.GetProperty("review_context").GetProperty("entries").EnumerateArray().ToArray();
            Assert.Equal(new[] { history, fixture.CurrentEntryId }, entries.Select(entry => entry.GetProperty("entry_id").GetGuid()));
            Assert.Equal(new[] { history, fixture.CurrentEntryId }.Select(id => fixture.Entries.Single(entry => entry.Id == id).Content),
                entries.Select(entry => entry.GetProperty("content").GetString()));
            Assert.All(tracker.Tools, call => { Assert.True(call.Executed); Assert.Null(call.Exception); });
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => optimizer.RunAsync(original));
            Assert.Equal("Entry review stopped because review context could not be read. Please retry.", error.Message);
            Assert.Null(error.InnerException);
            Assert.Equal(0, transport.Requests);
            Assert.Empty(transport.Options);
            Assert.Empty(transport.Messages);
            var failed = Assert.Single(tracker.Tools, call => call.Exception is not null);
            Assert.Same(error, failed.Exception);
            Assert.True(failed.Executed);
            Assert.Null(failed.Result);
            Assert.DoesNotContain("private driver", failed.Exception!.ToString());
        }
        Assert.Equal(reads, repository.Reads.Count);
        Assert.Equal(new[] { "get_entry_ids", "fetch_entry", "fetch_entry" }.Take(reads), repository.Reads.Select(read => read.Function));
        Assert.Equal(new[] { history, fixture.CurrentEntryId }.Take(reads - 1), repository.Reads.Skip(1).Select(read => read.EntryId!.Value));
        Assert.Equal(reads, tracker.Tools.Count);
        Assert.All(tracker.Tools, call => Assert.Null(call.ID)); // Application calls have no invented SDK IDs.
        Assert.Equal(repository.OriginalState, repository.CaptureState());
    }
}
