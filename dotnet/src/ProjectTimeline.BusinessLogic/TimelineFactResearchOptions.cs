namespace ProjectTimeline.BusinessLogic;

/// <summary>Server configuration only. Bounds may be lowered, never raised by model arguments.</summary>
public sealed class TimelineFactResearchOptions
{
    public int MaxModelCalls { get; }
    public int MaxToolCalls { get; }
    public int MaxSearchQueries { get; }
    public int MaxDistinctFacts { get; }
    public int MaxHitsPerSearch { get; }
    public int MaxSourceContexts { get; }
    public int MaxCharsPerContext { get; }
    public int MaxTotalSourceContextChars { get; }
    public int MaxTotalSerializedToolEvidenceChars { get; }
    public int MaxOutputTokens { get; }
    public TimeSpan Deadline { get; }

    public TimelineFactResearchOptions(int maxModelCalls = 20, int maxToolCalls = 20, int maxSearchQueries = 8,
        int maxDistinctFacts = 40, int maxHitsPerSearch = 10, int maxSourceContexts = 3,
        int maxCharsPerContext = 4000, int maxTotalSourceContextChars = 12000,
        int maxTotalSerializedToolEvidenceChars = 32000, int maxOutputTokens = 1500, TimeSpan? deadline = null)
    {
        MaxModelCalls = Bound(maxModelCalls, 20, nameof(maxModelCalls));
        MaxToolCalls = Bound(maxToolCalls, 20, nameof(maxToolCalls));
        MaxSearchQueries = Bound(maxSearchQueries, 8, nameof(maxSearchQueries));
        MaxDistinctFacts = Bound(maxDistinctFacts, 40, nameof(maxDistinctFacts));
        MaxHitsPerSearch = Bound(maxHitsPerSearch, 10, nameof(maxHitsPerSearch));
        MaxSourceContexts = Bound(maxSourceContexts, 3, nameof(maxSourceContexts));
        MaxCharsPerContext = Bound(maxCharsPerContext, 4000, nameof(maxCharsPerContext));
        MaxTotalSourceContextChars = Bound(maxTotalSourceContextChars, 12000, nameof(maxTotalSourceContextChars));
        MaxTotalSerializedToolEvidenceChars = Bound(maxTotalSerializedToolEvidenceChars, 32000, nameof(maxTotalSerializedToolEvidenceChars));
        MaxOutputTokens = Bound(maxOutputTokens, 1500, nameof(maxOutputTokens));
        Deadline = deadline ?? TimeSpan.FromSeconds(60);
        if (Deadline <= TimeSpan.Zero || Deadline > TimeSpan.FromSeconds(60)) throw new ArgumentOutOfRangeException(nameof(deadline));
    }

    private static int Bound(int value, int maximum, string name) => value is > 0 && value <= maximum
        ? value : throw new ArgumentOutOfRangeException(name);
}

/// <summary>Stable non-sensitive failure code; no input, scope IDs or database/provider messages.</summary>
public sealed class TimelineFactResearchException(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
}
