using ProjectTimeline.DataAccessLogic;

namespace ProjectTimeline.BusinessLogic.Agents;

public sealed record TimelineFactSourceSpan(string Id, string Source, int Start, int Length, string Text,
    string SourceKind = "Entry", Guid? AnswerId = null);

/// <summary>Shared server literal codec: lossless UTF-16 windows and job/answer-bound identities.</summary>
public static class TimelineFactSourceEvidence
{
    public static IReadOnlyList<TimelineFactSourceSpan> Create(TimelineFactExtraction source)
    {
        source = source.Freeze();
        TimelineFactStructuredResponse.ValidateSource(source);
        return Array.AsReadOnly(TimelineExtractionEvidence.Create(source.Title, source.Content, source.EntryId, source.Input)
            .Select(FromStored).ToArray());
    }
    internal static TimelineExtractionSpan ToStored(TimelineFactSourceSpan span) =>
        new(span.Id, span.Source, span.Start, span.Length, span.Text, span.SourceKind, span.AnswerId);
    private static TimelineFactSourceSpan FromStored(TimelineExtractionSpan span) =>
        new(span.Id, span.Source, span.Start, span.Length, span.Text, span.SourceKind, span.AnswerId);
    internal static string Reconstruct(TimelineFactExtraction source, IReadOnlyList<TimelineFactSourceSpan> spans,
        IReadOnlyList<string> evidenceIds)
    {
        try { return TimelineExtractionEvidence.Reconstruct(source.Title, source.Content, source.Input, spans.Select(ToStored).ToArray(), evidenceIds); }
        catch (TimelineFactConflictException) { throw new TimelineFactProviderException("InvalidResponse", "InvalidEvidence"); }
    }
    internal static TimelineFactSourceSpan Primary(IEnumerable<TimelineFactSourceSpan> spans) =>
        FromStored(TimelineExtractionEvidence.Primary(spans.Select(ToStored)));
}
