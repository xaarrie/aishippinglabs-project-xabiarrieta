using System.Collections.Frozen;

namespace ProjectTimeline.BusinessLogic.Agents.Framework;

/// <summary>Dated USD estimates, not live prices or an invoice. Only exact documented model IDs.</summary>
public static class AgentModelPricing
{
    public static DateOnly VerifiedOn { get; } = new(2026, 10, 25);
    public const string ChatSource = "https://developers.openai.com/api/docs/models/gpt-4o-mini";
    public const string EmbeddingSource = "https://developers.openai.com/api/docs/models/text-embedding-3-small";

    public static IReadOnlyDictionary<string, AgentTokenPricing> Usd { get; } =
        new Dictionary<string, AgentTokenPricing>(StringComparer.Ordinal)
        {
            ["gpt-4o-mini"] = new(0.15m, 0.60m, 0.075m),
            ["gpt-4o-mini-2024-07-18"] = new(0.15m, 0.60m, 0.075m),
            ["text-embedding-3-small"] = new(0.02m, 0m)
        }.ToFrozenDictionary(StringComparer.Ordinal);
}
