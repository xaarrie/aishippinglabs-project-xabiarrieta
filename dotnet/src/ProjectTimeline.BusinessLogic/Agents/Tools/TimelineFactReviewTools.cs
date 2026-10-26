using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using ProjectTimeline.DataAccessLogic;

namespace ProjectTimeline.BusinessLogic.Agents.Tools;

public sealed record TimelineFactSearchGroup(string Query, IReadOnlyList<TimelineFactSearchHit> Hits);
public sealed record TimelineFactSourceContext(TimelineFactSearchHit Fact, string Title, string Content);

/// <summary>Construct once per run. Register batch search and fetch only; scope/identity never come from the model.</summary>
public sealed class TimelineFactReviewTools(TimelineFactSearch search, CancellationToken cancellationToken = default,
    ProjectTimelineDbContext? db = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<(string Query, int Limit), IReadOnlyList<TimelineFactSearchHit>> cache = new();
    private int calls;
    private readonly Dictionary<Guid, TimelineFactSourceContext> deliveredContexts = new();
    internal IReadOnlyDictionary<Guid, TimelineFactSourceContext> DeliveredContexts => deliveredContexts;
    public const int MaximumCalls = 60;
    public const int MaximumBatchQueries = 20;
    public int RemainingReads => Math.Max(0, MaximumCalls - Volatile.Read(ref calls));
    public static string SearchBatchDescription => $"Search accepted valid facts in the authorized timeline through the current entry. Provide 1..{MaximumBatchQueries} query strings, max 300 characters each; backend uses the first 12 distinct lexical terms. Limit 1..{TimelineFactSearch.MaximumResults} applies independently per query. Returns identifiable groups in input order, retaining each query's rank. Lexical matches are not exhaustive. Repeated queries reuse run-local results. Each uncached query consumes one counted backend-read attempt; batching does not expand the shared budget.";

    // Scalar compatibility shares the same successful-result cache and backend budget.
    public async Task<IReadOnlyList<TimelineFactSearchHit>> search_facts(string terms, int limit = 20)
        => (await search_facts_batch([terms], limit))[0].Hits;

    public async Task<IReadOnlyList<TimelineFactSearchGroup>> search_facts_batch(string[] queries, int limit = 20)
    {
        if (queries is null || queries.Length is < 1 or > MaximumBatchQueries)
            throw new ArgumentException($"Provide 1..{MaximumBatchQueries} search queries.", nameof(queries));
        if (limit is < 1 or > TimelineFactSearch.MaximumResults) throw new ArgumentOutOfRangeException(nameof(limit));
        // Validate every original query before normalization or any backend work. Do not deduplicate lexemes,
        // synonyms or substrings: only whitespace/case equivalence shared by the backend tokenizer.
        var requested = queries.Select(query =>
        {
            if (query is null || query.Length > 300) throw new ArgumentException("Search text must be at most 300 characters.", nameof(queries));
            var normalized = string.Join(" ", query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
            return (Query: query, Key: (normalized, limit));
        }).ToArray();

        await gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireBudget(requested.Select(x => x.Key).Distinct().Count(key => !cache.ContainsKey(key)));
            var groups = new List<TimelineFactSearchGroup>(requested.Length);
            foreach (var request in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!cache.TryGetValue(request.Key, out var hits))
                {
                    calls++; // Failed backend attempts consume budget, but never populate the cache.
                    var result = await search.SearchAsync(request.Query, limit, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    hits = Array.AsReadOnly(result.ToArray());
                    cache.Add(request.Key, hits);
                }
                groups.Add(new(request.Query, hits));
            }
            return groups.AsReadOnly();
        }
        finally { gate.Release(); }
    }

    [Description("Read accepted valid facts by up to 50 distinct IDs in the authorized effective scope. Missing IDs are excluded. Does not read raw entry history.")]
    public Task<IReadOnlyList<TimelineFactSearchHit>> fetch_facts(Guid[] ids)
        => ReadAsync(() => search.FetchAsync(ids, cancellationToken));

    [Description("Read original title/content and fact provenance for up to 50 effective authorized fact IDs. Required before comparing historical claims. Missing or changed historical context is excluded; never infer contradiction from missing context. Read-only and bounded.")]
    public Task<IReadOnlyList<TimelineFactSourceContext>> fetch_fact_contexts(Guid[] ids)
        => ReadAsync<IReadOnlyList<TimelineFactSourceContext>>(async () =>
        {
            var facts = await search.FetchAsync(ids, cancellationToken);
            if (db is null) return [];
            var entryIds = facts.Select(x => x.EntryId).Distinct().ToArray();
            var entries = await db.TimelineEntries.AsNoTracking().Where(x => entryIds.Contains(x.Id))
                .ToListAsync(cancellationToken);
            var contexts = new List<TimelineFactSourceContext>();
            foreach (var fact in facts)
            {
                var entry = entries.SingleOrDefault(x => x.Id == fact.EntryId);
                if (entry is null || TimelineFactEvidence.Fingerprint(entry) != fact.SourceFingerprint) continue;
                var context = new TimelineFactSourceContext(fact, entry.Title, entry.Content);
                deliveredContexts[fact.Id] = context;
                contexts.Add(context);
            }
            return contexts.AsReadOnly();
        });

    private async Task<T> ReadAsync<T>(Func<Task<T>> read)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireBudget(1);
            calls++;
            return await read();
        }
        finally { gate.Release(); }
    }

    private void RequireBudget(int reads)
    {
        if (reads > MaximumCalls - calls) throw new InvalidOperationException("Read-only fact search budget exhausted.");
    }
}
