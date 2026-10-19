using System.Text.Json.Serialization;

namespace ProjectTimeline.BusinessLogic.Agents.AgentModels;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class TimelineFactExtractionResult
{
    public required List<TimelineFactExtractionClaim> Claims { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class TimelineFactExtractionClaim
{
    public required string Claim { get; init; }
    public required string EntityKey { get; init; }
    public required string EntityType { get; init; }
    public required string SourceQuote { get; init; }
}
