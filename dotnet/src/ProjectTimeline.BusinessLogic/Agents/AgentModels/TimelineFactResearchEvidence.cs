using System.Collections.Immutable;

namespace ProjectTimeline.BusinessLogic.Agents.AgentModels;

/// <summary>Separate current-entry context, NOT proof of an accepted fact (especially a clarification).
/// Text is a bounded prefix of title + newline + content, verified using the existing source fingerprint.</summary>
public sealed record TimelineFactResearchContext(Guid EvidenceId, Guid EntryId, int EntryNumber,
    Guid AuthorId, ImmutableArray<Guid> AnchorFactIds, string SourceFingerprint, string Text, bool Truncated,
    string EvidenceKind = "SeparateSourceEntryContextNotFactProof");

/// <summary>Internal tool envelope retaining original Entry/Clarification provenance.
/// The toolset projects a separate minimal wire payload; these shared records retain server/API metadata.</summary>
public sealed record TimelineFactResearchToolResult(string Status, ImmutableArray<TimelineFactSearchHit> Facts,
    ImmutableArray<TimelineFactResearchContext> Contexts, bool Truncated);

/// <summary>Immutable server-owned citation allowlist. Returned only after current authorization/evidence validation.
/// Character counters measure .NET strings/JSON, not input tokens or provider calls.</summary>
public sealed record TimelineFactResearchEvidence(long InitialFactRevision, ImmutableArray<TimelineFactSearchHit> Facts,
    ImmutableArray<TimelineFactResearchContext> Contexts, int ToolCalls, int SearchQueries,
    int EmittedEvidenceChars, int SourceContextChars, bool BudgetExhausted, bool Truncated = false);
