using System.ComponentModel;
using System.Text.Json.Serialization;

namespace ProjectTimeline.BusinessLogic.Agents.AgentModels;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class TimelineFactInformationResult
{
    public string Content { get; set; } = "";
    [Description("Fact Id GUIDs from Facts returned by research tools in this operation, without duplicates. Never context EvidenceIds, entry IDs or fingerprints.")]
    public List<Guid> CitedFactIds { get; set; } = [];
    [Description("Only EvidenceId GUIDs from Contexts returned by fetch_source_entry in this operation, without duplicates. No fetched context or no context used => []. Never fingerprints, entry IDs or fact Id GUIDs.")]
    public List<Guid> CitedContextEvidenceIds { get; set; } = [];
}

public sealed record TimelineFactInformationCitation(Guid FactId, Guid EntryId, int EntryNumber, Guid? CreationReviewId, Guid? ExtractionJobId = null);
// Public projection deliberately omits internal AuthorId. Context is never a fact citation.
public sealed record TimelineFactInformationSourceContext(Guid EvidenceId, Guid EntryId, int EntryNumber,
    IReadOnlyList<Guid> AnchorFactIds, string SourceFingerprint, string Text, bool Truncated, string EvidenceKind);
public sealed record TimelineFactInformationContextCitation(Guid EvidenceId, Guid EntryId, int EntryNumber,
    IReadOnlyList<Guid> AnchorFactIds, string SourceFingerprint, string EvidenceKind);
public sealed record TimelineFactInformationResearchLimits(int MaxModelCalls, int MaxToolCalls, int MaxSearchQueries,
    int MaxDistinctFacts, int MaxHitsPerSearch, int MaxSourceContexts, int MaxCharsPerContext,
    int MaxTotalSourceContextChars, int MaxTotalSerializedToolEvidenceChars, int MaxOutputTokens, double DeadlineSeconds);
public sealed record TimelineFactInformationResearchStatus(string Status, string? StopReason, int ModelCalls,
    int ToolCalls, int SearchQueries, bool EvidenceTruncated, bool BudgetExhausted,
    IReadOnlyList<string> Warnings, TimelineFactInformationResearchLimits Limits);

// Keep the existing four-argument constructor and fields for older consumers.
public sealed record TimelineFactInformationResponse(string Content, IReadOnlyList<TimelineFactSearchHit> Facts,
    IReadOnlyList<TimelineFactInformationCitation> Citations, TimelineFactInformationCoverage Coverage)
{
    public IReadOnlyList<TimelineFactInformationSourceContext> SourceContexts { get; init; } = [];
    public IReadOnlyList<TimelineFactInformationContextCitation> ContextCitations { get; init; } = [];
    public TimelineFactInformationResearchStatus? Research { get; init; }
}
