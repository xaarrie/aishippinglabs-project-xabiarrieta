using ProjectTimeline.DataAccessLogic;

namespace ProjectTimeline.BusinessLogic;

internal static class TimelineFactReconciliationEvidence
{
    internal static Task ValidateAsync(ProjectTimelineDbContext db, TimelineEntry source,
        IEnumerable<TimelineFactQuestion> questions, CancellationToken ct) =>
        db.ValidateExtractionReviewReferencesAsync(source, questions.Select(x => new TimelineExtractionReviewReference(
            x.ExtractionGenerationId, x.ExtractionAnswerId, x.Question, x.Answer)), ct);
}
