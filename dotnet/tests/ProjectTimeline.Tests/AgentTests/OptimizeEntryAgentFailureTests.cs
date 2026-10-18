using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.AgentModels;
using ProjectTimeline.BusinessLogic.Agents.Tracking;
using ProjectTimeline.Tests.AgentTests.Support;
using Xunit;

namespace ProjectTimeline.Tests.AgentTests;

// Deterministic failure-channel tests, not review-quality evaluations.
public sealed class OptimizeEntryAgentFailureTests
{
    private const string UnsafeDiagnostic = "SQL SELECT secret FROM credentials; Password=synthetic-only; at Repository.Read()";
    private const string SafeError = "Entry review stopped because review context could not be read. Please retry.";
    private const string SafeCancellation = "Entry review canceled while reading review context.";

    [Theory]
    [InlineData("list", 1, Status.Ready)]
    [InlineData("first-fetch", 2, Status.Ready)]
    [InlineData("later-fetch", 3, Status.Ready)]
    [InlineData("list", 1, Status.NeedsClarification)]
    [InlineData("first-fetch", 2, Status.NeedsClarification)]
    [InlineData("later-fetch", 3, Status.NeedsClarification)]
    public async Task Failed_preload_stops_before_any_consumer_call_and_records_only_actual_attempts(string point, int reads, Status status)
    {
        var fixture = ConversationFixtures.Get("travel-detailed-conditions");
        var repository = fixture.CreateRepository();
        ConfigureFailure(repository, fixture.CurrentEntryId, point, new InvalidOperationException(UnsafeDiagnostic));
        var tracker = new FunctionCallTracker();
        var consumer = new ScriptedInfrastructureAgent { ReturnTransportDraft = true, TransportStatus = status };
        var agent = new OptimizeEntryAgent(repository.SelectAnchor(fixture.TimelineId, fixture.CurrentEntryId), consumer, tracker);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync("synthetic failure transport"));

        AssertSafe(error);
        Assert.Equal(0, consumer.InitCalls);
        Assert.Equal(0, consumer.RunCalls);
        Assert.Equal(0, consumer.DraftsReturned);
        Assert.Equal(0, consumer.Subscribers);
        Assert.Empty(consumer.Messages);
        Assert.Equal(reads, repository.Reads.Count);
        Assert.Equal(new[] { "get_entry_ids", "fetch_entry", "fetch_entry" }.Take(reads), repository.Reads.Select(read => read.Function));
        var expectedIds = fixture.Entries.Where(entry => entry.TimelineId == fixture.TimelineId && entry.Sequence is 20 or 30)
            .OrderBy(entry => entry.Sequence).Select(entry => entry.Id).ToArray();
        Assert.Equal(expectedIds.Take(reads - 1), repository.Reads.Skip(1).Select(read => read.EntryId!.Value));
        Assert.Equal(reads, tracker.Tools.Count);
        Assert.All(tracker.Tools, call => { Assert.Null(call.ID); Assert.True(call.Executed); });
        Assert.Equal(repository.Reads.Select(read => read.Function), tracker.Tools.Select(call => call.Name));
        Assert.Empty(tracker.Tools[0].Args);
        for (var index = 1; index < reads; index++)
        {
            var argument = Assert.Single(tracker.Tools[index].Args);
            Assert.Equal("entryId", argument.Name);
            Assert.Equal(expectedIds[index - 1], Assert.IsType<Guid>(argument.Value));
        }
        Assert.All(tracker.Tools.Take(reads - 1), call => { Assert.Null(call.Exception); Assert.NotNull(call.Result); });
        Assert.Same(error, tracker.Tools[^1].Exception);
        Assert.Null(tracker.Tools[^1].Result);
        Assert.Equal(repository.OriginalState, repository.CaptureState());
    }

    [Theory]
    [InlineData("list", 1)]
    [InlineData("first-fetch", 2)]
    [InlineData("later-fetch", 3)]
    public async Task Same_agent_reloads_the_entire_window_after_a_failed_attempt(string point, int failedReads)
    {
        var fixture = ConversationFixtures.Get("business-partial-answers");
        var repository = fixture.CreateRepository();
        ConfigureFailure(repository, fixture.CurrentEntryId, point, new InvalidOperationException(UnsafeDiagnostic));
        var tracker = new FunctionCallTracker();
        var consumer = new ScriptedInfrastructureAgent { ReturnTransportDraft = true };
        var agent = new OptimizeEntryAgent(repository.SelectAnchor(fixture.TimelineId, fixture.CurrentEntryId), consumer, tracker);
        AssertSafe(await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync("first run")));
        repository.IdListFailure = null;
        repository.EntryFailures.Clear();

        var response = await agent.RunAsync("second run");

        Assert.Equal(Status.Ready, response.Result.Status); // Synthetic infrastructure response only.
        Assert.Equal(failedReads + 3, repository.Reads.Count);
        Assert.Equal(["get_entry_ids", "fetch_entry", "fetch_entry"], repository.Reads.Skip(failedReads).Select(read => read.Function));
        Assert.Equal(failedReads + 3, tracker.Tools.Count);
        Assert.Single(tracker.Tools, call => call.Exception is not null);
        Assert.All(tracker.Tools.Skip(failedReads), call => { Assert.Null(call.ID); Assert.Null(call.Exception); Assert.True(call.Executed); });
        Assert.Equal(1, consumer.InitCalls);
        Assert.Equal(1, consumer.RunCalls);
        Assert.Equal(0, consumer.Subscribers);
        Assert.Equal(repository.OriginalState, repository.CaptureState());
    }

    [Theory]
    [InlineData("list", 1)]
    [InlineData("first-fetch", 2)]
    [InlineData("later-fetch", 3)]
    public async Task Canceled_reads_are_sanitized_and_never_initialize_the_consumer(string point, int reads)
    {
        var fixture = ConversationFixtures.Get("software-functionality");
        var repository = fixture.CreateRepository();
        ConfigureFailure(repository, fixture.CurrentEntryId, point, new OperationCanceledException(UnsafeDiagnostic));
        var tracker = new FunctionCallTracker();
        var consumer = new ScriptedInfrastructureAgent { ReturnTransportDraft = true };
        var agent = new OptimizeEntryAgent(repository.SelectAnchor(fixture.TimelineId, fixture.CurrentEntryId), consumer, tracker);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.RunAsync("canceled preload"));

        Assert.Equal(SafeCancellation, error.Message);
        Assert.DoesNotContain(UnsafeDiagnostic, error.ToString());
        Assert.Null(error.InnerException);
        Assert.Equal(reads, repository.Reads.Count);
        Assert.Equal(reads, tracker.Tools.Count);
        Assert.Same(error, tracker.Tools[^1].Exception);
        Assert.Null(tracker.Tools[^1].Result);
        Assert.All(tracker.Tools, call => { Assert.Null(call.ID); Assert.True(call.Executed); });
        Assert.Equal(0, consumer.InitCalls);
        Assert.Equal(0, consumer.RunCalls);
        Assert.Equal(0, consumer.Subscribers);
        Assert.Equal(repository.OriginalState, repository.CaptureState());
    }

    [Theory]
    [InlineData("init")]
    [InlineData("run")]
    public async Task Downstream_errors_keep_their_boundary_and_release_the_gate_for_fresh_retrieval(string boundary)
    {
        var fixture = ConversationFixtures.Get("software-functionality");
        var repository = fixture.CreateRepository();
        var tracker = new FunctionCallTracker();
        var failure = new InvalidOperationException("Synthetic consumer error, not a context read failure.");
        var consumer = new ScriptedInfrastructureAgent { ReturnTransportDraft = true };
        if (boundary == "init") consumer.OnInit = () => throw failure;
        else consumer.Script = _ => throw failure;
        var agent = new OptimizeEntryAgent(repository.SelectAnchor(fixture.TimelineId, fixture.CurrentEntryId), consumer, tracker);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync("downstream failure"));

        Assert.Same(failure, error);
        Assert.Equal(3, repository.Reads.Count);
        Assert.All(tracker.Tools, call => { Assert.True(call.Executed); Assert.Null(call.Exception); });
        Assert.Equal(0, consumer.Subscribers);
        consumer.OnInit = null;
        consumer.Script = null;
        await agent.RunAsync("retry after downstream failure");
        Assert.Equal(6, repository.Reads.Count);
        Assert.Equal(6, tracker.Tools.Count);
        Assert.Equal(2, consumer.InitCalls);
        Assert.Equal(boundary == "init" ? 1 : 2, consumer.RunCalls);
        Assert.Equal(0, consumer.Subscribers);
        Assert.Equal(repository.OriginalState, repository.CaptureState());
    }

    [Fact]
    public async Task Prompt_retains_blocking_clarifications_unique_context_and_stable_partial_answers()
    {
        var consumer = new ScriptedInfrastructureAgent { ReturnTransportDraft = true };
        var agent = new OptimizeEntryAgent(contextTools: null, agent: consumer);
        await agent.RunAsync("prompt inspection only");
        foreach (var text in new[] {
            "Ask ONLY when faithful interpretation is blocked", "absent deadline, leader, acceptance criteria or project digest",
            "unique reference", "prior-context provenance", "Do not duplicate teams", "invent individual roles",
            "retain unanswered stable question IDs", "dependent pendingQuestionIds", "only explicit answers"
        })
            Assert.Contains(text, consumer.Instructions);
        Assert.DoesNotContain("When in doubt between asking and assuming, ask", consumer.Instructions);
        Assert.DoesNotContain("leave the field null and ask about it", consumer.Instructions);
    }

    private static void ConfigureFailure(FakeConversationRepository repository, Guid currentEntryId, string point, Exception failure)
    {
        if (point == "list") repository.IdListFailure = failure;
        else repository.EntryFailures[point == "first-fetch"
            ? repository.Entries.Single(entry => entry.Sequence == 20).Id : currentEntryId] = failure;
    }

    private static void AssertSafe(Exception error)
    {
        Assert.Equal(SafeError, error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(UnsafeDiagnostic, error.ToString());
    }
}
