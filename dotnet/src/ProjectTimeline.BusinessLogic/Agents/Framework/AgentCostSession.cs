using System.Diagnostics;
using Microsoft.Extensions.AI;

namespace ProjectTimeline.BusinessLogic.Agents.Framework;

/// <summary>
/// Explicit in-memory operation scope shared across agents/turns through ExecutionContext.
/// Await all work before disposing, in LIFO order and in the creating logical flow. Disposal
/// freezes accounting: in-flight calls remain incomplete and late results are ignored, never
/// redirected to another session. No response, message, exception or usage object is retained.
/// </summary>
public sealed class AgentCostSession : IDisposable
{
    private static readonly AsyncLocal<AgentCostSession?> Ambient = new();
    private readonly AgentCostSession? previous;
    private readonly Activity? activity;
    private readonly object gate = new();
    private readonly Dictionary<string, AgentTokenPricing> prices;
    private readonly Dictionary<string, Accumulator> models = new(StringComparer.Ordinal);
    private readonly Accumulator total = new();
    private volatile bool disposed;

    public Guid SessionId { get; }
    public string Currency { get; }
    // A stale inherited disposed scope must NOT fall through to its parent for attribution.
    public static AgentCostSession? Current => Ambient.Value is { disposed: false } scope ? scope : null;

    private AgentCostSession(IReadOnlyDictionary<string, AgentTokenPricing>? prices, Guid? sessionId, string currency)
    {
        if (currency is null || currency.Length != 3 || currency.Any(c => c is < 'A' or > 'Z'))
            throw new ArgumentException("Use one three-letter uppercase currency code.", nameof(currency));
        this.prices = new(StringComparer.Ordinal);
        prices ??= currency == "USD" ? AgentModelPricing.Usd : null;
        if (prices is not null)
            foreach (var pair in prices)
            {
                if (string.IsNullOrEmpty(pair.Key) || pair.Value is null)
                    throw new ArgumentException("Prices require exact nonempty model names and nonnull rates.", nameof(prices));
                this.prices.Add(pair.Key, pair.Value); // Values are immutable; copy the caller-owned catalog.
            }
        SessionId = sessionId ?? Guid.NewGuid();
        Currency = currency;
        previous = Ambient.Value;
        activity = AgentTelemetry.StartSession(SessionId, Currency);
    }

    public static AgentCostSession Start(IReadOnlyDictionary<string, AgentTokenPricing>? prices = null,
        Guid? sessionId = null, string currency = "USD")
    {
        var session = new AgentCostSession(prices, sessionId, currency);
        Ambient.Value = session;
        return session;
    }

    public AgentCostSnapshot GetSnapshot()
    {
        lock (gate)
            return new(SessionId, Currency, disposed, total.Snapshot(), Array.AsReadOnly(models
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new AgentModelCostSnapshot(pair.Key.Length == 0 ? null : pair.Key, pair.Value.Snapshot()))
                .ToArray()));
    }

    public void Dispose()
    {
        AgentCostTotals final;
        lock (gate)
        {
            if (disposed) return;
            if (!ReferenceEquals(Ambient.Value, this))
                throw new InvalidOperationException("Dispose cost sessions in LIFO order in the owning logical flow.");
            disposed = true;
            Ambient.Value = previous;
            final = total.Snapshot(); // Freeze in-flight incompleteness before stopping the span.
        }
        AgentTelemetry.CompleteSession(activity, final);
    }

    internal bool BeginCall(string? requestedModel)
    {
        lock (gate)
        {
            if (disposed) return false;
            total.Begin();
            Model(requestedModel).Begin();
            return true;
        }
    }

    internal void CompleteCall(string? requestedModel, ChatResponse? response) =>
        CompleteCall(requestedModel, response?.ModelId, Counts.Read(response?.Usage));

    internal void CompleteEmbeddingCall(string requestedModel, OpenAI.Embeddings.OpenAIEmbeddingCollection? response) =>
        CompleteCall(requestedModel, response?.Model, Counts.Read(response?.Usage is { } usage ? new UsageDetails
        {
            InputTokenCount = usage.InputTokenCount, TotalTokenCount = usage.TotalTokenCount,
            OutputTokenCount = 0 // Embedding vectors are not generated text tokens.
        } : null));

    private void CompleteCall(string? requestedModel, string? responseModel, Counts counts)
    {
        lock (gate)
        {
            if (disposed) return;
            var model = string.IsNullOrEmpty(responseModel) ? requestedModel : responseModel;
            prices.TryGetValue(model ?? "", out var price); // Exact response ID wins; no alias/fuzzy fallback.
            decimal? inputCost = null, outputCost = null;
            if (price is not null)
            {
                inputCost = PriceInput(counts, price);
                outputCost = Multiply(counts.Output, price.OutputPerMillionTokens);
            }
            var knownCost = Add(inputCost ?? 0m, outputCost ?? 0m);
            var fullCost = Add(inputCost, outputCost);
            var pending = Model(requestedModel);
            pending.Withdraw();
            if (pending.Calls == 0) models.Remove(requestedModel ?? "");
            Model(model).Observe(counts, knownCost, fullCost);
            total.Withdraw();
            total.Observe(counts, knownCost, fullCost);
        }
    }

    private Accumulator Model(string? model)
    {
        var key = model ?? "";
        if (!models.TryGetValue(key, out var value)) models.Add(key, value = new());
        return value;
    }

    private static decimal? PriceInput(Counts counts, AgentTokenPricing price)
    {
        if (price.CachedInputPerMillionTokens is not { } cachedRate || cachedRate == price.InputPerMillionTokens)
            return Multiply(counts.Input, price.InputPerMillionTokens);
        // Missing/invalid discount-sensitive cache counts are unknown, NOT assumed zero.
        if (counts.Input is not { } input || counts.Cached is not { } cached) return null;
        return Add(Multiply(input - cached, price.InputPerMillionTokens), Multiply(cached, cachedRate));
    }

    private static decimal? Multiply(long? tokens, decimal rate)
    {
        if (tokens is null) return null;
        try { return tokens.Value / 1_000_000m * rate; }
        catch (OverflowException) { return null; }
    }

    private static decimal? Add(decimal? a, decimal? b)
    {
        try { return checked(a + b); }
        catch (OverflowException) { return null; }
    }

    private static long? Add(long? a, long? b)
    {
        try { return checked(a + b); }
        catch (OverflowException) { return null; }
    }

    private readonly record struct Counts(long? Input, long? Output, long? Total, long? Cached, long? Reasoning, bool Complete)
    {
        public static Counts Read(UsageDetails? usage)
        {
            static long? Nonnegative(long? value) => value >= 0 ? value : null;
            var input = Nonnegative(usage?.InputTokenCount);
            var output = Nonnegative(usage?.OutputTokenCount);
            var total = Nonnegative(usage?.TotalTokenCount);
            // A contradictory reported total casts doubt on the base counts, including the price estimate.
            if (usage?.TotalTokenCount < 0 || input > total || output > total ||
                (input.HasValue && output.HasValue && total.HasValue &&
                 (decimal)input.Value + output.Value != total.Value)) input = output = total = null;
            var cached = Nonnegative(usage?.CachedInputTokenCount);
            var reasoning = Nonnegative(usage?.ReasoningTokenCount);
            var invalidSubset = usage?.CachedInputTokenCount < 0 || usage?.ReasoningTokenCount < 0 ||
                cached > input || reasoning > output;
            if (input is null || cached > input) cached = null;
            if (output is null || reasoning > output) reasoning = null;
            return new(input, output, total, cached, reasoning,
                input.HasValue && output.HasValue && total.HasValue && !invalidSubset);
        }
    }

    private sealed class Accumulator
    {
        public long Calls;
        private long inFlight, unknownUsage, unknownCost;
        private long? input = 0, output = 0, tokens = 0, cached = 0, reasoning = 0;
        private decimal? knownCost = 0m, fullCost = 0m;
        public void Begin() { Calls++; inFlight++; }
        public void Withdraw() { Calls--; inFlight--; }
        public void Observe(Counts counts, decimal? known, decimal? full)
        {
            Calls++;
            if (!counts.Complete) unknownUsage++;
            if (full is null) unknownCost++;
            input = Add(input, counts.Input); output = Add(output, counts.Output); tokens = Add(tokens, counts.Total);
            cached = Add(cached, counts.Cached); reasoning = Add(reasoning, counts.Reasoning);
            knownCost = Add(knownCost, known); fullCost = Add(fullCost, full);
        }
        public AgentCostTotals Snapshot() => new(Calls, inFlight, unknownUsage, unknownCost,
            inFlight == 0 ? input : null, inFlight == 0 ? output : null, inFlight == 0 ? tokens : null,
            inFlight == 0 ? cached : null, inFlight == 0 ? reasoning : null,
            knownCost, inFlight == 0 ? fullCost : null);
    }
}
