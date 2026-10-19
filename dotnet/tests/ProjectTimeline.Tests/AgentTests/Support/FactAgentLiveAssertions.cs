using System.Text.RegularExpressions;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.AgentModels;
using Xunit;

namespace ProjectTimeline.Tests.AgentTests.Support;

internal static class FactAgentLiveAssertions
{
    public static bool Matches(string text, string pattern) => Regex.IsMatch(text, pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static void Extraction(ExtractionLiveCase scenario, IReadOnlyList<TimelineFactCandidate> candidates)
    {
        if (scenario.Canonical.Length == 0) { Assert.Empty(candidates); return; }
        Assert.NotEmpty(candidates);
        // Require relationships within individual claims, without fixing full prose or an atomic split count.
        foreach (var anchors in scenario.RequiredClaims)
            Assert.Contains(candidates, c => anchors.All(pattern => Matches(c.Claim, pattern)));
        var normalized = candidates.Select(c => Regex.Replace(c.Claim.Trim().ToLowerInvariant(), @"\s+", " ")).ToArray();
        Assert.Equal(normalized.Length, normalized.Distinct(StringComparer.Ordinal).Count());
        Assert.All(candidates, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Claim));
            Assert.False(string.IsNullOrWhiteSpace(c.EntityKey));
            Assert.False(string.IsNullOrWhiteSpace(c.EntityType));
            // Draft quotes may be approximate. Literal reconstruction belongs to the semantic verifier.
            Assert.False(string.IsNullOrWhiteSpace(c.SourceQuote));
            Assert.Equal("Entry", c.SourceKind);
            Assert.Contains(scenario.RequiredClaims, anchors => anchors.All(pattern => Matches(c.Claim, pattern)));
            foreach (var unsupported in scenario.Forbidden)
                Assert.False(Matches(c.Claim, unsupported), $"Unexpected semantic anchor: {unsupported}");
            var source = scenario.Source.Title + " " + scenario.Source.Content;
            foreach (Match number in Regex.Matches(c.Claim, @"\b\d+\b"))
                Assert.True(Matches(source, @"\b" + Regex.Escape(number.Value) + @"\b"), "Invented numeric detail");
        });
    }

    // Whole marker tokens only; underscores delimit normalized entity keys as well as punctuation/whitespace.
    public static void NoPreviousEntryMarkers(IReadOnlyList<TimelineFactCandidate> candidates) =>
        Assert.All(candidates, c => Assert.DoesNotMatch(@"(?i)(?<![\p{L}\p{N}])(?:Nora|C-7|red)(?![\p{L}\p{N}])",
            c.Claim + " " + c.SourceQuote + " " + c.EntityKey));

    public static void GroundedSearch(TimelineFactInformationResponse result, Guid factId, Guid entryId, Guid? reviewId)
    {
        var fact = Assert.Single(result.Facts);
        Assert.Equal(factId, fact.Id);
        Assert.Equal("Nora owns Atlas delivery.", fact.Claim);
        Assert.Equal(fact.Claim, fact.SourceQuote);
        Assert.Equal("Entry", fact.SourceKind);
        var citation = Assert.Single(result.Citations);
        Assert.Equal(factId, citation.FactId);
        Assert.Equal(entryId, citation.EntryId);
        Assert.Equal(reviewId, citation.CreationReviewId);
        Assert.Equal(fact.EntryNumber, citation.EntryNumber);
        Assert.Equal(fact.ExtractionJobId, citation.ExtractionJobId);
        Assert.True(Matches(result.Content, "Atlas"));
        Assert.DoesNotMatch("(?i)violet|Orion|9000|Liam", result.Content);
        Assert.NotEmpty(result.Coverage.Warnings);
        Assert.True(result.Coverage.EntriesWithoutCompletedExtraction > 0);
        Assert.False(result.Coverage.Truncated);
    }

    public static void SupportedBatch(TimelineFactExtraction source, IReadOnlyList<TimelineFactCandidate> candidates,
        TimelineFactSemanticVerificationResult result)
    {
        Assert.Equal(candidates.Count, result.Assessments.Count);
        Assert.Equal(Enumerable.Range(0, candidates.Count), result.Assessments.Select(x => x.CandidateIndex).Order());
        var spans = TimelineFactSourceEvidence.Create(source).ToDictionary(x => x.Id);
        foreach (var item in result.Assessments)
        {
            Assert.True(item.Supported);
            Assert.Empty(item.Issues);
            Assert.True(double.IsFinite(item.SupportScore));
            Assert.InRange(item.SupportScore, 0, 1); // Descriptive range, never an acceptance threshold.
            Assert.NotEmpty(item.EvidenceIds);
            Assert.Equal(item.EvidenceIds.Count, item.EvidenceIds.Distinct().Count());
            Assert.All(item.EvidenceIds, id => Assert.True(spans.ContainsKey(id)));
            var selected = item.EvidenceIds.Select(id => spans[id]).ToArray();
            Assert.Single(selected.Select(x => x.Source).Distinct());
            var text = selected[0].Source == "Title" ? source.Title : source.Content;
            var start = selected.Min(x => x.Start);
            var end = selected.Max(x => x.Start + x.Length);
            Assert.Contains(candidates[item.CandidateIndex].SourceQuote, text[start..end], StringComparison.Ordinal);
        }
    }
}
