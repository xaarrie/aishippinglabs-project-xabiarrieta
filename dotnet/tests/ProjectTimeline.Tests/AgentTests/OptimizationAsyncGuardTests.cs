using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.AgentModels;
using ProjectTimeline.BusinessLogic.Agents.Models;
using ProjectTimeline.BusinessLogic.Agents.Tools;
using ProjectTimeline.BusinessLogic.Agents.Tracking;
using ProjectTimeline.Tests.AgentTests.Support;
using System.Text.Json;
using Xunit;

namespace ProjectTimeline.Tests.AgentTests;

// This file replaces SDK-wrapper lifetime checks with application-owned async preload checks.
public sealed class OptimizationAsyncGuardTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task Pending_failure_or_cancellation_stops_before_consumer_and_later_reads_then_allows_fresh_retry(int pausedRead, bool canceled)
    {
        var tools = new DeferredTools(pausedRead) {
            Failure = canceled ? new OperationCanceledException("Synthetic private cancellation detail")
                : new InvalidOperationException("Synthetic private repository detail")
        };
        var tracker = new FunctionCallTracker();
        var consumer = new ScriptedInfrastructureAgent { ReturnTransportDraft = true };
        var agent = new OptimizeEntryAgent(tools, consumer, tracker);
        var run = agent.RunAsync("pending preload");
        await tools.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.False(run.IsCompleted);
            Assert.Equal(pausedRead + 1, tools.Reads.Count);
            Assert.Equal(pausedRead + 1, tracker.Tools.Count);
            Assert.False(tracker.Tools[^1].Executed);
            Assert.Null(tracker.Tools[^1].Result);
            Assert.Null(tracker.Tools[^1].Exception);
            Assert.Equal(0, consumer.InitCalls);
            Assert.Equal(0, consumer.RunCalls);
        }
        finally { tools.Continue.TrySetResult(); }

        Exception error = canceled
            ? await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run)
            : await Assert.ThrowsAsync<InvalidOperationException>(() => run);
        Assert.Equal(canceled ? "Entry review canceled while reading review context."
            : "Entry review stopped because review context could not be read. Please retry.", error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(pausedRead + 1, tools.Reads.Count);
        Assert.Equal(0, consumer.InitCalls);
        Assert.Equal(0, consumer.RunCalls);
        Assert.Equal(0, consumer.Subscribers);
        Assert.Same(error, tracker.Tools[^1].Exception);
        Assert.True(tracker.Tools[^1].Executed);
        Assert.All(tracker.Tools, call => Assert.Null(call.ID));
        Assert.Null(tracker.Tools[^1].Result);

        await agent.RunAsync("retry after pending failure");
        Assert.Equal(new[] { "get_entry_ids", "fetch_entry", "fetch_entry" }, tools.Reads.Skip(pausedRead + 1));
        Assert.Equal(1, consumer.InitCalls);
        Assert.Equal(1, consumer.RunCalls);
        Assert.Equal(0, consumer.Subscribers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Same_instance_serializes_preload_and_queued_run_reloads_after_success_or_failure(bool failFirst)
    {
        var tools = new DeferredTools(0) { Failure = failFirst ? new InvalidOperationException("Synthetic failure") : null };
        var tracker = new FunctionCallTracker();
        var consumer = new ScriptedInfrastructureAgent { ReturnTransportDraft = true };
        var agent = new OptimizeEntryAgent(tools, consumer, tracker);
        var first = agent.RunAsync("first");
        await tools.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = agent.RunAsync("second");
        try
        {
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.Single(tools.Reads);
            Assert.Equal(0, consumer.InitCalls);
        }
        finally { tools.Continue.TrySetResult(); }

        if (failFirst) await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        else await first.WaitAsync(TimeSpan.FromSeconds(10));
        await second.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(failFirst ? 4 : 6, tools.Reads.Count);
        Assert.Equal(failFirst ? 1 : 2, consumer.InitCalls);
        Assert.Equal(failFirst ? 1 : 2, consumer.RunCalls);
        Assert.Equal(tools.Reads.Count, tracker.Tools.Count);
        Assert.Equal(0, consumer.Subscribers);
    }

    [Fact]
    public async Task Shared_consumer_holds_the_gate_through_model_work_and_keeps_context_events_and_trackers_isolated()
    {
        var fixtureA = ConversationFixtures.Get("software-functionality");
        var fixtureB = ConversationFixtures.Get("travel-detailed-conditions");
        var repositoryA = fixtureA.CreateRepository();
        var repositoryB = fixtureB.CreateRepository();
        var trackerA = new FunctionCallTracker();
        var trackerB = new FunctionCallTracker();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new List<FunctionCall>();
        var consumer = new ScriptedInfrastructureAgent { ReturnTransportDraft = true };
        consumer.Script = async script => {
            if (script.RunCalls == 1)
            {
                entered.TrySetResult();
                await release.Task;
            }
            var call = new FunctionCall { Name = "synthetic_consumer_event", Executed = true };
            reported.Add(call);
            script.Report(call);
        };
        var firstAgent = new OptimizeEntryAgent(repositoryA.SelectAnchor(fixtureA.TimelineId, fixtureA.CurrentEntryId), consumer, trackerA);
        var first = firstAgent.RunAsync("first original");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var secondAgent = new OptimizeEntryAgent(repositoryB.SelectAnchor(fixtureB.TimelineId, fixtureB.CurrentEntryId), consumer, trackerB);
        var second = secondAgent.RunAsync("second original");
        try
        {
            Assert.False(second.IsCompleted);
            Assert.Empty(repositoryB.Reads);
            Assert.Equal(1, consumer.InitCalls);
            Assert.Equal(1, consumer.RunCalls);
            Assert.Equal(1, consumer.Subscribers);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, consumer.InitCalls);
        Assert.Equal(2, consumer.RunCalls);
        Assert.Equal(0, consumer.Subscribers);
        Assert.Equal(3, repositoryA.Reads.Count);
        Assert.Equal(3, repositoryB.Reads.Count);
        Assert.Equal(4, trackerA.Tools.Count);
        Assert.Equal(4, trackerB.Tools.Count);
        Assert.Contains(reported[0], trackerA.Tools);
        Assert.DoesNotContain(reported[0], trackerB.Tools);
        Assert.Contains(reported[1], trackerB.Tools);
        Assert.DoesNotContain(reported[1], trackerA.Tools);
        Assert.Equal(new[] { "first original", "second original" }, consumer.Messages.Select(message => {
            using var document = JsonDocument.Parse(message);
            return document.RootElement.GetProperty("original_message").GetString();
        }));
        Assert.Equal(repositoryA.OriginalState, repositoryA.CaptureState());
        Assert.Equal(repositoryB.OriginalState, repositoryB.CaptureState());
    }

    [Fact]
    public async Task Independent_consumers_can_complete_while_an_unrelated_preload_is_pending_and_fails()
    {
        var failedTools = new DeferredTools(0) { Failure = new InvalidOperationException("Synthetic pending failure") };
        var failedConsumer = new ScriptedInfrastructureAgent { ReturnTransportDraft = true };
        var failed = new OptimizeEntryAgent(failedTools, failedConsumer);
        var first = failed.RunAsync("pending failed run");
        await failedTools.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var fixture = ConversationFixtures.Get("business-partial-answers");
        var repository = fixture.CreateRepository();
        var healthyConsumer = new ScriptedInfrastructureAgent { ReturnTransportDraft = true };
        var healthy = new OptimizeEntryAgent(repository.SelectAnchor(fixture.TimelineId, fixture.CurrentEntryId), healthyConsumer);
        try
        {
            await healthy.RunAsync("independent healthy run").WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(first.IsCompleted);
            Assert.Equal(0, failedConsumer.InitCalls);
            Assert.Equal(1, healthyConsumer.RunCalls);
        }
        finally { failedTools.Continue.TrySetResult(); }

        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        Assert.Equal(0, failedConsumer.RunCalls);
        Assert.Equal(3, repository.Reads.Count);
        Assert.Equal(repository.OriginalState, repository.CaptureState());
    }

    private sealed class DeferredTools(int pausedRead) : IOptimizationAgentTools
    {
        private readonly Guid[] ids = [Guid.NewGuid(), Guid.NewGuid()];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Reads { get; } = [];
        public Exception? Failure { get; init; }
        public async Task<List<Guid>> get_entry_ids()
        {
            await ReadAsync("get_entry_ids");
            return ids.ToList();
        }
        public async Task<string> fetch_entry(Guid entryId)
        {
            Assert.Contains(entryId, ids);
            await ReadAsync("fetch_entry");
            return $"Synthetic persisted content for {entryId}";
        }
        private async Task ReadAsync(string name)
        {
            Reads.Add(name);
            if (Reads.Count - 1 != pausedRead) return;
            Entered.TrySetResult();
            await Continue.Task;
            if (Failure is not null) throw Failure;
        }
    }
}
