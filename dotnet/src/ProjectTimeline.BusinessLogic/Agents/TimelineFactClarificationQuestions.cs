using ProjectTimeline.BusinessLogic.Agents.AgentModels;
using ProjectTimeline.DataAccessLogic;

namespace ProjectTimeline.BusinessLogic.Agents;

// Structural admission and citation binding, not a semantic proof of question neutrality.
internal static class TimelineFactClarificationQuestions
{
    internal static IReadOnlyList<TimelineEntryQuestion> Validate(List<TimelineFactExtractionQuestion> questions,
        TimelineFactExtraction source, IReadOnlyList<TimelineFactSourceSpan> spans)
    {
        if (questions is null || questions.Count + (source.Input?.Answers.Count ?? 0) > 20 || questions.Any(x => x is null
            || !TimelineEntryClarificationEvidence.Text(x.Key, 100)
            || !TimelineEntryClarificationEvidence.Text(x.Text, 2000)
            || !x.Text.EndsWith("?", StringComparison.Ordinal)
            || x.Reason is not ("MissingInformation" or "AmbiguousReference"))
            || questions.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != questions.Count)
            throw new TimelineFactProviderException("InvalidResponse", "InvalidQuestions");
        foreach (var question in questions)
            TimelineFactSourceEvidence.Reconstruct(source, spans, question.EvidenceIds);
        return Array.AsReadOnly(questions.Select(x => new TimelineEntryQuestion(x.Key, x.Text)).ToArray());
    }
}
