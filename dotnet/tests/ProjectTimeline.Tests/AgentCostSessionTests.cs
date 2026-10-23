using System.Net;
using Microsoft.Extensions.AI;
using OpenAI;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using System.ClientModel.Primitives;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class AgentCostSessionTests
{
    [Fact]
    public async Task Installed_OpenAI_adapter_exposes_typed_cache_and_reasoning_subsets()
    {
        using var http = new HttpClient(new OfflineOpenAI());
        using var client = new OpenAIClient(new System.ClientModel.ApiKeyCredential("offline-placeholder"),
            new OpenAIClientOptions
            {
                Transport = new HttpClientPipelineTransport(http), RetryPolicy = new ClientRetryPolicy(0)
            }).GetChatClient("requested").AsIChatClient();
        var response = await client.GetResponseAsync([new(ChatRole.User, "synthetic")]);
        Assert.Equal("resolved", response.ModelId);
        var usage = Assert.IsType<UsageDetails>(response.Usage);
        Assert.Equal(100, usage.InputTokenCount);
        Assert.Equal(20, usage.OutputTokenCount);
        Assert.Equal(120, usage.TotalTokenCount);
        Assert.Equal(40, usage.CachedInputTokenCount);
        Assert.Equal(5, usage.ReasoningTokenCount);
        Assert.Equal(new[] { "InputTokenDetails.AudioTokenCount", "OutputTokenDetails.AcceptedPredictionTokenCount",
            "OutputTokenDetails.AudioTokenCount", "OutputTokenDetails.RejectedPredictionTokenCount" },
            usage.AdditionalCounts!.Keys.Order(StringComparer.Ordinal));
    }

    private static Dictionary<string, AgentTokenPricing> Prices() => new(StringComparer.Ordinal)
    {
        ["a"] = new(2m, 5m), ["b"] = new(3m, 7m)
    };

    private static ChatResponse Response(string? model = "a", long? input = 100, long? output = 20,
        long? total = 120, long? cached = null, long? reasoning = null) => new(new ChatMessage(ChatRole.Assistant, "synthetic"))
    {
        ModelId = model, ResponseId = "legitimately-repeated-id",
        Usage = new UsageDetails { InputTokenCount = input, OutputTokenCount = output, TotalTokenCount = total,
            CachedInputTokenCount = cached, ReasoningTokenCount = reasoning }
    };

    private sealed class Chat(Func<CancellationToken, Task<ChatResponse>> respond) : IChatClient
    {
        public int Calls;
        public IEnumerable<ChatMessage>? Messages;
        public ChatOptions? Options;
        public CancellationToken Token;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            Messages = messages; Options = options; Token = cancellationToken;
            return respond(cancellationToken);
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static UsageTrackingChatClient Client(ChatResponse response, string? model = null) =>
        new(new Chat(_ => Task.FromResult(response)), model);
    private static Task<ChatResponse> Call(IChatClient client) => client.GetResponseAsync([new(ChatRole.User, "synthetic")]);

    [Fact]
    public async Task Aggregates_exact_models_and_legitimate_repeated_responses_with_decimal_arithmetic()
    {
        var id = Guid.NewGuid();
        using var session = AgentCostSession.Start(Prices(), id);
        using var a = Client(Response());
        using var b = Client(Response("b"));
        await Call(a); await Call(a); await Call(b);
        var snapshot = session.GetSnapshot();
        Assert.Equal(id, snapshot.SessionId);
        Assert.Equal("USD", snapshot.Currency);
        Assert.Equal(3, snapshot.Totals.CallCount);
        Assert.Equal(300, snapshot.Totals.InputTokenCount);
        Assert.Equal(60, snapshot.Totals.OutputTokenCount);
        Assert.Equal(360, snapshot.Totals.TotalTokenCount);
        Assert.Equal(0.00104m, snapshot.Totals.EstimatedTotalCost);
        Assert.True(snapshot.Totals.IsUsageComplete);
        Assert.True(snapshot.Totals.IsEstimatedCostComplete);
        Assert.Equal(2, snapshot.Models.Count);
        Assert.Equal(2, snapshot.Models.Single(m => m.ModelId == "a").Totals.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Task_based_agent_runs_reach_nonstreaming_provider_boundary(bool harness)
    {
        using var session = AgentCostSession.Start(Prices());
        using var client = Client(Response()); // Streaming deliberately throws in the fake.
        var provider = new CustomOpenAIClient(client, isolateHarnessFacilities: true);
        var agent = harness ? provider.CreateHarnessAgent("a", "one", "synthetic", []) :
            provider.CreateAgent("a", "one", "synthetic", []);
        await agent.RunAsync("synthetic");
        await provider.CreateAgent("a", "two", "synthetic", []).RunAsync("synthetic");
        Assert.Equal(2, session.GetSnapshot().Totals.CallCount);
        Assert.Equal(0.0006m, session.GetSnapshot().Totals.EstimatedTotalCost);
    }

    [Fact]
    public async Task Parallel_calls_share_session_but_independent_async_sessions_are_isolated()
    {
        using var outer = AgentCostSession.Start(Prices());
        using var client = Client(Response());
        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => Call(client))));
        var children = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var child = AgentCostSession.Start(Prices());
            await Task.Yield();
            Assert.Same(child, AgentCostSession.Current);
            await Call(client);
            return child.GetSnapshot();
        }));
        Assert.Same(outer, AgentCostSession.Current);
        Assert.Equal(50, outer.GetSnapshot().Totals.CallCount);
        Assert.Equal(0.015m, outer.GetSnapshot().Totals.EstimatedTotalCost);
        Assert.All(children, child => Assert.Equal(1, child.Totals.CallCount));
        Assert.Equal(8, children.Select(child => child.SessionId).Distinct().Count());
    }

    [Fact]
    public async Task Captures_session_at_call_start_and_restores_nested_scope()
    {
        using var outer = AgentCostSession.Start(Prices());
        var gate = new TaskCompletionSource<ChatResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new UsageTrackingChatClient(new Chat(_ => gate.Task));
        var pending = Call(client);
        Assert.Equal(1, outer.GetSnapshot().Totals.InFlightCallCount);
        Assert.Null(outer.GetSnapshot().Totals.EstimatedTotalCost);
        using (var inner = AgentCostSession.Start(Prices()))
        {
            gate.SetResult(Response());
            await pending;
            Assert.Same(inner, AgentCostSession.Current);
            Assert.Equal(0, inner.GetSnapshot().Totals.CallCount);
        }
        Assert.Same(outer, AgentCostSession.Current);
        Assert.Equal(1, outer.GetSnapshot().Totals.CallCount);
        Assert.Equal(0.0003m, outer.GetSnapshot().Totals.EstimatedTotalCost);
    }

    [Fact]
    public async Task No_session_is_transparent_and_late_session_does_not_capture_earlier_call()
    {
        Assert.Null(AgentCostSession.Current);
        var gate = new TaskCompletionSource<ChatResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var chat = new Chat(_ => gate.Task);
        using var client = new UsageTrackingChatClient(chat);
        var messages = new[] { new ChatMessage(ChatRole.User, "synthetic") };
        var options = new ChatOptions { ModelId = "a" };
        using var cancellation = new CancellationTokenSource();
        var pending = client.GetResponseAsync(messages, options, cancellation.Token);
        using var session = AgentCostSession.Start(Prices());
        var response = Response(); gate.SetResult(response);
        Assert.Same(response, await pending);
        Assert.Same(messages, chat.Messages); Assert.Same(options, chat.Options);
        Assert.Equal(cancellation.Token, chat.Token);
        Assert.Equal(0, session.GetSnapshot().Totals.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_and_cancellation_keep_exception_identity_and_unknown_billable_usage(bool cancelled)
    {
        using var session = AgentCostSession.Start(Prices());
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Exception error = cancelled ? new OperationCanceledException("synthetic", cancellation.Token) : new InvalidOperationException("synthetic");
        using var client = new UsageTrackingChatClient(new Chat(_ => throw error), "a");
        Assert.Same(error, await Record.ExceptionAsync(() => client.GetResponseAsync([], cancellationToken: cancellation.Token)));
        var totals = session.GetSnapshot().Totals;
        Assert.Equal(1, totals.CallCount); Assert.Equal(0, totals.InFlightCallCount);
        Assert.Equal(1, totals.UnknownUsageCallCount); Assert.Equal(1, totals.UnknownCostCallCount);
        Assert.Null(totals.InputTokenCount); Assert.Null(totals.EstimatedTotalCost);
        Assert.False(totals.IsUsageComplete); Assert.False(totals.IsEstimatedCostComplete);
    }

    [Theory]
    [InlineData(null, 20L, 20L)]
    [InlineData(100L, null, 100L)]
    [InlineData(-1L, 20L, 19L)]
    [InlineData(100L, -1L, 99L)]
    [InlineData(100L, 20L, -1L)]
    [InlineData(100L, 20L, 999L)]
    public async Task Partial_or_invalid_usage_is_not_invented(long? input, long? output, long? total)
    {
        using var session = AgentCostSession.Start(Prices());
        using var client = Client(Response(input: input, output: output, total: total));
        await Call(client);
        var totals = session.GetSnapshot().Totals;
        Assert.False(totals.IsUsageComplete);
        Assert.Null(totals.EstimatedTotalCost);
        Assert.False(totals.IsEstimatedCostComplete);
    }

    [Fact]
    public async Task Absent_usage_is_unknown_but_explicit_zero_usage_is_complete()
    {
        using var session = AgentCostSession.Start(Prices());
        using var zero = Client(Response(input: 0, output: 0, total: 0)); await Call(zero);
        Assert.Equal(0m, session.GetSnapshot().Totals.EstimatedTotalCost);
        var absent = Response(); absent.Usage = null;
        using var client = Client(absent); await Call(client);
        var totals = session.GetSnapshot().Totals;
        Assert.Null(totals.InputTokenCount); Assert.Null(totals.EstimatedTotalCost);
        Assert.Equal(1, totals.UnknownUsageCallCount);
        Assert.Equal(0m, totals.KnownEstimatedCost);
    }

    [Theory]
    [InlineData(40L, "0.00024")]
    [InlineData(0L, "0.0003")]
    [InlineData(100L, "0.00015")]
    [InlineData(null, null)]
    [InlineData(-1L, null)]
    [InlineData(101L, null)]
    public async Task Cache_discount_requires_valid_typed_subset_and_never_adds_reasoning(long? cached, string? cost)
    {
        using var session = AgentCostSession.Start(new Dictionary<string, AgentTokenPricing> { ["a"] = new(2m, 5m, 0.5m) });
        var response = Response(cached: cached, reasoning: 5);
        response.Usage!.AdditionalCounts = new() { ["unverified-cache-label"] = 1000000 };
        using var client = Client(response); await Call(client);
        var totals = session.GetSnapshot().Totals;
        Assert.Equal(cost is null ? null : decimal.Parse(cost, System.Globalization.CultureInfo.InvariantCulture), totals.EstimatedTotalCost);
        Assert.Equal(120, totals.TotalTokenCount);
        Assert.Equal(5, totals.ReasoningTokenCount);
        if (cost is null) Assert.Equal(0.0001m, totals.KnownEstimatedCost); // Only output is priceable.
    }

    [Fact]
    public async Task Missing_optional_subsets_do_not_block_uniform_prices_or_invent_total_tokens()
    {
        using var session = AgentCostSession.Start(Prices());
        using var client = Client(Response(total: null)); await Call(client);
        var totals = session.GetSnapshot().Totals;
        Assert.Null(totals.TotalTokenCount); Assert.Null(totals.CachedInputTokenCount);
        Assert.Null(totals.ReasoningTokenCount); Assert.False(totals.IsUsageComplete);
        Assert.Equal(0.0003m, totals.EstimatedTotalCost); // Cost only needs input/output for uniform rates.
    }

    [Theory]
    [InlineData("a", "b", "a", "0.00044")]
    [InlineData("a", "unknown", "a", null)]
    [InlineData("a", "A", "a", null)]
    [InlineData("a", null, null, "0.0003")]
    [InlineData(null, null, "b", "0.00044")]
    [InlineData(null, null, null, null)]
    public async Task Pricing_matches_exact_response_model_before_request_or_configuration(string? configured, string? resolved,
        string? requested, string? expected)
    {
        using var session = AgentCostSession.Start(Prices());
        using var client = Client(Response(resolved), configured);
        await client.GetResponseAsync([], new ChatOptions { ModelId = requested });
        var snapshot = session.GetSnapshot();
        Assert.Equal(resolved ?? requested ?? configured, Assert.Single(snapshot.Models).ModelId);
        Assert.Equal(expected is null ? null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), snapshot.Totals.EstimatedTotalCost);
        Assert.True(snapshot.Totals.IsUsageComplete);
    }

    [Fact]
    public async Task Prices_and_snapshots_are_frozen_and_unknown_prices_preserve_known_partial_cost()
    {
        var prices = Prices();
        using var session = AgentCostSession.Start(prices, currency: "EUR");
        prices["a"] = new(999m, 999m); prices.Clear();
        using var priced = Client(Response()); await Call(priced);
        var before = session.GetSnapshot();
        using var unpriced = Client(Response("unpriced")); await Call(unpriced);
        Assert.Equal(0.0003m, before.Totals.EstimatedTotalCost);
        Assert.Single(before.Models);
        var after = session.GetSnapshot();
        Assert.Equal("EUR", after.Currency);
        Assert.Null(after.Totals.EstimatedTotalCost);
        Assert.Equal(0.0003m, after.Totals.KnownEstimatedCost);
        Assert.Equal(1, after.Totals.UnknownCostCallCount);
    }

    [Fact]
    public void Invalid_prices_currency_and_duplicate_direct_decorators_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentTokenPricing(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentTokenPricing(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentTokenPricing(0, 0, -1));
        Assert.Throws<ArgumentException>(() => AgentCostSession.Start(currency: "USD/EUR"));
        using var client = Client(Response());
        Assert.Throws<ArgumentException>(() => new UsageTrackingChatClient(client));
        Assert.Null(AgentCostSession.Current);
    }

    [Fact]
    public async Task Disposing_with_inflight_work_freezes_incomplete_snapshot_and_never_attributes_late_work_to_parent()
    {
        using var parent = AgentCostSession.Start(Prices());
        var child = AgentCostSession.Start(Prices());
        var gate = new TaskCompletionSource<ChatResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new UsageTrackingChatClient(new Chat(_ => gate.Task));
        var pending = Call(client);
        child.Dispose(); child.Dispose();
        Assert.Same(parent, AgentCostSession.Current);
        var final = child.GetSnapshot();
        Assert.True(final.IsDisposed); Assert.Equal(1, final.Totals.InFlightCallCount);
        Assert.False(final.Totals.IsUsageComplete); Assert.Null(final.Totals.EstimatedTotalCost);
        gate.SetResult(Response()); await pending;
        Assert.Equal(final.Totals, child.GetSnapshot().Totals);
        Assert.Equal(0, parent.GetSnapshot().Totals.CallCount);
    }

    [Fact]
    public void Disposal_requires_LIFO_and_is_idempotent()
    {
        var outer = AgentCostSession.Start(); var inner = AgentCostSession.Start();
        try
        {
            Assert.Throws<InvalidOperationException>(() => outer.Dispose());
            Assert.Same(inner, AgentCostSession.Current);
            inner.Dispose(); Assert.Same(outer, AgentCostSession.Current);
            inner.Dispose(); Assert.Same(outer, AgentCostSession.Current);
            outer.Dispose(); outer.Dispose(); Assert.Null(AgentCostSession.Current);
            Assert.Equal(0m, outer.GetSnapshot().Totals.EstimatedTotalCost);
        }
        finally { inner.Dispose(); outer.Dispose(); }
    }

    [Fact]
    public async Task Numeric_overflow_does_not_replace_provider_results_or_claim_complete_totals()
    {
        using var session = AgentCostSession.Start(new Dictionary<string, AgentTokenPricing> { ["a"] = new(decimal.MaxValue, decimal.MaxValue) });
        var response = Response(input: long.MaxValue, output: 0, total: long.MaxValue);
        using var client = Client(response);
        Assert.Same(response, await Call(client)); Assert.Same(response, await Call(client));
        var totals = session.GetSnapshot().Totals;
        Assert.Null(totals.InputTokenCount); Assert.Null(totals.EstimatedTotalCost);
        Assert.False(totals.IsUsageComplete);
    }

    [Theory]
    [InlineData(100L, null, 50L)]
    [InlineData(null, 20L, 10L)]
    public async Task Total_cannot_be_smaller_than_a_known_component_even_with_partial_usage(long? input, long? output, long total)
    {
        using var session = AgentCostSession.Start(Prices());
        using var client = Client(Response(input: input, output: output, total: total)); await Call(client);
        var totals = session.GetSnapshot().Totals;
        Assert.Null(totals.InputTokenCount); Assert.Null(totals.OutputTokenCount);
        Assert.Null(totals.TotalTokenCount); Assert.Equal(0m, totals.KnownEstimatedCost);
        Assert.Null(totals.EstimatedTotalCost);
    }

    [Fact]
    public async Task Partial_components_remain_unknown_while_other_counts_and_partial_cost_stay_available()
    {
        using var session = AgentCostSession.Start(Prices());
        using var partial = Client(Response(input: null, total: null)); await Call(partial);
        using var complete = Client(Response()); await Call(complete);
        var snapshot = session.GetSnapshot();
        Assert.Null(snapshot.Totals.InputTokenCount);
        Assert.Equal(40, snapshot.Totals.OutputTokenCount);
        Assert.Null(snapshot.Totals.TotalTokenCount);
        Assert.Equal(0.0004m, snapshot.Totals.KnownEstimatedCost);
        Assert.Null(snapshot.Totals.EstimatedTotalCost);
        Assert.Equal(1, snapshot.Totals.UnknownUsageCallCount);
        Assert.Throws<NotSupportedException>(() => ((IList<AgentModelCostSnapshot>)snapshot.Models).Clear());
    }

    [Fact]
    public async Task Response_mutation_cannot_rewrite_observed_usage_or_snapshots()
    {
        using var session = AgentCostSession.Start(Prices());
        var response = Response(cached: 40, reasoning: 5);
        using var client = Client(response); await Call(client);
        var snapshot = session.GetSnapshot();
        response.ModelId = "rewritten"; response.Usage!.InputTokenCount = 999;
        response.Usage.CachedInputTokenCount = 999; response.Usage.ReasoningTokenCount = 999;
        Assert.Equal(snapshot.Totals, session.GetSnapshot().Totals);
        Assert.Equal("a", Assert.Single(session.GetSnapshot().Models).ModelId);
    }

    [Fact]
    public async Task Disposed_scope_in_inherited_context_does_not_fall_through_to_live_parent()
    {
        using var parent = AgentCostSession.Start(Prices());
        var child = AgentCostSession.Start(Prices());
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = Client(Response());
        var later = Task.Run(async () =>
        {
            await gate.Task;
            Assert.Null(AgentCostSession.Current);
            await Call(client);
        });
        child.Dispose(); gate.SetResult(); await later;
        Assert.Same(parent, AgentCostSession.Current);
        Assert.Equal(0, parent.GetSnapshot().Totals.CallCount);
        Assert.Equal(0, child.GetSnapshot().Totals.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_session_preserves_provider_failure_identity(bool cancelled)
    {
        Exception error = cancelled ? new OperationCanceledException("synthetic") : new InvalidOperationException("synthetic");
        using var client = new UsageTrackingChatClient(new Chat(_ => Task.FromException<ChatResponse>(error)));
        Assert.Same(error, await Record.ExceptionAsync(() => Call(client)));
        Assert.Null(AgentCostSession.Current);
    }

    [Fact]
    public async Task Missing_catalog_never_makes_zero_usage_free_and_equal_cache_rate_needs_no_subset_count()
    {
        using (var unpriced = AgentCostSession.Start())
        {
            using var zero = Client(Response(input: 0, output: 0, total: 0)); await Call(zero);
            Assert.Null(unpriced.GetSnapshot().Totals.EstimatedTotalCost);
            Assert.Equal(1, unpriced.GetSnapshot().Totals.UnknownCostCallCount);
        }
        using var uniform = AgentCostSession.Start(new Dictionary<string, AgentTokenPricing> { ["a"] = new(2m, 5m, 2m) });
        using var client = Client(Response()); await Call(client);
        Assert.Equal(0.0003m, uniform.GetSnapshot().Totals.EstimatedTotalCost);
    }

    private sealed class OfflineOpenAI : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"id":"synthetic","object":"chat.completion","created":1,"model":"resolved",
                     "choices":[{"index":0,"message":{"role":"assistant","content":"synthetic"},"finish_reason":"stop"}],
                     "usage":{"prompt_tokens":100,"completion_tokens":20,"total_tokens":120,
                       "prompt_tokens_details":{"cached_tokens":40},"completion_tokens_details":{"reasoning_tokens":5}}}
                    """, System.Text.Encoding.UTF8, "application/json")
            });
    }
}
