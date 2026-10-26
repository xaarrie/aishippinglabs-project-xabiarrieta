using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using ProjectTimeline.DataAccessLogic;

namespace ProjectTimeline.BusinessLogic;

public sealed record TimelineFactSearchHit(Guid Id, Guid EntryId, int EntryNumber, string Claim,
    string EntityKey, string EntityType, string SourceQuote, string SourceKind, string SourceFingerprint,
    string ClarificationEvidence, Guid? CreationReviewId, Guid? ExtractionJobId = null);
public sealed record TimelineFactCoverage(int Entries, int EntriesWithoutCompletedExtraction,
    string CurrentExtractionState, string SearchStatus = "BoundedLexicalSearchNotExhaustive");

/// <summary>Read-only access with server-captured authorization and chronology. SQL filters before Take;
/// PostgreSQL simple full-text rank precedes stable chronology/ID ties, without entry-body search.</summary>
public sealed class TimelineFactSearch(ProjectTimelineDbContext db, TimelineFactScope scope, Guid actorId, bool administrator)
{
    public const int MaximumResults = 50;
    public async Task<IReadOnlyList<TimelineFactSearchHit>> SearchAsync(string terms, int limit = 20, CancellationToken ct = default)
    {
        var source = await AuthorizeAsync(ct);
        var tokens = Terms(terms);
        if (limit is < 1 or > MaximumResults) throw new ArgumentOutOfRangeException(nameof(limit));
        if (tokens.Length == 0) return [];
        return await SearchQuery(source.EntryNumber, tokens, limit).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TimelineFactSearchHit>> FetchAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        var source = await AuthorizeAsync(ct);
        if (ids.Count > MaximumResults || ids.Distinct().Count() != ids.Count)
            throw new ArgumentException("Provide at most 50 distinct fact IDs.", nameof(ids));
        return await Effective(source.EntryNumber).Where(x => ids.Contains(x.Fact.Id))
            .OrderBy(x => x.Entry.EntryNumber).ThenBy(x => x.Fact.Id).Select(Hit).ToListAsync(ct);
    }

    public async Task<TimelineFactCoverage> CoverageAsync(CancellationToken ct = default)
    {
        var source = await AuthorizeAsync(ct);
        var entries = db.TimelineEntries.AsNoTracking().Where(x => x.TimelineId == scope.TimelineId
            && x.EntryNumber <= source.EntryNumber);
        var missing = await entries.CountAsync(x => !db.TimelineFactJobs.Any(j => j.EntryId == x.Id
            && j.Kind == "Extraction" && j.State == "Completed"), ct);
        var state = await db.TimelineFactJobs.AsNoTracking().Where(x => x.EntryId == source.Id && x.Kind == "Extraction")
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).Select(x => x.State).FirstOrDefaultAsync(ct);
        return new(await entries.CountAsync(ct), missing, state ?? "AbsentRequiresExplicitBackfill");
    }

    internal async Task<TimelineEntry> AuthorizeAsync(CancellationToken ct)
    {
        if (!await new ProjectAuthorization(db).CanReadAsync(scope.ProjectId, actorId, administrator, ct)
            || !await db.Users.AnyAsync(x => x.Id == actorId && x.IsActive, ct)) throw new UnauthorizedAccessException();
        return await db.TimelineEntries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == scope.EntryId
            && x.TimelineId == scope.TimelineId && x.Timeline.ProjectId == scope.ProjectId, ct)
            ?? throw new KeyNotFoundException("Entry not found in this scope.");
    }

    internal static string[] Terms(string text) => TimelineFactFullTextSearch.Terms(text);

    private IQueryable<TimelineFactSearchHit> SearchQuery(int entryNumber, string[] tokens, int limit) =>
        TimelineFactFullTextSearch.Rank(db, Effective(entryNumber), x => x.Fact, tokens)
            .ThenByDescending(x => x.Entry.EntryNumber).ThenBy(x => x.Fact.Id).Take(limit).Select(Hit);
    private sealed class FactRow
    {
        public TimelineFact Fact { get; init; } = null!;
        public TimelineEntry Entry { get; init; } = null!;
    }
    private static readonly Expression<Func<FactRow, TimelineFactSearchHit>> Hit = x => new(
        x.Fact.Id, x.Fact.EntryId, x.Entry.EntryNumber, x.Fact.Claim, x.Fact.EntityKey, x.Fact.EntityType,
        x.Fact.SourceQuote, x.Fact.SourceKind, x.Fact.SourceFingerprint, x.Fact.ClarificationEvidence, x.Fact.CreationReviewId, x.Fact.ExtractionJobId);

    private IQueryable<FactRow> Effective(int entryNumber) =>
        from fact in TimelineFactEvidence.Effective(db, scope.TimelineId, entryNumber).AsNoTracking()
        join entry in db.TimelineEntries on fact.EntryId equals entry.Id
        where entry.Timeline.ProjectId == scope.ProjectId
        select new FactRow { Fact = fact, Entry = entry };
}
