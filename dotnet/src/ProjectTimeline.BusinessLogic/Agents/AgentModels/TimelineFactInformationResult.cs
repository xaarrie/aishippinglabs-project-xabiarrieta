using System.Text.Json.Serialization;

namespace ProjectTimeline.BusinessLogic.Agents.AgentModels;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class TimelineFactInformationResult
{
    public string Content { get; set; } = "";
    public List<Guid> CitedFactIds { get; set; } = [];
}

public sealed record TimelineFactInformationCitation(Guid FactId, Guid EntryId, int EntryNumber, Guid? CreationReviewId, Guid? ExtractionJobId = null);
public sealed record TimelineFactInformationResponse(string Content, IReadOnlyList<TimelineFactSearchHit> Facts,
    IReadOnlyList<TimelineFactInformationCitation> Citations, TimelineFactInformationCoverage Coverage);
