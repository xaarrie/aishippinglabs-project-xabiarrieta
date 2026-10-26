using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.BusinessLogic.Agents.Models;
using Xunit;

namespace ProjectTimeline.Tests;

[Collection(AgentTelemetryCollection.Name)]
public sealed class AgentResearchExecutionTests
{
    public sealed class Result { public string? Value { get; set; } }

    private static ICustomAgent Agent(IChatClient chat, bool harness = false, List<AITool>? tools = null)
    {
        var provider = new CustomOpenAIClient(chat, true);
        Assert.Same(chat, provider.GetChatClient("offline"));
        ICustomAgent agent = harness ? new LocalCustomHarnessAgent(provider) : new LocalCustomAgent(provider);
        agent.Init("offline", "offline", "Use tools when needed.", tools ?? []);
        return agent;
    }

    private static AgentExecutionBudget Start(int calls = 6, CancellationToken ct = default, TimeSpan? timeout = null) =>
        AgentExecutionBudget.Start(new AgentExecutionBudgetOptions(calls, 1500, timeout ?? TimeSpan.FromSeconds(60)), ct);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initialized_agents_admit_six_calls_and_reject_seventh_before_provider_and_accounting(bool harness)
    {
        var chat = new Chat();
        var agent = Agent(chat, harness);
        using var cost = AgentCostSession.Start();
        using var budget = Start();
        for (var i = 0; i < 6; i++) await agent.RunAsync("offline", CancellationToken.None);
        var error = await Assert.ThrowsAsync<AgentExecutionBudgetException>(() => agent.RunAsync("private input", CancellationToken.None));
        Assert.Equal(AgentExecutionBudgetReason.ProviderCallLimit, error.Reason);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private input", error.ToString());
        Assert.Equal(6, chat.Calls);
        Assert.Equal(6, budget.ProviderCallCount);
        Assert.Equal(6, cost.GetSnapshot().Totals.CallCount);
        Assert.All(chat.Options, options => Assert.Equal(1500, options!.MaxOutputTokens));
    }

    [Fact]
    public async Task Parallel_admission_is_atomic_and_independent_operations_do_not_share_limits()
    {
        var chat = new Chat();
        var agent = Agent(chat);
        await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            using var budget = Start();
            var outcomes = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
                await Record.ExceptionAsync(() => agent.RunAsync("offline", CancellationToken.None))));
            Assert.Equal(6, outcomes.Count(error => error is null));
            Assert.All(outcomes.Where(error => error is not null), error =>
                Assert.Equal(AgentExecutionBudgetReason.ProviderCallLimit, Assert.IsType<AgentExecutionBudgetException>(error).Reason));
        }));
        Assert.Equal(18, chat.Calls);
        Assert.Null(AgentExecutionBudget.Current);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Cancellable_text_and_typed_overloads_reach_provider(bool harness, bool typed)
    {
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chat = new Chat(async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Response();
        });
        var agent = Agent(chat, harness);
        var run = typed ? (Task)agent.RunAsync<Result>("offline", cancel.Token) : agent.RunAsync("offline", cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(chat.Token.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tool_error_result_continuations_stop_at_actual_provider_limit(bool harness)
    {
        var toolCalls = 0;
        // Research tools return safe error envelopes. Preserve SDK exception handling defaults.
        var tool = AIFunctionFactory.Create(() =>
        {
            toolCalls++;
            return "{\"error\":\"Unavailable\"}";
        }, "lookup");
        var chat = new Chat((call, _) => Task.FromResult(ToolResponse(call)));
        var agent = Agent(chat, harness, [tool]);
        using var session = AgentCostSession.Start();
        using var budget = Start();
        var error = await Assert.ThrowsAsync<AgentExecutionBudgetException>(() => agent.RunAsync("offline", CancellationToken.None));
        Assert.Equal(AgentExecutionBudgetReason.ProviderCallLimit, error.Reason);
        Assert.Equal(6, chat.Calls);
        Assert.Equal(6, toolCalls);
        Assert.Equal(6, session.GetSnapshot().Totals.CallCount);
        Assert.Equal(0, session.GetSnapshot().Totals.UnknownUsageCallCount);
        Assert.DoesNotContain("private", error.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Budget_preserves_native_hierarchy_exact_usage_and_tool_callbacks(bool harness)
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentTelemetry.NativeSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        Activity? toolActivity = null;
        var tool = AIFunctionFactory.Create(() => { toolActivity = Activity.Current; return "ok"; }, "lookup");
        var chat = new Chat((call, _) => Task.FromResult(call == 1 ? ToolResponse(call) : Response()));
        var agent = Agent(new UsageTrackingChatClient(chat, "offline"), harness, [tool]);
        var callbacks = 0;
        agent.FunctionCalled += (_, _) => callbacks++;
        using var session = AgentCostSession.Start(new Dictionary<string, AgentTokenPricing> { ["offline"] = new(2m, 5m) });
        using var budget = Start();
        Assert.Equal("ok", (await agent.RunAsync<Result>("offline", CancellationToken.None)).Result.Value);
        Assert.Equal(2, callbacks);
        Assert.Equal(2, chat.Calls);
        Assert.Equal(2, session.GetSnapshot().Totals.CallCount);
        Assert.Equal(24, session.GetSnapshot().Totals.TotalTokenCount);
        Assert.Equal(0.00006m, session.GetSnapshot().Totals.EstimatedTotalCost);
        var invoke = Assert.Single(spans, span => Operation(span) == "invoke_agent");
        var chats = spans.Where(span => Operation(span) == "chat").ToArray();
        Assert.Equal(2, chats.Length);
        Assert.All(chats, span => Assert.Equal(invoke.SpanId, span.ParentSpanId));
        var execution = Assert.Single(spans, span => Operation(span) == "execute_tool");
        Assert.Equal(invoke.SpanId, execution.ParentSpanId);
        Assert.Same(execution, toolActivity);
        Assert.All(chat.Activities, span => Assert.Contains(span, chats));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SDK_continuation_after_thrown_tool_error_cannot_bypass_provider_cap(bool harness)
    {
        var error = new InvalidOperationException("offline tool error");
        var tool = AIFunctionFactory.Create((Func<string>)(() => throw error), "lookup");
        var chat = new Chat((call, _) => Task.FromResult(ToolResponse(call)));
        var agent = Agent(chat, harness, [tool]);
        using var budget = Start(1);
        using var session = AgentCostSession.Start();
        // Installed SDK continues after the first thrown tool error. A tool counter alone would
        // not stop its next provider turn, even though this operation allows only one real call.
        var denied = await Assert.ThrowsAsync<AgentExecutionBudgetException>(() => agent.RunAsync("offline", CancellationToken.None));
        Assert.Equal(AgentExecutionBudgetReason.ProviderCallLimit, denied.Reason);
        Assert.Equal(1, chat.Calls);
        Assert.Equal(1, session.GetSnapshot().Totals.CallCount);
    }

    [Theory]
    [InlineData(false, "success")]
    [InlineData(true, "success")]
    [InlineData(false, "provider")]
    [InlineData(true, "provider")]
    [InlineData(false, "tool")]
    [InlineData(true, "tool")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    public async Task Same_wrapper_starts_fresh_only_after_the_previous_execution_ends(bool harness, string ending)
    {
        using var cancel = new CancellationTokenSource();
        var chat = new Chat((call, ct) =>
        {
            if (call == 1 && ending == "provider") throw new InvalidDataException("offline failure");
            if (call == 1 && ending == "cancel") { cancel.Cancel(); ct.ThrowIfCancellationRequested(); }
            return Task.FromResult(call == 1 && ending == "tool" ? ToolResponse(call) : Response());
        });
        var tool = AIFunctionFactory.Create((Func<string>)(() => throw new InvalidOperationException("offline tool failure")), "lookup");
        var agent = Agent(chat, harness, [tool]);
        using (var first = Start(1))
        {
            var error = await Record.ExceptionAsync(() => agent.RunAsync<Result>("first", cancel.Token));
            if (ending == "success") Assert.Null(error);
            else if (ending == "provider") Assert.IsType<InvalidDataException>(error);
            else Assert.Equal(ending == "tool" ? AgentExecutionBudgetReason.ProviderCallLimit : AgentExecutionBudgetReason.RequestCanceled,
                Assert.IsType<AgentExecutionBudgetException>(error).Reason);
            Assert.Equal(1, first.ProviderCallCount);
            // Neither failures nor per-call cancellation refund an admitted attempt in a live scope.
            var denied = await Assert.ThrowsAsync<AgentExecutionBudgetException>(() => agent.RunAsync("retry", CancellationToken.None));
            Assert.Equal(AgentExecutionBudgetReason.ProviderCallLimit, denied.Reason);
            Assert.Equal(1, chat.Calls);
        }
        Assert.Null(AgentExecutionBudget.Current);
        using (var second = Start(1))
        {
            Assert.Equal("ok", (await agent.RunAsync<Result>("independent", CancellationToken.None)).Result.Value);
            Assert.Equal(1, second.ProviderCallCount);
        }
        Assert.Equal(2, chat.Calls);
        Assert.Null(AgentExecutionBudget.Current);
    }

    private static string? Operation(Activity span) => span.GetTagItem("gen_ai.operation.name") as string;

    // Exercise the exact shared factory leaf without SDK option-merging obscuring identity assertions.
    private static IChatClient Boundary(IChatClient chat) => (IChatClient)typeof(CustomOpenAIClient)
        .GetMethod("GetTrackedChatClient", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .Invoke(new CustomOpenAIClient(chat, true), ["offline"])!;

    [Theory]
    [InlineData(null, 1500)]
    [InlineData(2000, 1500)]
    [InlineData(300, 300)]
    public async Task Output_cap_clones_options_and_preserves_other_settings(int? requested, int expected)
    {
        var chat = new Chat();
        var leaf = Boundary(chat);
        var tool = AIFunctionFactory.Create(() => "ok", "lookup");
        var options = new ChatOptions
        {
            MaxOutputTokens = requested, Temperature = 0.2f, ResponseFormat = ChatResponseFormat.Json,
            Tools = [tool], ModelId = "offline", AdditionalProperties = new() { ["setting"] = "kept" },
            RawRepresentationFactory = _ => new object()
        };
        using var budget = Start();
        await leaf.GetResponseAsync([], options);
        var observed = Assert.Single(chat.Options)!;
        Assert.NotSame(options, observed);
        Assert.Equal(requested, options.MaxOutputTokens);
        Assert.Equal(expected, observed.MaxOutputTokens);
        Assert.Equal(options.Temperature, observed.Temperature);
        Assert.Same(options.ResponseFormat, observed.ResponseFormat);
        Assert.Same(tool, Assert.Single(observed.Tools!));
        Assert.Equal("kept", observed.AdditionalProperties!["setting"]);
        Assert.Same(options.RawRepresentationFactory, observed.RawRepresentationFactory);
    }

    [Fact]
    public async Task No_budget_is_transparent_including_options_token_response_errors_and_streaming()
    {
        var response = Response();
        var error = new InvalidDataException("private provider failure");
        var chat = new Chat((call, _) => call == 1 ? Task.FromResult(response) : throw error);
        var leaf = Boundary(chat);
        using var ct = new CancellationTokenSource();
        var options = new ChatOptions { MaxOutputTokens = 5000 };
        Assert.Same(response, await leaf.GetResponseAsync([], options, ct.Token));
        Assert.Same(options, Assert.Single(chat.Options));
        Assert.Equal(ct.Token, chat.Token);
        Assert.Same(error, await Assert.ThrowsAsync<InvalidDataException>(() => leaf.GetResponseAsync([])));
        Assert.Same(chat.Stream, leaf.GetStreamingResponseAsync([], options, ct.Token));
        Assert.Equal(1, chat.StreamCalls);
        using var budget = Start();
        Assert.Throws<NotSupportedException>(() => leaf.GetStreamingResponseAsync([]));
        Assert.Equal(1, chat.StreamCalls);
    }

    [Fact]
    public async Task Nested_scopes_restore_parent_and_inflight_calls_remain_attributed_to_original_budget()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chat = new Chat(async (call, _) =>
        {
            if (call == 1) { entered.SetResult(); await release.Task; }
            return Response();
        });
        var agent = Agent(chat);
        var outer = Start(1);
        try
        {
            var pending = agent.RunAsync("offline", CancellationToken.None);
            await entered.Task;
            using (var inner = Start(1))
            {
                Assert.Throws<InvalidOperationException>(() => outer.Dispose());
                Assert.Same(inner, AgentExecutionBudget.Current);
                release.SetResult();
                await pending;
                Assert.Equal(0, inner.ProviderCallCount);
                await agent.RunAsync("offline", CancellationToken.None);
                Assert.Equal(1, inner.ProviderCallCount);
            }
            Assert.Same(outer, AgentExecutionBudget.Current);
            Assert.Equal(1, outer.ProviderCallCount);
            await Assert.ThrowsAsync<AgentExecutionBudgetException>(() => agent.RunAsync("offline", CancellationToken.None));
        }
        finally { outer.Dispose(); outer.Dispose(); }
        Assert.Null(AgentExecutionBudget.Current);
        await agent.RunAsync("offline", CancellationToken.None);
        Assert.Equal(3, chat.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Precancelled_requests_are_denied_before_provider_or_cost(bool harness, bool typed)
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var chat = new Chat();
        var agent = Agent(chat, harness);
        using var session = AgentCostSession.Start();
        using var budget = Start(ct: cancel.Token);
        var error = await Assert.ThrowsAsync<AgentExecutionBudgetException>(() => typed
            ? (Task)agent.RunAsync<Result>("offline", cancel.Token) : agent.RunAsync("offline", cancel.Token));
        Assert.Equal(AgentExecutionBudgetReason.RequestCanceled, error.Reason);
        Assert.Equal(0, chat.Calls);
        Assert.Equal(0, budget.ProviderCallCount);
        Assert.Equal(0, session.GetSnapshot().Totals.CallCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Cancellation_and_deadline_reach_provider_and_preserve_unknown_usage(bool harness, bool deadline)
    {
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chat = new Chat(async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Response();
        });
        var agent = Agent(chat, harness);
        using var session = AgentCostSession.Start();
        using var budget = Start(ct: cancel.Token, timeout: deadline ? TimeSpan.FromMilliseconds(200) : null);
        var pending = agent.RunAsync<Result>("offline", CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (!deadline) cancel.Cancel();
        var error = await Assert.ThrowsAsync<AgentExecutionBudgetException>(() => pending);
        Assert.Equal(deadline ? AgentExecutionBudgetReason.DeadlineExceeded : AgentExecutionBudgetReason.RequestCanceled, error.Reason);
        Assert.True(chat.Token.IsCancellationRequested);
        Assert.Equal(1, chat.Calls);
        Assert.Equal(1, session.GetSnapshot().Totals.UnknownUsageCallCount);
        Assert.Null(session.GetSnapshot().Totals.EstimatedTotalCost);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Cancellation_and_deadline_reach_actual_SDK_tool_tokens(bool harness, bool deadline)
    {
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken toolToken = default;
        var tool = AIFunctionFactory.Create(async (CancellationToken ct) =>
        {
            toolToken = ct;
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return "ok";
        }, "lookup");
        var chat = new Chat((call, _) => Task.FromResult(ToolResponse(call)));
        var agent = Agent(chat, harness, [tool]);
        using var budget = Start(ct: cancel.Token, timeout: deadline ? TimeSpan.FromMilliseconds(200) : null);
        var pending = agent.RunAsync("offline", CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (!deadline) cancel.Cancel();
        var error = await Assert.ThrowsAsync<AgentExecutionBudgetException>(() => pending);
        Assert.Equal(deadline ? AgentExecutionBudgetReason.DeadlineExceeded : AgentExecutionBudgetReason.RequestCanceled, error.Reason);
        Assert.True(toolToken.IsCancellationRequested);
        Assert.Equal(1, chat.Calls);
    }

    [Fact]
    public async Task Disposal_cancels_inflight_work_and_stale_inherited_scope_never_bypasses_guard()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStale = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chat = new Chat(async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Response();
        });
        var agent = Agent(chat);
        var budget = Start();
        var stale = Task.Run(async () =>
        {
            await releaseStale.Task;
            Assert.Same(budget, AgentExecutionBudget.Current);
            var denied = await Assert.ThrowsAsync<AgentExecutionBudgetException>(() => agent.RunAsync("offline", CancellationToken.None));
            Assert.Equal(AgentExecutionBudgetReason.ScopeDisposed, denied.Reason);
        });
        var pending = agent.RunAsync("offline", CancellationToken.None);
        await entered.Task;
        budget.Dispose(); budget.Dispose();
        Assert.Null(AgentExecutionBudget.Current);
        var error = await Assert.ThrowsAsync<AgentExecutionBudgetException>(() => pending);
        Assert.Equal(AgentExecutionBudgetReason.ScopeDisposed, error.Reason);
        releaseStale.SetResult();
        await stale;
        Assert.Equal(1, chat.Calls);
    }

    [Fact]
    public async Task Expired_budget_and_late_ignored_cancellation_response_cannot_publish_success()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chat = new Chat(async (_, _) => { entered.SetResult(); await release.Task; return Response(); });
        var agent = Agent(chat);
        using var session = AgentCostSession.Start();
        using var budget = Start(timeout: TimeSpan.FromMilliseconds(200));
        var pending = agent.RunAsync("offline", CancellationToken.None);
        await entered.Task;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(Timeout.InfiniteTimeSpan, budget.Token));
        release.SetResult();
        var error = await Assert.ThrowsAsync<AgentExecutionBudgetException>(() => pending);
        Assert.Equal(AgentExecutionBudgetReason.DeadlineExceeded, error.Reason);
        await Assert.ThrowsAsync<AgentExecutionBudgetException>(() => agent.RunAsync("offline", CancellationToken.None));
        Assert.Equal(1, chat.Calls);
        // A known late response remains real usage, not fabricated zero or an unknown charge.
        Assert.Equal(12, session.GetSnapshot().Totals.TotalTokenCount);
        Assert.Equal(1, session.GetSnapshot().Totals.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_key_production_SDK_path_sends_one_attempt_on_503_and_obeys_six_call_limit(bool harness)
    {
        using var handler = new OfflineHttp();
        using var http = new HttpClient(handler);
        var provider = new CustomOpenAIClient("offline-placeholder", http, true);
        ICustomAgent agent = harness ? new LocalCustomHarnessAgent(provider) : new LocalCustomAgent(provider);
        agent.Init("offline", "gpt-4o-mini", "offline", []);
        using var session = AgentCostSession.Start();
        using var budget = Start();
        for (var i = 1; i <= 6; i++)
        {
            var error = await Record.ExceptionAsync(() => agent.RunAsync("offline", CancellationToken.None));
            Assert.NotNull(error);
            Assert.IsNotType<AgentExecutionBudgetException>(error);
            Assert.Equal(i, handler.Calls); // Retryable HTTP status must NOT result in a second SDK attempt.
        }
        var denied = await Assert.ThrowsAsync<AgentExecutionBudgetException>(() => agent.RunAsync("offline", CancellationToken.None));
        Assert.Equal(AgentExecutionBudgetReason.ProviderCallLimit, denied.Reason);
        Assert.Equal(6, handler.Calls);
        Assert.Equal(6, session.GetSnapshot().Totals.CallCount);
        Assert.Equal(6, session.GetSnapshot().Totals.UnknownUsageCallCount);
        Assert.All(handler.Bodies, body => Assert.Contains("1500", body));
    }

    private sealed class OfflineHttp : HttpMessageHandler
    {
        public int Calls;
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{\"error\":{\"message\":\"offline failure\",\"type\":\"server_error\"}}", Encoding.UTF8, "application/json")
            };
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Per_run_cancellation_is_classified_even_without_scope_request_token(bool harness)
    {
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chat = new Chat(async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Response();
        });
        var agent = Agent(chat, harness);
        using var budget = Start();
        var pending = agent.RunAsync("offline", cancel.Token);
        await entered.Task;
        cancel.Cancel();
        var error = await Assert.ThrowsAsync<AgentExecutionBudgetException>(() => pending);
        Assert.Equal(AgentExecutionBudgetReason.RequestCanceled, error.Reason);
        Assert.True(chat.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Null_options_are_created_for_bounded_calls_and_invalid_limits_do_not_replace_ambient_scope()
    {
        var chat = new Chat();
        var leaf = Boundary(chat);
        using var budget = Start();
        Assert.Throws<ArgumentNullException>(() => AgentExecutionBudget.Start(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentExecutionBudget.Start(new(0, 1500, TimeSpan.FromSeconds(60))));
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentExecutionBudget.Start(new(6, 0, TimeSpan.FromSeconds(60))));
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentExecutionBudget.Start(new(6, 1500, TimeSpan.Zero)));
        Assert.Same(budget, AgentExecutionBudget.Current);
        await leaf.GetResponseAsync([]);
        Assert.Equal(1500, Assert.Single(chat.Options)!.MaxOutputTokens);
    }

    [Fact]
    public async Task Legacy_implementations_fail_explicitly_instead_of_ignoring_new_cancellation_contracts()
    {
        ICustomAgent legacy = new LegacyAgent();
        await Assert.ThrowsAsync<NotSupportedException>(() => legacy.RunAsync("offline", CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => legacy.RunAsync<Result>("offline", CancellationToken.None));
    }

    private sealed class LegacyAgent : ICustomAgent
    {
        public event EventHandler<FunctionCallEventArgs>? FunctionCalled { add { } remove { } }
        public void Init(string agentName, string model, string instructions, List<AITool> tools) { }
        public Task<AgentResponse> RunAsync(string message) => throw new Xunit.Sdk.XunitException("Legacy text run must not be used");
        public Task<AgentResponse<T>> RunAsync<T>(string message) where T : class => throw new Xunit.Sdk.XunitException("Legacy typed run must not be used");
    }

    private static ChatResponse ToolResponse(int call)
    {
        var response = Response();
        response.Messages[0] = new(ChatRole.Assistant, [new FunctionCallContent($"call-{call}", "lookup", new Dictionary<string, object?>())]);
        return response;
    }

    private static ChatResponse Response() => new(new ChatMessage(ChatRole.Assistant, "{\"Value\":\"ok\"}"))
    {
        Usage = new() { InputTokenCount = 10, OutputTokenCount = 2, TotalTokenCount = 12 }, ModelId = "offline"
    };

    private sealed class Chat(Func<int, CancellationToken, Task<ChatResponse>>? respond = null) : IChatClient
    {
        public int Calls;
        public CancellationToken Token;
        public ConcurrentQueue<ChatOptions?> Options { get; } = new();
        public ConcurrentQueue<Activity?> Activities { get; } = new();
        public int StreamCalls;
        public IAsyncEnumerable<ChatResponseUpdate> Stream { get; } = EmptyStream();
        private static async IAsyncEnumerable<ChatResponseUpdate> EmptyStream() { await Task.Yield(); yield break; }
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            Activities.Enqueue(Activity.Current);
            Options.Enqueue(options);
            var call = Interlocked.Increment(ref Calls);
            await Task.Yield();
            return respond is null ? Response() : await respond(call, cancellationToken);
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            StreamCalls++;
            return Stream;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
