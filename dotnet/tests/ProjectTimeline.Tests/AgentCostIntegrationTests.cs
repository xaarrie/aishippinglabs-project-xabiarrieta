using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using Xunit;

namespace ProjectTimeline.Tests;

[Collection(AgentTelemetryCollection.Name)]
public sealed class AgentCostIntegrationTests
{
    private const string Secret = "PRIVATE_COST_PAYLOAD_SENTINEL";
    private static Dictionary<string, AgentTokenPricing> Prices() => new() { [Secret] = new(2m, 5m) };
    public sealed class Result { public string? Value { get; set; } }

    private static ChatResponse Response(bool tool = false) => new(new ChatMessage(ChatRole.Assistant,
        tool ? [new FunctionCallContent(Secret, "lookup", new Dictionary<string, object?> { ["input"] = Secret })] :
        [new TextContent("{\"Value\":\"" + Secret + "\"}")]))
    {
        ModelId = Secret, ResponseId = Secret,
        Usage = new() { InputTokenCount = 100, OutputTokenCount = 20, TotalTokenCount = 120,
            CachedInputTokenCount = 40, ReasoningTokenCount = 5 }
    };

    private sealed class Chat(Func<int, CancellationToken, Task<ChatResponse>> respond) : IChatClient
    {
        public int Calls, Disposals;
        public Activity? Current;
        public IEnumerable<ChatMessage>? Messages;
        public ChatOptions? Options;
        public CancellationToken Token;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Current = Activity.Current; Messages = messages; Options = options; Token = cancellationToken;
            return respond(Interlocked.Increment(ref Calls), cancellationToken);
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => serviceType == typeof(Chat) && serviceKey is null ? this : null;
        public void Dispose() => Disposals++;
    }

    private sealed class Capture : IDisposable
    {
        private readonly ActivityListener listener;
        public ConcurrentQueue<Activity> Stopped { get; } = new();
        public Capture(ActivitySamplingResult sampling = ActivitySamplingResult.AllDataAndRecorded)
        {
            listener = new()
            {
                ShouldListenTo = source => source.Name == AgentTelemetry.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => sampling,
                SampleUsingParentId = (ref ActivityCreationOptions<string> _) => sampling,
                ActivityStopped = activity => Stopped.Enqueue(activity)
            };
            ActivitySource.AddActivityListener(listener);
        }
        public void Dispose() => listener.Dispose();
    }

    private static ICustomAgent Agent(CustomOpenAIClient provider, bool harness = false, bool tools = false)
    {
        ICustomAgent agent = harness ? new LocalCustomHarnessAgent(provider) : new LocalCustomAgent(provider);
        agent.Init(Secret, Secret, Secret, tools ? [AIFunctionFactory.Create((string input) => input, "lookup")] : []);
        return agent;
    }
    private static StructuredAgentRunOptions Options(Action<ChatResponse>? validate = null) => new()
    {
        ChatOptions = new(), SerializerOptions = new JsonSerializerOptions(),
        ValidateResponse = validate ?? (_ => { }), InvalidResult = () => new InvalidDataException(Secret)
    };
    private static async Task Run(ICustomAgent agent, string kind)
    {
        if (kind == "text") Assert.Contains(Secret, (await agent.RunAsync(Secret)).Text);
        else if (kind == "typed") Assert.Equal(Secret, (await agent.RunAsync<Result>(Secret)).Result.Value);
        else Assert.Equal(Secret, (await agent.RunAsync<Result>(Secret, Options(), default)).Result.Value);
    }

    [Theory]
    [InlineData(false, "text")]
    [InlineData(false, "typed")]
    [InlineData(false, "structured")]
    [InlineData(true, "text")]
    [InlineData(true, "typed")]
    public async Task Initialized_agents_count_each_provider_turn_once_below_tool_aggregation(bool harness, string kind)
    {
        var chat = new Chat((call, _) => Task.FromResult(Response(tool: call < 3)));
        var agent = Agent(new CustomOpenAIClient(chat, true), harness, tools: true);
        using var session = AgentCostSession.Start(Prices());
        await Run(agent, kind);
        var totals = session.GetSnapshot().Totals;
        Assert.Equal(3, chat.Calls); Assert.Equal(3, totals.CallCount);
        Assert.Equal(300, totals.InputTokenCount); Assert.Equal(60, totals.OutputTokenCount);
        Assert.Equal(360, totals.TotalTokenCount); Assert.Equal(120, totals.CachedInputTokenCount);
        Assert.Equal(15, totals.ReasoningTokenCount); Assert.Equal(0.0009m, totals.EstimatedTotalCost);
        Assert.True(totals.IsUsageComplete); Assert.True(totals.IsEstimatedCostComplete);
    }

    [Fact]
    public async Task Two_preinitialized_agents_share_operation_and_all_tool_turns()
    {
        var chat = new Chat((call, _) => Task.FromResult(Response(tool: call % 3 != 0)));
        var provider = new CustomOpenAIClient(chat, true);
        var plain = Agent(provider, tools: true); var harness = Agent(provider, harness: true, tools: true);
        using var session = AgentCostSession.Start(Prices());
        await Run(plain, "text"); await Run(harness, "text");
        Assert.Equal(6, chat.Calls); Assert.Equal(6, session.GetSnapshot().Totals.CallCount);
        Assert.Equal(0.0018m, session.GetSnapshot().Totals.EstimatedTotalCost);
    }

    [Fact]
    public async Task Already_tracked_injected_provider_is_not_stacked_by_factories_or_structured_runs()
    {
        var chat = new Chat((_, _) => Task.FromResult(Response()));
        using var tracked = new UsageTrackingChatClient(chat, Secret);
        var provider = new CustomOpenAIClient(tracked, true);
        Assert.Same(tracked, provider.GetChatClient(Secret));
        var plain = Agent(provider); var harness = Agent(provider, harness: true);
        using var session = AgentCostSession.Start(Prices());
        await Run(plain, "structured"); await Run(harness, "text");
        Assert.Equal(2, chat.Calls); Assert.Equal(2, session.GetSnapshot().Totals.CallCount);
    }

    [Fact]
    public async Task Judge_and_direct_agent_factory_consumers_have_accounting_without_invented_run_spans()
    {
        using var capture = new Capture();
        var chat = new Chat((_, _) =>
        {
            var response = Response();
            response.Messages[0] = new(ChatRole.Assistant, "{\"Criteria\":[],\"Feedback\":\"offline\"}");
            return Task.FromResult(response);
        });
        var provider = new CustomOpenAIClient(chat, true);
        var judge = new JudgeAgent(provider); judge.Init("judge", Secret, Secret);
        var direct = provider.CreateAgent(Secret, Secret, Secret, []);
        var session = AgentCostSession.Start(Prices());
        try
        {
            Assert.Equal("offline", (await judge.RunAsync(Secret, Secret, [])).Result.Feedback);
            await direct.RunAsync(Secret);
            Assert.Empty(capture.Stopped);
        }
        finally { session.Dispose(); }
        Assert.Equal(2, session.GetSnapshot().Totals.CallCount);
        Assert.Equal("agent.session", Assert.Single(capture.Stopped).OperationName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sdk_backed_factories_observe_actual_adapter_usage(bool harness)
    {
        using var handler = new OfflineOpenAI(); using var http = new HttpClient(handler);
        var provider = new CustomOpenAIClient("offline-placeholder", http, true);
        var agent = Agent(provider, harness);
        using var session = AgentCostSession.Start(Prices());
        await Run(agent, "text");
        Assert.Equal(1, handler.Calls); Assert.Equal(1, session.GetSnapshot().Totals.CallCount);
        Assert.Equal(0.0003m, session.GetSnapshot().Totals.EstimatedTotalCost);
    }

    [Fact]
    public async Task Raw_getter_preserves_identity_and_is_untracked_unless_explicitly_decorated()
    {
        var response = Response(); var chat = new Chat((_, _) => Task.FromResult(response));
        var provider = new CustomOpenAIClient(chat);
        Assert.Same(chat, provider.GetChatClient(Secret));
        using var session = AgentCostSession.Start(Prices());
        Assert.Same(chat, provider.GetChatClient(Secret));
        Assert.Same(response, await provider.GetChatClient(Secret).GetResponseAsync([]));
        Assert.Equal(0, session.GetSnapshot().Totals.CallCount);
        using var tracked = new UsageTrackingChatClient(provider.GetChatClient(Secret));
        Assert.Same(chat, tracked.GetService<Chat>());
        Assert.Same(response, await tracked.GetResponseAsync([]));
        Assert.Equal(1, session.GetSnapshot().Totals.CallCount);
        tracked.Dispose(); Assert.Equal(1, chat.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_session_factories_preserve_results_failures_and_client_lifetime(bool harness)
    {
        var error = new InvalidOperationException(Secret);
        var chat = new Chat((call, _) => call == 1 ? Task.FromResult(Response()) : throw error);
        var provider = new CustomOpenAIClient(chat, true);
        var agent = harness ? provider.CreateHarnessAgent(Secret, Secret, Secret, []) : provider.CreateAgent(Secret, Secret, Secret, []);
        Assert.Contains(Secret, (await agent.RunAsync(Secret)).Text);
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync(Secret)));
        Assert.Equal(0, chat.Disposals); Assert.Null(AgentCostSession.Current);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("cancel")]
    [InlineData("validation")]
    [InlineData("before")]
    public async Task Structured_failure_preserves_identity_and_accounts_only_actual_provider_work(string stage)
    {
        using var cancellation = new CancellationTokenSource();
        Exception error = stage == "cancel" || stage == "before" ? new OperationCanceledException(Secret, cancellation.Token) : new InvalidDataException(Secret);
        var chat = new Chat((_, _) => stage is "error" or "cancel" ? throw error : Task.FromResult(Response()));
        var agent = Agent(new CustomOpenAIClient(chat, true));
        if (stage == "before") cancellation.Cancel();
        using var session = AgentCostSession.Start(Prices());
        var actual = await Record.ExceptionAsync(() => agent.RunAsync<Result>(Secret, Options(_ => throw error), cancellation.Token));
        if (stage != "before") Assert.Same(error, actual);
        else Assert.IsAssignableFrom<OperationCanceledException>(actual);
        var totals = session.GetSnapshot().Totals;
        Assert.Equal(stage == "before" ? 0 : 1, totals.CallCount);
        Assert.Equal(stage is "error" or "cancel" ? 1 : 0, totals.UnknownUsageCallCount);
        if (stage == "validation") Assert.Equal(0.0003m, totals.EstimatedTotalCost);
        if (stage is "error" or "cancel") Assert.Null(totals.EstimatedTotalCost);
        Assert.Equal(0, chat.Disposals);
    }

    [Fact]
    public async Task Session_summary_parents_wrapper_spans_and_exports_only_safe_final_numeric_totals_once()
    {
        using var capture = new Capture(); using var parent = new Activity("request").Start();
        var chat = new Chat((_, _) => Task.FromResult(Response()));
        var agent = Agent(new CustomOpenAIClient(chat, true));
        var id = Guid.NewGuid(); var session = AgentCostSession.Start(Prices(), id, "EUR");
        var root = Activity.Current;
        try
        {
            Assert.NotSame(parent, root); Assert.Equal("agent.session", root?.OperationName);
            await Run(agent, "structured"); await Run(agent, "text");
            Assert.Same(root, Activity.Current); Assert.Equal(2, capture.Stopped.Count);
            Assert.All(capture.Stopped, span => Assert.Equal(root!.SpanId, span.ParentSpanId));
        }
        finally { session.Dispose(); session.Dispose(); }
        Assert.Same(parent, Activity.Current); Assert.True(session.GetSnapshot().IsDisposed);
        var summary = Assert.Single(capture.Stopped, span => span.OperationName == "agent.session");
        Assert.Equal(parent.SpanId, summary.ParentSpanId); Assert.Equal(parent.TraceId, summary.TraceId);
        Assert.Equal(AgentTelemetry.SourceName, summary.Source.Name);
        Assert.Equal(id.ToString("D"), summary.GetTagItem("agent.session.id"));
        Assert.Equal("EUR", summary.GetTagItem("agent.session.currency"));
        Assert.Equal(2L, summary.GetTagItem("agent.session.call_count"));
        Assert.Equal(0L, summary.GetTagItem("agent.session.in_flight_call_count"));
        Assert.Equal(240L, summary.GetTagItem("agent.session.total_tokens"));
        Assert.Equal(0.0006d, summary.GetTagItem("agent.session.estimated_total_cost"));
        Assert.Equal(0.0006d, summary.GetTagItem("agent.session.known_estimated_cost"));
        Assert.Equal(true, summary.GetTagItem("agent.session.usage_complete"));
        Assert.Equal(true, summary.GetTagItem("agent.session.estimated_cost_complete"));
        Assert.True(summary.Duration > TimeSpan.Zero);
        Safe(summary);
    }

    [Fact]
    public void Empty_and_nested_sessions_restore_both_ambient_scopes_and_reject_non_lifo_disposal()
    {
        using var capture = new Capture(); using var parent = new Activity("request").Start();
        var outer = AgentCostSession.Start(); var root = Activity.Current;
        var inner = AgentCostSession.Start(); var child = Activity.Current;
        try
        {
            Assert.Throws<InvalidOperationException>(() => outer.Dispose());
            Assert.Same(child, Activity.Current); Assert.Same(inner, AgentCostSession.Current);
            inner.Dispose(); inner.Dispose();
            Assert.Same(root, Activity.Current); Assert.Same(outer, AgentCostSession.Current);
        }
        finally { inner.Dispose(); outer.Dispose(); outer.Dispose(); }
        Assert.Same(parent, Activity.Current); Assert.Null(AgentCostSession.Current);
        Assert.Equal(2, capture.Stopped.Count);
        Assert.Equal(root!.SpanId, child!.ParentSpanId);
        Assert.All(capture.Stopped, span =>
        {
            Assert.Equal(0L, span.GetTagItem("agent.session.call_count"));
            Assert.Equal(0d, span.GetTagItem("agent.session.estimated_total_cost"));
            Assert.Equal(true, span.GetTagItem("agent.session.usage_complete")); Safe(span);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Accounting_works_without_listener_or_when_sampling_drops_session(bool drop)
    {
        using var capture = drop ? new Capture(ActivitySamplingResult.None) : null;
        using var parent = new Activity("request").Start();
        var agent = Agent(new CustomOpenAIClient(new Chat((_, _) => Task.FromResult(Response())), true));
        using (var session = AgentCostSession.Start(Prices()))
        {
            Assert.Same(parent, Activity.Current); await Run(agent, "text");
            Assert.Equal(1, session.GetSnapshot().Totals.CallCount);
        }
        Assert.Same(parent, Activity.Current);
        if (capture is not null) Assert.Empty(capture.Stopped);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("partial")]
    [InlineData("unpriced")]
    [InlineData("failure")]
    public async Task Unknown_summary_values_are_omitted_not_zero_and_sensitive_payload_is_absent(string kind)
    {
        using var capture = new Capture();
        var response = Response();
        if (kind == "missing") response.Usage = null;
        if (kind == "partial") { response.Usage!.InputTokenCount = null; response.Usage.TotalTokenCount = null; }
        var agent = Agent(new CustomOpenAIClient(new Chat((_, _) => kind == "failure" ? throw new InvalidDataException(Secret) : Task.FromResult(response)), true));
        var session = AgentCostSession.Start(kind == "unpriced" ? new Dictionary<string, AgentTokenPricing>() : Prices());
        try { await Record.ExceptionAsync(() => agent.RunAsync(Secret)); }
        finally { session.Dispose(); }
        var summary = Assert.Single(capture.Stopped, span => span.OperationName == "agent.session");
        Assert.Null(summary.GetTagItem("agent.session.estimated_total_cost"));
        Assert.Equal(false, summary.GetTagItem("agent.session.estimated_cost_complete"));
        Assert.Equal(1L, summary.GetTagItem("agent.session.unknown_cost_call_count"));
        Assert.Equal(kind == "unpriced", summary.GetTagItem("agent.session.usage_complete"));
        if (kind != "unpriced") Assert.Null(summary.GetTagItem("agent.session.input_tokens"));
        if (kind == "partial")
        {
            Assert.Equal(20L, summary.GetTagItem("agent.session.output_tokens"));
            Assert.Equal(0.0001d, summary.GetTagItem("agent.session.known_estimated_cost"));
        }
        Safe(summary);
    }

    [Fact]
    public async Task Early_disposal_freezes_inflight_summary_and_late_completion_is_not_reexported()
    {
        using var capture = new Capture();
        var gate = new TaskCompletionSource<ChatResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var direct = new CustomOpenAIClient(new Chat((_, _) => gate.Task), true).CreateAgent(Secret, Secret, Secret, []);
        var session = AgentCostSession.Start(Prices());
        var pending = direct.RunAsync(Secret); session.Dispose(); session.Dispose();
        var summary = Assert.Single(capture.Stopped);
        Assert.Equal(1L, summary.GetTagItem("agent.session.in_flight_call_count"));
        Assert.Equal(false, summary.GetTagItem("agent.session.usage_complete"));
        Assert.Null(summary.GetTagItem("agent.session.input_tokens"));
        Assert.Null(summary.GetTagItem("agent.session.estimated_total_cost"));
        var frozen = session.GetSnapshot().Totals;
        gate.SetResult(Response()); await pending;
        Assert.Equal(frozen, session.GetSnapshot().Totals); Assert.Single(capture.Stopped);
    }

    [Theory]
    [InlineData(null, "PRIVATE_COST_PAYLOAD_SENTINEL", true)]
    [InlineData("unmapped-response", "PRIVATE_COST_PAYLOAD_SENTINEL", false)]
    public async Task Factory_configuration_is_only_a_fallback_when_response_model_is_missing(string? resolved, string configured, bool priced)
    {
        var response = Response(); response.ModelId = resolved;
        var provider = new CustomOpenAIClient(new Chat((_, _) => Task.FromResult(response)), true);
        var agent = provider.CreateAgent(configured, "direct", Secret, []);
        using var session = AgentCostSession.Start(Prices());
        await agent.RunAsync(Secret);
        var snapshot = session.GetSnapshot();
        Assert.Equal(resolved ?? configured, Assert.Single(snapshot.Models).ModelId);
        Assert.Equal(priced ? 0.0003m : null, snapshot.Totals.EstimatedTotalCost);
        Assert.Equal(1, snapshot.Totals.CallCount);
    }

    [Fact]
    public async Task Factory_captures_session_at_call_start_and_invalid_start_leaves_ambient_parent_unchanged()
    {
        using var capture = new Capture(); using var parent = new Activity("request").Start();
        Assert.Throws<ArgumentException>(() => AgentCostSession.Start(currency: Secret));
        Assert.Same(parent, Activity.Current); Assert.Null(AgentCostSession.Current); Assert.Empty(capture.Stopped);
        var gate = new TaskCompletionSource<ChatResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new CustomOpenAIClient(new Chat((_, _) => gate.Task), true).CreateAgent(Secret, "direct", Secret, []);
        using var outer = AgentCostSession.Start(Prices());
        var root = Activity.Current;
        var pending = agent.RunAsync(Secret);
        using (var inner = AgentCostSession.Start(Prices()))
        {
            var nested = Activity.Current;
            gate.SetResult(Response()); await pending;
            Assert.Same(inner, AgentCostSession.Current); Assert.Same(nested, Activity.Current);
            Assert.Equal(0, inner.GetSnapshot().Totals.CallCount);
        }
        Assert.Same(root, Activity.Current); Assert.Same(outer, AgentCostSession.Current);
        Assert.Equal(1, outer.GetSnapshot().Totals.CallCount);
        Assert.Equal(0.0003m, outer.GetSnapshot().Totals.EstimatedTotalCost);
    }

    [Fact]
    public async Task Shared_factories_isolate_concurrent_operation_scopes_and_span_parentage()
    {
        using var capture = new Capture();
        var chat = new Chat(async (_, _) => { await Task.Yield(); return Response(); });
        var agent = Agent(new CustomOpenAIClient(chat, true));
        var snapshots = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            using var session = AgentCostSession.Start(Prices());
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Run(agent, "text")));
            return session.GetSnapshot();
        }));
        Assert.Equal(24, chat.Calls); Assert.Equal(6, snapshots.Select(s => s.SessionId).Distinct().Count());
        Assert.All(snapshots, s => { Assert.Equal(4, s.Totals.CallCount); Assert.Equal(0.0012m, s.Totals.EstimatedTotalCost); });
        var roots = capture.Stopped.Where(span => span.OperationName == "agent.session").ToArray();
        Assert.Equal(6, roots.Length);
        Assert.All(roots, root => Assert.Equal(4, capture.Stopped.Count(span => span.ParentSpanId == root.SpanId)));
        Assert.Null(AgentCostSession.Current);
    }

    private static void Safe(Activity summary)
    {
        Assert.Empty(summary.Events); Assert.Empty(summary.Links); Assert.Null(summary.StatusDescription);
        Assert.DoesNotContain(Secret, string.Join("|", summary.TagObjects.Select(tag => $"{tag.Key}={tag.Value}")));
        Assert.All(summary.TagObjects, tag =>
        {
            Assert.StartsWith("agent.session.", tag.Key);
            if (tag.Key is not ("agent.session.id" or "agent.session.currency"))
                Assert.True(tag.Value is long or double or bool, "Only numeric totals and completeness flags may be exported.");
        });
    }

    private sealed class OfflineOpenAI : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    id = Secret, model = Secret, created = 1,
                    choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = Secret } } },
                    usage = new { prompt_tokens = 100, completion_tokens = 20, total_tokens = 120 }
                }), System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
