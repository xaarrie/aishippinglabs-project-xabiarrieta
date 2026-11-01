namespace ProjectTimeline.BusinessLogic;

/// <summary>Server configuration only. Bounds may be lowered, never raised by model arguments.</summary>
public sealed class TimelineFactResearchOptions
{
    public int MaxModelCalls { get; }
    public int MaxToolCalls { get; }
    public int? MaxSearchQueries => null; // No expression-count ceiling; deadline/output still bound execution.
    public int MaxDistinctFacts { get; }
    public int MaxHitsPerSearch { get; }
    // Legacy response metadata: conservative bounds derived from fact/global limits, not extra caps.
    public int MaxSourceContexts => MaxDistinctFacts;
    public int MaxCharsPerContext => MaxTotalSerializedToolEvidenceChars;
    public int MaxTotalSourceContextChars => MaxTotalSerializedToolEvidenceChars;
    public int MaxTotalSerializedToolEvidenceChars { get; }
    public int MaxOutputTokens { get; }
    public TimeSpan Deadline { get; }

    public TimelineFactResearchOptions(int maxModelCalls = 20, int maxToolCalls = 20,
        int maxDistinctFacts = 40, int maxHitsPerSearch = 10,
        int maxTotalSerializedToolEvidenceChars = 32000, int maxOutputTokens = 1500, TimeSpan? deadline = null)
    {
        MaxModelCalls = Bound(maxModelCalls, 20, nameof(maxModelCalls));
        MaxToolCalls = Bound(maxToolCalls, 20, nameof(maxToolCalls));
        MaxDistinctFacts = Bound(maxDistinctFacts, 40, nameof(maxDistinctFacts));
        MaxHitsPerSearch = Bound(maxHitsPerSearch, 10, nameof(maxHitsPerSearch));
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
