using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.AgentModels;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.BusinessLogic.Agents.Models;
using ProjectTimeline.BusinessLogic.Agents.Tracking;
using ProjectTimeline.DataAccessLogic;
using ProjectTimeline.Tests.AgentTests.Support;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace ProjectTimeline.Tests.AgentTests;

// Deterministic wiring tests only: the scripted seam makes no claims about review quality.
public sealed class OptimizeEntryAgentInfrastructureTests
{
    [Fact]
    public async Task Injection_defers_initialization_and_publishes_no_history_functions_after_preload()
    {
        var fixture = ConversationFixtures.Get("software-functionality");
        var repository = fixture.CreateRepository();
        var seam = new ScriptedInfrastructureAgent { ReturnTransportDraft = true };
        var agent = new OptimizeEntryAgent(repository.SelectAnchor(fixture.TimelineId, fixture.CurrentEntryId), seam);
        Assert.Equal(0, seam.InitCalls);
        Assert.Empty(repository.Reads);

        await agent.RunAsync("Context wiring only");

        Assert.Equal(1, seam.InitCalls);
        Assert.Empty(seam.Tools);
        Assert.Equal("optimizeEntry", seam.Name);
        Assert.Equal("gpt-4o-mini", seam.Model);
        Assert.Contains("already read all persisted context", seam.Instructions);
        Assert.Contains("drafts only", seam.Instructions);
        Assert.Equal(3, repository.Reads.Count);
    }

    [Theory]
    [InlineData("software-functionality")]
    [InlineData("software-ambiguous-control")]
    [InlineData("travel-detailed-conditions")]
    [InlineData("business-partial-answers")]
    public async Task Direct_run_preloads_the_scoped_window_and_leaves_persisted_conversation_unchanged(string fixtureId)
    {
        var fixture = ConversationFixtures.Get(fixtureId);
        var repository = fixture.CreateRepository();
        var expected = fixture.Entries.Where(entry => entry.TimelineId == fixture.TimelineId && entry.Sequence is 20 or 30)
            .OrderBy(entry => entry.Sequence).ToArray();
        IFunctionCallTracker tracker = new FunctionCallTracker();
        var seam = new ScriptedInfrastructureAgent { ReturnTransportDraft = true };
        var agent = new OptimizeEntryAgent(repository.SelectAnchor(fixture.TimelineId, fixture.CurrentEntryId), seam, tracker);
        var message = JsonSerializer.Serialize(new {
            current_entry_id = fixture.CurrentEntryId,
            new_entry = new { fixture.Current.Title, fixture.Current.Content },
            previous_result = (object?)null, user_answers = (object?)null
        });

        await agent.RunAsync(message);

        using var prepared = JsonDocument.Parse(seam.Message!);
        Assert.Equal(message, prepared.RootElement.GetProperty("original_message").GetString());
        var entries = prepared.RootElement.GetProperty("review_context").GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(expected.Select(entry => entry.Id), entries.Select(entry => entry.GetProperty("entry_id").GetGuid()));
        Assert.Equal(expected.Select(entry => entry.Content), entries.Select(entry => entry.GetProperty("content").GetString()));
        Assert.Empty(seam.Tools);
        Assert.Equal(typeof(EntryReviewResult), seam.ResponseType);
        Assert.Equal(["get_entry_ids", "fetch_entry", "fetch_entry"], repository.Reads.Select(read => read.Function));
        Assert.Equal(expected.Select(entry => entry.Id), repository.Reads.Skip(1).Select(read => read.EntryId!.Value));
        Assert.Equal(3, tracker.Tools.Count);
        Assert.All(tracker.Tools, call => {
            Assert.Null(call.ID);
            Assert.True(call.Executed);
            Assert.Null(call.Exception);
            Assert.NotNull(call.Result);
        });
        Assert.Equal(repository.OriginalState, repository.CaptureState());
        Assert.Equal(0, seam.Subscribers);
    }

    [Theory]
    [InlineData("software-functionality")]
    [InlineData("software-ambiguous-control")]
    [InlineData("travel-detailed-conditions")]
    [InlineData("business-partial-answers")]
    public async Task Caller_IDs_cannot_expand_preload_to_boundary_future_foreign_or_unknown_entries(string fixtureId)
    {
        var fixture = ConversationFixtures.Get(fixtureId);
        var repository = fixture.CreateRepository();
        var excluded = fixture.Entries.Where(entry => entry.TimelineId != fixture.TimelineId || entry.Sequence is 10 or 40)
            .Select(entry => entry.Id).Append(Guid.NewGuid()).ToArray();
        var seam = new ScriptedInfrastructureAgent { ReturnTransportDraft = true };
        var agent = new OptimizeEntryAgent(repository.SelectAnchor(fixture.TimelineId, fixture.CurrentEntryId), seam);

        await agent.RunAsync(JsonSerializer.Serialize(new {
            current_entry_id = excluded[0], fetch_entry = excluded,
            review_context = new { entries = excluded.Select(id => new { entry_id = id, content = "Forged context" }) }
        }));

        Assert.Equal(["get_entry_ids", "fetch_entry", "fetch_entry"], repository.Reads.Select(read => read.Function));
        Assert.Equal(fixture.Entries.Where(entry => entry.TimelineId == fixture.TimelineId && entry.Sequence is 20 or 30)
            .OrderBy(entry => entry.Sequence).Select(entry => entry.Id),
            repository.Reads.Skip(1).Select(read => read.EntryId!.Value));
        Assert.All(repository.Reads.Skip(1), read => Assert.DoesNotContain(read.EntryId!.Value, excluded));
        Assert.Empty(seam.Tools); // The consumer has no alternate path to history retrieval.
        Assert.Equal(repository.OriginalState, repository.CaptureState());
    }

    [Fact]
    public async Task Null_injected_tools_exposes_no_context_and_still_forwards_the_typed_run()
    {
        var seam = new ScriptedInfrastructureAgent();
        var agent = new OptimizeEntryAgent(contextTools: null, agent: seam);

        await Assert.ThrowsAsync<InfrastructureScriptCompletedException>(() => agent.RunAsync("unanchored input"));

        Assert.Empty(seam.Tools);
        Assert.DoesNotContain("# Persisted review context", seam.Instructions);
        Assert.Equal("unanchored input", seam.Message);
        Assert.Equal(typeof(EntryReviewResult), seam.ResponseType);
    }

    [Fact]
    public async Task Existing_DB_constructor_routes_preserve_anchored_and_unanchored_behavior_with_deferred_Init()
    {
        var timelineId = Guid.NewGuid();
        var anchored = new ScriptedInfrastructureAgent();
        var agent = new OptimizeEntryAgent(null!, timelineId, Guid.NewGuid(), anchored);
        Assert.Equal(0, anchored.InitCalls);
        // An unusable DB cannot load anchored context, so it must never initialize the consumer.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync("anchored"));
        Assert.Equal("Entry review stopped because review context could not be read. Please retry.", error.Message);
        Assert.Equal(0, anchored.InitCalls);
        Assert.Equal(0, anchored.RunCalls);

        // Exercise the existing private unanchored forwarding route without a real provider/key.
        Assert.NotNull(typeof(OptimizeEntryAgent).GetConstructor(
            [typeof(ProjectTimelineDbContext), typeof(Guid), typeof(IFunctionCallTracker)]));
        var route = typeof(OptimizeEntryAgent).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(ProjectTimelineDbContext), typeof(Guid), typeof(Guid?), typeof(ICustomAgent), typeof(IFunctionCallTracker)],
            modifiers: null);
        var unanchored = new ScriptedInfrastructureAgent();
        Assert.NotNull(route);
        var legacy = Assert.IsType<OptimizeEntryAgent>(route.Invoke([null, timelineId, null, unanchored, null]));
        Assert.Equal(0, unanchored.InitCalls);
        await Assert.ThrowsAsync<InfrastructureScriptCompletedException>(() => legacy.RunAsync("legacy original"));
        Assert.Equal("legacy original", unanchored.Message);
        Assert.Empty(unanchored.Tools);
        Assert.DoesNotContain("# Persisted review context", unanchored.Instructions);
    }

    [Fact]
    public void Tracker_keeps_unidentified_calls_distinct_and_never_downgrades_a_failure()
    {
        var tracker = new FunctionCallTracker();
        var first = new FunctionCall { Name = "fetch_entry" };
        var second = new FunctionCall { ID = "", Name = "fetch_entry" };
        tracker.FunctionCallReceiver(null, new() { Function = first });
        tracker.FunctionCallReceiver(null, new() { Function = second });
        first.Executed = true;
        first.Result = "direct result";
        tracker.FunctionCallReceiver(null, new() { Function = first });
        var failure = new InvalidOperationException("Sanitized synthetic failure");
        var failed = new FunctionCall { ID = "sdk-id", Name = "fetch_entry", Executed = true, Exception = failure };
        tracker.FunctionCallReceiver(null, new() { Function = failed });
        tracker.FunctionCallReceiver(null, new() { Function = new() {
            ID = "sdk-id", Executed = true, Result = "Terminal transport result"
        } });

        Assert.Equal(3, tracker.Tools.Count);
        Assert.Same(first, tracker.Tools[0]);
        Assert.Same(second, tracker.Tools[1]);
        Assert.Same(failure, tracker.Tools[2].Exception);
        Assert.Null(tracker.Tools[2].Result);
        Assert.True(tracker.Tools[0].Executed);
    }

    [Fact]
    public async Task Actual_tracker_is_injectable_and_merges_execution_by_call_ID_not_function_name()
    {
        IFunctionCallTracker tracker = new FunctionCallTracker();
        var original = new FunctionCall {
            ID = "first", Name = "fetch_entry", Args = [new() { Name = "entryId", Value = Guid.NewGuid() }]
        };
        var second = new FunctionCall { ID = "second", Name = "fetch_entry" };
        var failure = new InvalidOperationException("Synthetic fetch failure");
        var seam = new ScriptedInfrastructureAgent {
            Script = script => {
                script.Report(original);
                script.Report(second);
                script.Report(new() { ID = "first", Executed = true, Result = "persisted content" });
                script.Report(new() { ID = "second", Executed = true, Exception = failure });
                script.Report(new() { ID = "first", Name = "must not replace metadata", Executed = false });
                script.Report(new() { ID = "result-only", Executed = true, Result = "late event" });
                return Task.CompletedTask;
            }
        };
        var agent = new OptimizeEntryAgent(contextTools: null, agent: seam, callTracker: tracker);

        await Assert.ThrowsAsync<InfrastructureScriptCompletedException>(() => agent.RunAsync("tracker infrastructure only"));

        Assert.Equal(3, tracker.Tools.Count);
        Assert.Same(original, tracker.Tools[0]);
        Assert.Equal("fetch_entry", original.Name);
        Assert.Single(original.Args);
        Assert.True(original.Executed);
        Assert.Equal("persisted content", original.Result);
        Assert.Null(original.Exception);
        Assert.Same(second, tracker.Tools[1]);
        Assert.True(second.Executed);
        Assert.Same(failure, second.Exception);
        Assert.Null(second.Result);
        Assert.Equal("result-only", tracker.Tools[2].ID);
        tracker.FunctionCallReceiver(null, new FunctionCallEventArgs());
        Assert.Equal(3, tracker.Tools.Count);
        Assert.Equal(0, seam.Subscribers);
    }
}
