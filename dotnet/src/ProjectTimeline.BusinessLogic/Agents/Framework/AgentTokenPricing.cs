namespace ProjectTimeline.BusinessLogic.Agents.Framework;

/// <summary>Externally supplied prices per million tokens, all in the owning session's currency.</summary>
public sealed record AgentTokenPricing
{
    public decimal InputPerMillionTokens { get; }
    public decimal OutputPerMillionTokens { get; }
    public decimal? CachedInputPerMillionTokens { get; }

    public AgentTokenPricing(decimal inputPerMillionTokens, decimal outputPerMillionTokens,
        decimal? cachedInputPerMillionTokens = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputPerMillionTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(outputPerMillionTokens);
        if (cachedInputPerMillionTokens < 0) throw new ArgumentOutOfRangeException(nameof(cachedInputPerMillionTokens));
        InputPerMillionTokens = inputPerMillionTokens;
        OutputPerMillionTokens = outputPerMillionTokens;
        CachedInputPerMillionTokens = cachedInputPerMillionTokens;
    }
}
