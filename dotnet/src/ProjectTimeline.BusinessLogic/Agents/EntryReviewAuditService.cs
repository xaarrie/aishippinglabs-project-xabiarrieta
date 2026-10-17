using System.Text.Json;
using ProjectTimeline.BusinessLogic.Agents.AgentModels;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.DataAccessLogic;

namespace ProjectTimeline.BusinessLogic.Agents;

public interface IEntryReviewAuditService
{
    Task<EntryReviewAuditResult> AuditAsync(TimelineEntry entry, EntryReviewAuditRequest request, CancellationToken ct = default);
}

public sealed class EntryReviewAuditService(Func<ICustomAgent>? agentFactory = null) : IEntryReviewAuditService
{
    public async Task<EntryReviewAuditResult> AuditAsync(TimelineEntry entry, EntryReviewAuditRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var stage = EntryReviewStage.AuditInitialization;
        try
        {
            var auditor = new EntryReviewAuditAgent(agentFactory?.Invoke());
            var message = JsonSerializer.Serialize(new
            {
                original = new { title = entry.Title, content = entry.Content },
                proposal = new { title = request.ProposalTitle, content = request.ProposalContent }
            });
            ct.ThrowIfCancellationRequested();
            stage = EntryReviewStage.AuditInvocation;
            // ICustomAgent has no cancellation overload; honor caller cancellation at both boundaries.
            var response = await auditor.RunAsync(message);
            ct.ThrowIfCancellationRequested();
            stage = EntryReviewStage.AuditResultValidation;
            var inventory = response?.Result ?? throw new InvalidOperationException("No audit inventory.");
            return Check(entry, request, inventory);
        }
        catch (Exception error)
        {
            ct.ThrowIfCancellationRequested();
            throw new EntryReviewFailure(stage, error);
        }
    }

    private static EntryReviewAuditResult Check(TimelineEntry entry, EntryReviewAuditRequest request, EntryReviewAuditInventory inventory)
    {
        var structurallyValid = inventory.Facts is not null && inventory.UnsupportedAdditions is not null;
        var facts = new List<EntryAuditCheckedFact>();
        foreach (var fact in inventory.Facts ?? [])
        {
            var valid = fact is not null && !string.IsNullOrWhiteSpace(fact.Fact) &&
                fact.Status.HasValue && Enum.IsDefined(fact.Status.Value);
            structurallyValid &= valid;
            var sourceVerified = Contains(entry.Title, entry.Content, fact?.SourceQuote);
            var proposalVerified = Contains(request.ProposalTitle, request.ProposalContent, fact?.ProposalQuote);
            var reported = fact?.Status;
            var grounded = valid && sourceVerified &&
                (reported is not (EntryAuditFactStatus.Preserved or EntryAuditFactStatus.Contradicted) || proposalVerified) &&
                (fact?.ProposalQuote is null || proposalVerified);
            facts.Add(new(fact?.Fact, grounded ? reported!.Value : EntryAuditFactStatus.Uncertain,
                reported.HasValue && Enum.IsDefined(reported.Value) ? reported : null,
                fact?.SourceQuote, fact?.ProposalQuote, fact?.Explanation, sourceVerified, proposalVerified,
                grounded ? null : "Unverified finding: invalid fact/status or missing/nonliteral evidence. Not counted as preserved."));
        }

        // Deduplicate exact source facts, not distinct facts that happen to share evidence.
        // Conflicting duplicate assessments retain the least favorable grounded issue, never extra credit.
        facts = facts.GroupBy(f => (Fact: f.Fact?.Trim(), Quote: f.SourceQuote?.Trim()))
            .Select(g => g.OrderBy(f => f.Status switch
            {
                EntryAuditFactStatus.Contradicted => 0,
                EntryAuditFactStatus.Omitted => 1,
                EntryAuditFactStatus.Uncertain => 2,
                _ => 3
            }).First()).ToList();
        var additions = (inventory.UnsupportedAdditions ?? []).Select(a =>
            new EntryAuditCheckedAddition(a?.Finding, a?.ProposalQuote,
                !string.IsNullOrWhiteSpace(a?.Finding) && Contains(request.ProposalTitle, request.ProposalContent, a.ProposalQuote))).ToList();
        var preserved = facts.Count(f => f.Status == EntryAuditFactStatus.Preserved);
        double? coverage = structurallyValid && facts.Count > 0 ? (double)preserved / facts.Count : null;
        return new(entry.Id, EntryReviewFingerprints.Source(entry.Id, entry.Title, entry.Content),
            EntryReviewFingerprints.Proposal(request.ProposalTitle!, request.ProposalContent!), coverage,
            coverage is null || facts.Any(f => f.Status == EntryAuditFactStatus.Uncertain) || additions.Any(a => !a.EvidenceVerified),
            preserved, facts.Count, facts, additions);
    }

    private static bool Contains(string? title, string? content, string? quote) => !string.IsNullOrWhiteSpace(quote) &&
        (title?.Contains(quote, StringComparison.Ordinal) == true || content?.Contains(quote, StringComparison.Ordinal) == true);
}
