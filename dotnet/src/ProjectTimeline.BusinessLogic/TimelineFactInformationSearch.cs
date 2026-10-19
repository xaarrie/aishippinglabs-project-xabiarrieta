using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using ProjectTimeline.DataAccessLogic;

namespace ProjectTimeline.BusinessLogic;

// Captured by the server after authentication; deliberately has no entry anchor.
public sealed record TimelineFactInformationScope(Guid ProjectId, Guid TimelineId);
public sealed record TimelineFactInformationCoverage(int Entries, int EntriesWithoutCompletedExtraction,
    int PendingReviews, int AcceptedValidFacts, bool Truncated, IReadOnlyList<string> Warnings);
public sealed record TimelineFactInformationEvidence(IReadOnlyList<TimelineFactSearchHit> Facts,
    TimelineFactInformationCoverage Coverage, long Revision);

/// <summary>Latest valid extracted or accepted-review state across the timeline. No source-content or vector search.</summary>
public sealed class TimelineFactInformationSearch(ProjectTimelineDbContext db, TimelineFactInformationScope scope,
    Guid actorId, bool administrator)
{
    public const int MaximumResults = 50;
    public static void ValidateQuestion(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || TimelineFactSearch.Terms(text).Length == 0)
            throw new ArgumentException("Provide searchable text of at most 300 characters.");
    }

    public async Task<TimelineFactInformationEvidence> SearchAsync(string question, int limit = 20, CancellationToken ct = default)
    {
        var revision = await AuthorizeAsync(ct);
        ValidateQuestion(question);
        if (limit is < 1 or > MaximumResults) throw new ArgumentOutOfRangeException(nameof(limit));
        var rows = await SearchQuery(TimelineFactSearch.Terms(question), limit + 1).ToListAsync(ct);
        var entries = db.TimelineEntries.AsNoTracking().Where(x => x.TimelineId == scope.TimelineId);
        // Latest extraction job, not any historical completion, determines coverage.
        var missing = await entries.CountAsync(x => db.TimelineFactJobs.Where(j => j.EntryId == x.Id && j.Kind == "Extraction")
            .OrderByDescending(j => j.CreatedAtUtc).ThenByDescending(j => j.Id).Select(j => j.State).FirstOrDefault() != "Completed", ct);
        var pending = await (from review in db.TimelineFactProposals.AsNoTracking()
            join entry in entries on review.EntryId equals entry.Id
            where review.Status == "Pending" select review.Id).CountAsync(ct);
        var warnings = new List<string> {
            "Timeline-wide latest-valid extracted or accepted-review facts only; lexical/entity matching is not exhaustive or contradiction detection.",
            "Matching uses any of the first 12 distinct searchable terms (at least two characters each), not semantic similarity.",
            "Grounded direct extraction is effective immediately. Pending review proposals and original entry content are never search evidence."
        };
        if (missing > 0) warnings.Add("Extraction is absent, pending or failed for some entries; explicit processing/backfill may be required.");
        if (pending > 0) warnings.Add("Pending reviews are excluded until accepted.");
        if (rows.Count > limit) warnings.Add("Matching facts were truncated to the result limit.");
        var coverage = new TimelineFactInformationCoverage(await entries.CountAsync(ct), missing, pending,
            await Effective().CountAsync(ct), rows.Count > limit, warnings);
        await EnsureRevisionAsync(revision, ct);
        return new(rows.Take(limit).ToArray(), coverage, revision);
    }

    public async Task EnsureRevisionAsync(long revision, CancellationToken ct = default)
    {
        if (await AuthorizeAsync(ct) != revision)
            throw new TimelineFactConflictException("Fact state changed during information search.");
    }

    private async Task<long> AuthorizeAsync(CancellationToken ct)
    {
        // Re-read persisted identity on every check: the captured flag is only an upper bound,
        // never authority to retain administrative access after demotion or deactivation.
        var actor = await db.Users.AsNoTracking().Where(x => x.Id == actorId && x.IsActive)
            .Select(x => new { Administrator = db.UserRoles.Any(r => r.Id == x.IDUserRole && r.RoleName == "Admin") })
            .SingleOrDefaultAsync(ct);
        if (actor is null
            || !await new ProjectAuthorization(db).CanReadAsync(scope.ProjectId, actorId, administrator && actor.Administrator, ct))
            throw new UnauthorizedAccessException();
        return await db.Timelines.AsNoTracking().Where(x => x.Id == scope.TimelineId && x.ProjectId == scope.ProjectId)
            .Select(x => (long?)x.FactRevision).SingleOrDefaultAsync(ct) ?? throw new KeyNotFoundException();
    }

    private sealed class Row
    {
        public TimelineFact Fact { get; init; } = null!;
        public TimelineEntry Entry { get; init; } = null!;
    }
    private IQueryable<Row> Effective() =>
        from fact in TimelineFactEvidence.Effective(db, scope.TimelineId).AsNoTracking()
        join entry in db.TimelineEntries on fact.EntryId equals entry.Id
        where entry.Timeline.ProjectId == scope.ProjectId
        select new Row { Fact = fact, Entry = entry };

    private IQueryable<TimelineFactSearchHit> SearchQuery(string[] tokens, int take)
    {
        Expression<Func<Row, bool>> predicate = x => false;
        foreach (var token in tokens)
        {
            var word = token;
            Expression<Func<Row, bool>> match = x => x.Fact.Claim.ToLower().Contains(word)
                || x.Fact.EntityKey.ToLower().Contains(word) || x.Fact.EntityType.ToLower().Contains(word);
            var body = new ReplaceParameter(match.Parameters[0], predicate.Parameters[0]).Visit(match.Body)!;
            predicate = Expression.Lambda<Func<Row, bool>>(Expression.OrElse(predicate.Body, body), predicate.Parameters);
        }
        return Effective().Where(predicate).OrderByDescending(x => x.Entry.EntryNumber).ThenBy(x => x.Fact.Id)
            .Take(take).Select(x => new TimelineFactSearchHit(x.Fact.Id, x.Entry.Id, x.Entry.EntryNumber,
                x.Fact.Claim, x.Fact.EntityKey, x.Fact.EntityType, x.Fact.SourceQuote, x.Fact.SourceKind,
                x.Fact.SourceFingerprint, x.Fact.ClarificationEvidence, x.Fact.CreationReviewId, x.Fact.ExtractionJobId));
    }
    private sealed class ReplaceParameter(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}
