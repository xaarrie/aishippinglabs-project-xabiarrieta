namespace ProjectTimeline.BusinessLogic.Agents.AgentModels;

/// <summary>Model output has no rewritten source or readiness flag. Answers are checked against server evidence.</summary>
public sealed class TimelineFactReviewResult
{
    public List<TimelineFactCandidate> Additions { get; set; } = [];
    public List<TimelineFactRetraction> Invalidations { get; set; } = [];
    public List<TimelineFactQuestion> Questions { get; set; } = [];
    public List<TimelineFactInvalidationAssessment> InvalidationAssessments { get; set; } = [];
}

/// <summary>Model judgment bound to a specific pair and delivered source evidence, not independent semantic proof.</summary>
public sealed record TimelineFactInvalidationAssessment(Guid FactId, int InvalidatingAdditionIndex,
    string HistoricalClaim, string HistoricalSourceFingerprint, string HistoricalQuote,
    string ReplacementClaim, string ReplacementQuote, string Relationship, bool SameScope,
    string SharedScope, string Explanation);
