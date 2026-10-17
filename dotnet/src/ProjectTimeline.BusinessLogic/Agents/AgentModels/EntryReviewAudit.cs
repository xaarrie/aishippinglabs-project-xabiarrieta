using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProjectTimeline.BusinessLogic.Agents.AgentModels;

public sealed record EntryReviewAuditRequest(string? ProposalTitle, string? ProposalContent, string? ExpectedSourceFingerprint);
public sealed record EntryReviewSource(Guid EntryId, string Title, string Content, string Fingerprint);

public static class EntryReviewFingerprints
{
    // Versioned JSON string arrays bind field boundaries and identity without normalizing text.
    public static string Source(Guid entryId, string title, string content) => Hash(
        ["entry-review-source-v1", entryId.ToString("D"), title, content]);
    public static string Proposal(string title, string content) => Hash(["entry-review-proposal-v1", title, content]);
    private static string Hash(string[] fields) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(fields)));
}

[JsonConverter(typeof(JsonStringEnumConverter<EntryAuditFactStatus>))]
public enum EntryAuditFactStatus { Preserved, Omitted, Contradicted, Uncertain }

// Only this inventory is model-generated. Coverage, binding and evidence checks are server-owned.
public sealed record EntryReviewAuditInventory(
    [property: Description("Independent atomic source-fact inventory. Empty if the source contains no facts; never invent facts to fill it.")]
    List<EntryAuditFact?>? Facts,
    [property: Description("Proposal claims not supported by the original, each with a literal proposal quote. Empty if none.")]
    List<EntryAuditAddition?>? UnsupportedAdditions);

public sealed record EntryAuditFact(
    string? Fact,
    EntryAuditFactStatus? Status,
    [property: Description("Exact nonempty substring of original title OR content supporting this atomic fact.")]
    string? SourceQuote,
    [property: Description("Exact substring of proposal title OR content for preserved/contradicted facts; null when omitted.")]
    string? ProposalQuote,
    string? Explanation);

public sealed record EntryAuditAddition(string? Finding, string? ProposalQuote);
public sealed record EntryAuditCheckedFact(string? Fact, EntryAuditFactStatus Status, EntryAuditFactStatus? ReportedStatus,
    string? SourceQuote, string? ProposalQuote, string? Explanation,
    bool SourceQuoteVerified, bool ProposalQuoteVerified, string? EvidenceWarning);
public sealed record EntryAuditCheckedAddition(string? Finding, string? ProposalQuote, bool EvidenceVerified);

public sealed record EntryReviewAuditResult(Guid EntryId, string SourceFingerprint, string ProposalFingerprint,
    double? Coverage, bool Inconclusive, int PreservedFacts, int TotalFacts,
    List<EntryAuditCheckedFact> Facts, List<EntryAuditCheckedAddition> UnsupportedAdditions)
{
    public string Limitation => "Inventory and semantic relations are model-estimated. Literal quote checks do not prove meaning or completeness; coverage is not a calibrated probability or Save approval.";
}
