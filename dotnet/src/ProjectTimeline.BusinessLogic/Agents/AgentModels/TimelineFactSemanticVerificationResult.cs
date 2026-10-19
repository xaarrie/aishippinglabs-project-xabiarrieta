using System.Text.Json.Serialization;

namespace ProjectTimeline.BusinessLogic.Agents.AgentModels;

// Transient model assessments, not calibrated probabilities or persistence authorization receipts.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class TimelineFactSemanticVerificationResult
{
    public required List<TimelineFactSemanticAssessment> Assessments { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class TimelineFactSemanticAssessment
{
    public required int CandidateIndex { get; init; }
    public required bool Supported { get; init; }
    public required double SupportScore { get; init; }
    public required List<TimelineFactSemanticIssue> Issues { get; init; }
    public required List<string> EvidenceIds { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<TimelineFactSemanticIssue>))]
public enum TimelineFactSemanticIssue
{
    Numeric,
    Date,
    Actor,
    Negation,
    Unsupported
}
