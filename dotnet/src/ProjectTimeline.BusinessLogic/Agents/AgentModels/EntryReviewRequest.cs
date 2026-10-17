namespace ProjectTimeline.BusinessLogic.Agents.AgentModels;

public sealed record EntryReviewRequest(
    EntryReviewResult? PreviousResult = null,
    Dictionary<string, string>? UserAnswers = null);
