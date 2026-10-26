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

/// <summary>Latest valid extracted or accepted-review state across the timeline. Fact-only lexical full-text search.</summary>
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
        var coverage = await CoverageAsync(rows.Count > limit, ct);
        await EnsureRevisionAsync(revision, ct);
        return new(rows.Take(limit).ToArray(), coverage, revision);
    }

    /// <summary>Coverage only: no lexical search outside the operation's counted tools.</summary>
    public async Task<TimelineFactInformationEvidence> CaptureCoverageAsync(string question, CancellationToken ct = default)
    {
        var revision = await AuthorizeAsync(ct);
        ValidateQuestion(question);
        var coverage = await CoverageAsync(false, ct);
        await EnsureRevisionAsync(revision, ct);
        return new([], coverage, revision);
    }

    private async Task<TimelineFactInformationCoverage> CoverageAsync(bool truncated, CancellationToken ct)
    {
        var entries = db.TimelineEntries.AsNoTracking().Where(x => x.TimelineId == scope.TimelineId);
        // Latest extraction job, not any historical completion, determines coverage.
        var missing = await entries.CountAsync(x => db.TimelineFactJobs.Where(j => j.EntryId == x.Id && j.Kind == "Extraction")
            .OrderByDescending(j => j.CreatedAtUtc).ThenByDescending(j => j.Id).Select(j => j.State).FirstOrDefault() != "Completed", ct);
        var pending = await (from review in db.TimelineFactProposals.AsNoTracking()
            join entry in entries on review.EntryId equals entry.Id
            where review.Status == "Pending" select review.Id).CountAsync(ct);
        var warnings = new List<string> {
            "Timeline-wide latest-valid extracted or accepted-review facts only; lexical/entity matching is not exhaustive or contradiction detection.",
            "Matching uses PostgreSQL simple whole lexemes from any of the first 12 distinct searchable terms (at least two characters each), ranked by relevance; no stemming, typo tolerance, synonyms or accent folding.",
            "Grounded direct extraction is effective immediately. Pending reviews are excluded. Fact search is facts-only; separately verified source-entry context is not fact proof."
        };
        if (missing > 0) warnings.Add("Extraction is absent, pending or failed for some entries; explicit processing/backfill may be required.");
        if (pending > 0) warnings.Add("Pending reviews are excluded until accepted.");
        if (truncated) warnings.Add("Matching facts were truncated to the result limit.");
        return new TimelineFactInformationCoverage(await entries.CountAsync(ct), missing, pending,
            await Effective().CountAsync(ct), truncated, warnings);
    }

    public async Task EnsureRevisionAsync(long revision, CancellationToken ct = default)
    {
        if (await AuthorizeAsync(ct) != revision)
            throw new TimelineFactConflictException("Fact state changed during information search.");
    }

    internal async Task<long> AuthorizeAsync(CancellationToken ct)
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

    private IQueryable<TimelineFactSearchHit> SearchQuery(string[] tokens, int take) =>
        TimelineFactFullTextSearch.Rank(db, Effective(), x => x.Fact, tokens)
            .ThenByDescending(x => x.Entry.EntryNumber).ThenBy(x => x.Fact.Id).Take(take).Select(ResearchHit);

    // Both timeline-wide paths share relevance and stable tie-breaks; counted-tool and citation APIs stay intact.
    private IQueryable<TimelineFactSearchHit> ResearchQuery(string[] tokens, int take) => SearchQuery(tokens, take);

    private static readonly Expression<Func<Row, TimelineFactSearchHit>> ResearchHit = x => new(
        x.Fact.Id, x.Entry.Id, x.Entry.EntryNumber, x.Fact.Claim, x.Fact.EntityKey, x.Fact.EntityType,
        x.Fact.SourceQuote, x.Fact.SourceKind, x.Fact.SourceFingerprint, x.Fact.ClarificationEvidence,
        x.Fact.CreationReviewId, x.Fact.ExtractionJobId);

    internal Task<List<TimelineFactSearchHit>> ResearchSearchAsync(string query, int take, CancellationToken ct)
        => ResearchQuery(TimelineFactSearch.Terms(query), take).ToListAsync(ct);

    internal Task<List<TimelineFactSearchHit>> ResearchFetchAsync(Guid[] ids, CancellationToken ct)
        => Effective().Where(x => ids.Contains(x.Fact.Id)).OrderBy(x => x.Fact.Id).Select(ResearchHit).ToListAsync(ct);

    // Only the operation toolset calls this, after resolving a previously returned, currently effective fact.
    internal Task<TimelineEntry?> ResearchSourceAsync(Guid entryId, CancellationToken ct)
        => db.TimelineEntries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == entryId
            && x.TimelineId == scope.TimelineId && x.Timeline.ProjectId == scope.ProjectId, ct);

}
