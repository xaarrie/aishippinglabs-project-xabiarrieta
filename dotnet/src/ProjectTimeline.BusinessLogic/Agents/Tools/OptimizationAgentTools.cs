using Microsoft.EntityFrameworkCore;
using System.ComponentModel;
using ProjectTimeline.DataAccessLogic;

namespace ProjectTimeline.BusinessLogic.Agents.Tools;
public interface IOptimizationAgentTools
{
    [Description("Returns chronological entry IDs after the latest preceding snapshot boundary, through the current entry inclusive. No future entries are available.")]
    Task<List<Guid>> get_entry_ids();
    [Description("Returns persisted content for an entry in the current review context. Use get_entry_ids to discover allowed IDs.")]
    Task<string> fetch_entry(
        [Description("Entry ID from get_entry_ids.")] Guid entryId);
}

/// <summary>Read-only review context anchored to a persisted entry in a single timeline.</summary>
public sealed class OptimizationAgentTools(ProjectTimelineDbContext db, Guid timelineId, Guid currentEntryId):IOptimizationAgentTools
{
    [Description("Returns chronological entry IDs after the latest preceding snapshot boundary, through the current entry inclusive. No future entries are available.")]
    public async Task<List<Guid>> get_entry_ids()
    {
        return await (await GetWindowAsync())
            .OrderBy(entry => entry.EntryNumber)
            .Select(entry => entry.Id)
            .ToListAsync();
    }

    [Description("Returns persisted content for an entry in the current review context. Use get_entry_ids to discover allowed IDs.")]
    public async Task<string> fetch_entry(
        [Description("Entry ID from get_entry_ids.")] Guid entryId)
    {
        var content = await (await GetWindowAsync())
            .Where(entry => entry.Id == entryId)
            .Select(entry => entry.Content)
            .SingleOrDefaultAsync();
        return content ?? throw new InvalidOperationException("Entry was not found in the current review context.");
    }

    private async Task<IQueryable<TimelineEntry>> GetWindowAsync()
    {
        // Always query persisted state: a caller-supplied number or unsaved tracked entity is not an anchor.
        var currentNumber = await db.TimelineEntries.AsNoTracking()
            .Where(entry => entry.TimelineId == timelineId && entry.Id == currentEntryId)
            .Select(entry => (int?)entry.EntryNumber)
            .SingleOrDefaultAsync()
            ?? throw new InvalidOperationException("Current entry was not found in the selected timeline.");

        // Max over actual members handles noncontiguous snapshots. Reject whole snapshots containing
        // current/future or foreign entries; filtering individual members would create a false boundary.
        var boundary = await db.TimelineSnapshots.AsNoTracking()
            .Where(snapshot => snapshot.TimelineId == timelineId && snapshot.Entries.Any()
                && snapshot.Entries.All(member => member.Entry.TimelineId == timelineId))
            .Select(snapshot => snapshot.Entries.Max(member => (int?)member.Entry.EntryNumber))
            .Where(maximum => maximum < currentNumber)
            .MaxAsync();

        var window = db.TimelineEntries.AsNoTracking()
            .Where(entry => entry.TimelineId == timelineId && entry.EntryNumber <= currentNumber);
        if (boundary.HasValue)
            window = window.Where(entry => entry.EntryNumber > boundary.Value);
        return window;
    }
}
