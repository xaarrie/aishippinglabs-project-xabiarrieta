namespace ProjectTimeline.BusinessLogic.Agents.Framework;

/// <summary>Point-in-time immutable accounting; model labels are not approved telemetry attributes.</summary>
public sealed record AgentCostSnapshot(Guid SessionId, string Currency, bool IsDisposed,
    AgentCostTotals Totals, IReadOnlyList<AgentModelCostSnapshot> Models);

/// <summary>A null model identifies calls with neither a response nor a requested/configured model.</summary>
public sealed record AgentModelCostSnapshot(string? ModelId, AgentCostTotals Totals);

/// <summary>
/// Null token totals mean at least one call is unknown/invalid, in flight, or the sum overflowed.
/// Cache/reasoning are optional subsets, never additional billable tokens. KnownEstimatedCost is
/// only the sum of priceable components, NOT a complete estimate (null on arithmetic overflow).
/// </summary>
public sealed record AgentCostTotals(long CallCount, long InFlightCallCount, long UnknownUsageCallCount,
    long UnknownCostCallCount, long? InputTokenCount, long? OutputTokenCount, long? TotalTokenCount,
    long? CachedInputTokenCount, long? ReasoningTokenCount, decimal? KnownEstimatedCost, decimal? EstimatedTotalCost)
{
    public bool IsUsageComplete => InFlightCallCount == 0 && UnknownUsageCallCount == 0 &&
        InputTokenCount.HasValue && OutputTokenCount.HasValue && TotalTokenCount.HasValue;
    public bool IsEstimatedCostComplete => InFlightCallCount == 0 && UnknownCostCallCount == 0 && EstimatedTotalCost.HasValue;
}
