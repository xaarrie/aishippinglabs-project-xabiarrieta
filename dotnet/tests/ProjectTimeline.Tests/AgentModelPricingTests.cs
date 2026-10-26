using Microsoft.Extensions.AI;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class AgentModelPricingTests
{
    [Theory]
    [InlineData("gpt-4o-mini", 40L, "0.000024")]
    [InlineData("gpt-4o-mini-2024-07-18", 0L, "0.000027")]
    [InlineData("gpt-4o-mini", null, null)]
    [InlineData("gpt-4o-mini-unlisted", 0L, null)]
    public async Task Default_USD_prices_exact_models_and_requires_discount_sensitive_cache(string model, long? cached, string? expected)
    {
        using var session = AgentCostSession.Start();
        using var client = new UsageTrackingChatClient(new Chat(model, cached), "gpt-4o-mini");
        await client.GetResponseAsync([]);
        var totals = session.GetSnapshot().Totals;
        Assert.Equal(expected is null ? null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), totals.EstimatedTotalCost);
        Assert.Equal(120, totals.TotalTokenCount); Assert.Equal(5, totals.ReasoningTokenCount);
        if (cached is null) Assert.Equal(0.000012m, totals.KnownEstimatedCost);
        Assert.Equal(expected is null ? 1 : 0, totals.UnknownCostCallCount);
    }

    [Theory]
    [InlineData("empty", "USD", null)]
    [InlineData("default", "EUR", null)]
    [InlineData("override", "EUR", "0.0003")]
    [InlineData("override", "USD", "0.0003")]
    public async Task Explicit_catalog_is_authoritative_and_currency_never_implies_FX(string kind, string currency, string? expected)
    {
        Dictionary<string, AgentTokenPricing>? prices = kind == "default" ? null : new();
        if (kind == "override") prices!.Add("gpt-4o-mini", new(2m, 5m));
        using var session = AgentCostSession.Start(prices, currency: currency);
        prices?.Clear(); // The caller cannot mutate the session's private copy.
        using var client = new UsageTrackingChatClient(new Chat("gpt-4o-mini", 0));
        await client.GetResponseAsync([]);
        Assert.Equal(currency, session.Currency);
        Assert.Equal(expected is null ? null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), session.GetSnapshot().Totals.EstimatedTotalCost);
    }

    [Fact]
    public void Catalog_is_immutable_dated_and_has_only_documented_exact_keys()
    {
        Assert.Equal(new DateOnly(2026, 10, 25), AgentModelPricing.VerifiedOn);
        Assert.Equal(new[] { "gpt-4o-mini", "gpt-4o-mini-2024-07-18", "text-embedding-3-small" }, AgentModelPricing.Usd.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("https://developers.openai.com/api/docs/models/gpt-4o-mini", AgentModelPricing.ChatSource);
        Assert.Equal("https://developers.openai.com/api/docs/models/text-embedding-3-small", AgentModelPricing.EmbeddingSource);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, AgentTokenPricing>)AgentModelPricing.Usd).Clear());
        Assert.Null(AgentModelPricing.Usd["text-embedding-3-small"].CachedInputPerMillionTokens);
    }

    private sealed class Chat(string model, long? cached) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthetic"))
            {
                ModelId = model,
                Usage = new() { InputTokenCount = 100, OutputTokenCount = 20, TotalTokenCount = 120, CachedInputTokenCount = cached, ReasoningTokenCount = 5 }
            });
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
