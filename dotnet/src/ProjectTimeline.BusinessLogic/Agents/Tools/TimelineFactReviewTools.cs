using System.ComponentModel;

namespace ProjectTimeline.BusinessLogic.Agents.Tools;

/// <summary>Construct once per run. Only these two methods become tools; scope/identity never come from the model.</summary>
public sealed class TimelineFactReviewTools(TimelineFactSearch search, CancellationToken cancellationToken = default)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private int calls;
    public const int MaximumCalls = 8;

    [Description("Search accepted valid facts in the authorized timeline through the current entry. Partial lexical/entity matches only; not exhaustive. Max 300 characters, 12 terms, limit 1..50.")]
    public Task<IReadOnlyList<TimelineFactSearchHit>> search_facts(string terms, int limit = 20)
        => ReadAsync(() => search.SearchAsync(terms, limit, cancellationToken));

    [Description("Read accepted valid facts by up to 50 distinct IDs in the authorized effective scope. Missing IDs are excluded. Does not read raw entry history.")]
    public Task<IReadOnlyList<TimelineFactSearchHit>> fetch_facts(Guid[] ids)
        => ReadAsync(() => search.FetchAsync(ids, cancellationToken));

    private async Task<T> ReadAsync<T>(Func<Task<T>> read)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (++calls > MaximumCalls) throw new InvalidOperationException("Read-only fact search budget exhausted.");
            return await read();
        }
        finally { gate.Release(); }
    }
}
