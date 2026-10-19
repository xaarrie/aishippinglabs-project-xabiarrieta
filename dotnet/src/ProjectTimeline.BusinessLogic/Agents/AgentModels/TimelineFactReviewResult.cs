namespace ProjectTimeline.BusinessLogic.Agents.AgentModels;

/// <summary>Model output has no rewritten source or readiness flag. Answers are checked against server evidence.</summary>
public sealed class TimelineFactReviewResult
{
    public List<TimelineFactCandidate> Additions { get; set; } = [];
    public List<TimelineFactRetraction> Invalidations { get; set; } = [];
    public List<TimelineFactQuestion> Questions { get; set; } = [];
}
