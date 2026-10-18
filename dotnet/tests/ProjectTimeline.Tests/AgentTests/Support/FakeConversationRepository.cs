using ProjectTimeline.BusinessLogic.Agents.Tools;
using System.ComponentModel;
using System.Text.Json;

namespace ProjectTimeline.Tests.AgentTests.Support;

internal sealed record PersistedConversationEntry(
    Guid Id, Guid TimelineId, int Sequence, string Title, string Content);

internal sealed record ConversationSnapshot(Guid TimelineId, IReadOnlyList<Guid> EntryIds);
internal sealed record ContextRead(string Function, Guid TimelineId, Guid CurrentEntryId, Guid? EntryId = null);

/// <summary>
/// Synthetic persisted context, not an ORM replacement. Entries are immutable, and there is no save API.
/// Anchor selection belongs to each adapter so two reviews cannot change each other's window.
/// </summary>
internal sealed class FakeConversationRepository
{
    public IReadOnlyList<PersistedConversationEntry> Entries { get; }
    public IReadOnlyList<ConversationSnapshot> Snapshots { get; }
    public string OriginalState { get; }
    public List<ContextRead> Reads { get; } = [];
    public Exception? IdListFailure { get; set; }
    public Dictionary<Guid, Exception> EntryFailures { get; } = [];

    public FakeConversationRepository(
        IEnumerable<PersistedConversationEntry> entries, IEnumerable<ConversationSnapshot>? snapshots = null)
    {
        Entries = Array.AsReadOnly(entries.ToArray());
        Snapshots = Array.AsReadOnly((snapshots ?? []).Select(snapshot => new ConversationSnapshot(
            snapshot.TimelineId, Array.AsReadOnly(snapshot.EntryIds.ToArray()))).ToArray());
        if (Entries.Select(entry => entry.Id).Distinct().Count() != Entries.Count
            || Entries.Select(entry => (entry.TimelineId, entry.Sequence)).Distinct().Count() != Entries.Count)
            throw new ArgumentException("Persisted identities and per-timeline sequences must be unique.");
        OriginalState = CaptureState();
    }

    // Failure controls and read evidence are deliberately not persisted entry state.
    public string CaptureState() => JsonSerializer.Serialize(new { Entries, Snapshots });

    public FakeOptimizationAgentTools SelectAnchor(Guid timelineId, Guid currentEntryId)
        => new(this, timelineId, currentEntryId);

    internal List<Guid> ReadIds(Guid timelineId, Guid currentEntryId)
    {
        Reads.Add(new("get_entry_ids", timelineId, currentEntryId));
        if (IdListFailure is not null)
            throw IdListFailure;
        return Window(timelineId, currentEntryId).Select(entry => entry.Id).ToList();
    }

    internal string ReadEntry(Guid timelineId, Guid currentEntryId, Guid entryId)
    {
        Reads.Add(new("fetch_entry", timelineId, currentEntryId, entryId));
        var entry = Window(timelineId, currentEntryId).SingleOrDefault(entry => entry.Id == entryId)
            ?? throw new InvalidOperationException("Entry was not found in the current review context.");
        if (EntryFailures.TryGetValue(entryId, out var failure))
            throw failure;
        return entry.Content;
    }

    private IEnumerable<PersistedConversationEntry> Window(Guid timelineId, Guid currentEntryId)
    {
        var current = Entries.SingleOrDefault(entry => entry.TimelineId == timelineId && entry.Id == currentEntryId)
            ?? throw new InvalidOperationException("Current entry was not found in the selected timeline.");
        // Match the production boundary rule: ignore entire malformed/current/future snapshots.
        var boundary = Snapshots.Where(snapshot => snapshot.TimelineId == timelineId && snapshot.EntryIds.Count > 0)
            .Where(snapshot => snapshot.EntryIds.All(id => Entries.Any(entry => entry.Id == id && entry.TimelineId == timelineId)))
            .Select(snapshot => snapshot.EntryIds.Max(id => Entries.Single(entry => entry.Id == id).Sequence))
            .Where(maximum => maximum < current.Sequence)
            .Select(maximum => (int?)maximum)
            .Max();
        return Entries.Where(entry => entry.TimelineId == timelineId && entry.Sequence <= current.Sequence
                && (!boundary.HasValue || entry.Sequence > boundary.Value))
            .OrderBy(entry => entry.Sequence);
    }
}

/// <summary>Only context access is faked; this adapter never produces an optimized entry.</summary>
internal sealed class FakeOptimizationAgentTools(
    FakeConversationRepository repository, Guid timelineId, Guid currentEntryId) : IOptimizationAgentTools
{
    [Description("Returns chronological entry IDs after the latest preceding snapshot boundary, through the current entry inclusive. No future entries are available.")]
    public Task<List<Guid>> get_entry_ids() => Task.FromResult(repository.ReadIds(timelineId, currentEntryId));

    [Description("Returns persisted content for an entry in the current review context. Use get_entry_ids to discover allowed IDs.")]
    public Task<string> fetch_entry([Description("Entry ID from get_entry_ids.")] Guid entryId)
        => Task.FromResult(repository.ReadEntry(timelineId, currentEntryId, entryId));
}
