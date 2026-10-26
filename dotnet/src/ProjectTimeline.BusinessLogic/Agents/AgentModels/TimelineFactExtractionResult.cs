using System.Text.Json.Serialization;

namespace ProjectTimeline.BusinessLogic.Agents.AgentModels;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class TimelineFactExtractionResult
{
    public required List<TimelineFactExtractionClaim> Claims { get; init; }
    // Optional for legacy model responses. No answer/suggestion field is admitted.
    public List<TimelineFactExtractionQuestion> Questions { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class TimelineFactExtractionQuestion
{
    public required string Key { get; init; }
    public required string Text { get; init; }
    public required string Reason { get; init; }
    public required List<string> EvidenceIds { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class TimelineFactExtractionClaim
{
    public required string Claim { get; init; }
    public required string EntityKey { get; init; }
    public required string EntityType { get; init; }
    public required string SourceQuote { get; init; }
}
