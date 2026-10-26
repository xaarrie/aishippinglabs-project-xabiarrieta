using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using Xunit;

namespace ProjectTimeline.Tests;

[Collection(AgentTelemetryCollection.Name)]
public sealed class EmbeddingCostTests
{
    [Fact]
    public async Task Shared_session_counts_embedding_and_chat_once_without_vector_output_tokens()
    {
        using var handler = new OfflineEmbedding(); using var http = new HttpClient(handler);
        var provider = new CustomOpenAIClient("offline-placeholder", http);
        using var session = AgentCostSession.Start();
        Assert.Equal(1536, (await provider.EmbedAsync("synthetic", default)).Length);
        using var chat = new UsageTrackingChatClient(new Chat());
        await chat.GetResponseAsync([]);
        var snapshot = session.GetSnapshot(); var totals = snapshot.Totals;
        Assert.Equal(1, handler.Calls); Assert.Equal(2, totals.CallCount);
        Assert.Equal(110, totals.InputTokenCount); Assert.Equal(20, totals.OutputTokenCount);
        Assert.Equal(130, totals.TotalTokenCount); Assert.Equal(0.0000272m, totals.EstimatedTotalCost);
        Assert.True(totals.IsUsageComplete); Assert.True(totals.IsEstimatedCostComplete);
        Assert.Equal(0, snapshot.Models.Single(m => m.ModelId == "text-embedding-3-small").Totals.OutputTokenCount);
    }

    [Theory]
    [InlineData("invalid-vector", true)]
    [InlineData("other-model", false)]
    [InlineData("invalid-usage", false)]
    [InlineData("negative-usage", false)]
    [InlineData("missing-usage", false)]
    [InlineData("throw", false)]
    [InlineData("cancel", false)]
    public async Task Provider_attempts_complete_once_and_keep_billed_usage_before_validation(string mode, bool priced)
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new OfflineEmbedding(mode, cancellation); using var http = new HttpClient(handler);
        var provider = new CustomOpenAIClient("offline-placeholder", http);
        using var session = AgentCostSession.Start();
        var error = await Record.ExceptionAsync(() => provider.EmbedAsync("synthetic", cancellation.Token));
        if (mode is not ("invalid-usage" or "negative-usage" or "missing-usage")) Assert.NotNull(error);
        var snapshot = session.GetSnapshot(); var totals = snapshot.Totals;
        Assert.Equal(1, handler.Calls); Assert.Equal(1, totals.CallCount); Assert.Equal(0, totals.InFlightCallCount);
        Assert.Equal(priced ? 0.0000002m : null, totals.EstimatedTotalCost);
        Assert.Equal(mode is "invalid-vector" or "other-model" ? 0 : 1, totals.UnknownUsageCallCount);
        if (mode == "other-model") Assert.Equal("other-model", Assert.Single(snapshot.Models).ModelId);
        if (mode is "throw" or "cancel" or "missing-usage" or "invalid-usage" or "negative-usage") Assert.Null(totals.InputTokenCount);
    }

    [Fact]
    public async Task Owned_embedding_scope_lasts_until_response_and_restores_ambient_without_leaking()
    {
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stopped.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new OfflineEmbedding(wait: gate.Task); using var http = new HttpClient(handler);
        var pending = new CustomOpenAIClient("offline-placeholder", http).EmbedAsync("synthetic", default);
        Assert.Empty(stopped); Assert.Null(AgentCostSession.Current);
        gate.SetResult(); await pending;
        Assert.Null(AgentCostSession.Current);
        var summary = Assert.Single(stopped);
        Assert.Equal("agent.session", summary.OperationName);
        Assert.Equal(1L, summary.GetTagItem("agent.session.call_count"));
        Assert.Equal(0.0000002d, summary.GetTagItem("agent.session.estimated_total_cost"));
        Assert.Equal("USD", summary.GetTagItem("agent.session.currency"));
    }

    [Theory]
    [InlineData("USD", false, null)]
    [InlineData("EUR", true, null)]
    [InlineData("EUR", false, "0.00003")]
    public async Task Embeddings_use_the_same_authoritative_catalog_and_currency(string currency, bool defaults, string? expected)
    {
        using var handler = new OfflineEmbedding(); using var http = new HttpClient(handler);
        Dictionary<string, AgentTokenPricing>? prices = defaults ? null : new();
        if (expected is not null) prices!.Add("text-embedding-3-small", new(3m, 0m));
        using var session = AgentCostSession.Start(prices, currency: currency);
        await new CustomOpenAIClient("offline-placeholder", http).EmbedAsync("synthetic", default);
        Assert.Same(session, AgentCostSession.Current);
        Assert.Equal(expected is null ? null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), session.GetSnapshot().Totals.EstimatedTotalCost);
        Assert.Equal(1, session.GetSnapshot().Totals.CallCount);
    }

    [Fact]
    public async Task Local_rejections_do_not_invent_provider_calls()
    {
        using var session = AgentCostSession.Start();
        var provider = new CustomOpenAIClient(new Chat()); // No embedding SDK client.
        Assert.NotNull(await Record.ExceptionAsync(() => provider.EmbedAsync("", default)));
        Assert.NotNull(await Record.ExceptionAsync(() => provider.EmbedAsync("synthetic", default)));
        Assert.Equal(0, session.GetSnapshot().Totals.CallCount);
    }

    private sealed class OfflineEmbedding(string mode = "valid", CancellationTokenSource? cancellation = null, Task? wait = null) : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (wait is not null) await wait.WaitAsync(ct);
            if (mode == "throw") throw new HttpRequestException("synthetic");
            if (mode == "cancel") { cancellation!.Cancel(); ct.ThrowIfCancellationRequested(); }
            var vector = Enumerable.Repeat(0.25f, mode == "invalid-vector" ? 2 : 1536).ToArray();
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    @object = "list", model = mode == "other-model" ? "other-model" : "text-embedding-3-small",
                    data = new[] { new { @object = "embedding", index = 0, embedding = vector } },
                    usage = mode == "missing-usage" ? null : new { prompt_tokens = mode == "negative-usage" ? -1 : 10, total_tokens = mode == "invalid-usage" ? 9 : 10 }
                }), Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class Chat : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthetic"))
            {
                ModelId = "gpt-4o-mini", Usage = new() { InputTokenCount = 100, OutputTokenCount = 20, TotalTokenCount = 120, CachedInputTokenCount = 0 }
            });
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
