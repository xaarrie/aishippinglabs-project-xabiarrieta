using System.Text.Json.Serialization;

namespace ProjectTimeline.BusinessLogic.Agents.AgentModels;

/// <summary>Only a query plan or a fixed application refusal; never model-authored public prose.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class TimelineFactInformationPlan
{
    public required string Decision { get; init; }
    public required string[] Queries { get; init; }
    public required string? RefusalReason { get; init; }
}
