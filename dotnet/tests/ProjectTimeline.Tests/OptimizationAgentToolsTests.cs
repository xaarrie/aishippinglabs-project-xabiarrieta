using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.BusinessLogic.Agents.Models;
using ProjectTimeline.BusinessLogic.Agents.Tools;
using ProjectTimeline.BusinessLogic.Agents.Tracking;
using ProjectTimeline.DataAccessLogic;
using System.Text.Json;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class OptimizationAgentToolsTests
{
    [Fact]
    public async Task No_snapshot_returns_entries_through_current_in_entry_number_order()
    {
        await using var db = CreateDb();
        var timeline = Guid.NewGuid();
        var future = Entry(db, timeline, 50);
        var current = Entry(db, timeline, 30);
        var first = Entry(db, timeline, 10);
        var second = Entry(db, timeline, 20);
        Entry(db, Guid.NewGuid(), 15);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var tools = new OptimizationAgentTools(db, timeline, current.Id);

        Assert.Equal([first.Id, second.Id, current.Id], await tools.get_entry_ids());
        Assert.Equal(current.Content, await tools.fetch_entry(current.Id));
        Assert.Equal(first.Content, await tools.fetch_entry(first.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.fetch_entry(future.Id));
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Window_uses_highest_preceding_snapshot_max_not_snapshot_number_or_membership_gaps()
    {
        await using var db = CreateDb();
        var timeline = Guid.NewGuid();
        var entries = Enumerable.Range(1, 9).Select(n => Entry(db, timeline, n)).ToArray();
        Snapshot(db, timeline, 90, entries[0], entries[1]);
        Snapshot(db, timeline, 1, entries[0], entries[4]); // Noncontiguous: max is 5.
        Snapshot(db, timeline, 100, entries[2], entries[7]); // Future member: ignore entire snapshot.
        Snapshot(db, timeline, 101, entries[0], entries[6]); // Current member: not strictly preceding.
        Snapshot(db, timeline, 102); // Empty snapshot has no boundary.
        await db.SaveChangesAsync();
        var tools = new OptimizationAgentTools(db, timeline, entries[6].Id);

        Assert.Equal([entries[5].Id, entries[6].Id], await tools.get_entry_ids());
        Assert.Equal(entries[5].Content, await tools.fetch_entry(entries[5].Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.fetch_entry(entries[3].Id));
    }

    [Fact]
    public async Task Future_and_other_timeline_snapshots_do_not_create_a_boundary()
    {
        await using var db = CreateDb();
        var timeline = Guid.NewGuid();
        var otherTimeline = Guid.NewGuid();
        var first = Entry(db, timeline, 1);
        var current = Entry(db, timeline, 2);
        var future = Entry(db, timeline, 3);
        var other = Entry(db, otherTimeline, 1);
        Snapshot(db, timeline, 1, first, future);
        Snapshot(db, otherTimeline, 1, other);
        await db.SaveChangesAsync();
        var tools = new OptimizationAgentTools(db, timeline, current.Id);

        Assert.Equal([first.Id, current.Id], await tools.get_entry_ids());
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.fetch_entry(other.Id));
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => tools.fetch_entry(Guid.NewGuid()));
        Assert.Contains("review context", missing.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Anchor_must_be_persisted_in_selected_timeline(bool useOtherTimeline)
    {
        await using var db = CreateDb();
        var timeline = Guid.NewGuid();
        var entry = Entry(db, useOtherTimeline ? Guid.NewGuid() : timeline, 1);
        if (useOtherTimeline)
            await db.SaveChangesAsync();
        var tools = new OptimizationAgentTools(db, timeline, entry.Id);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => tools.get_entry_ids());
        Assert.Contains("Current entry", exception.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.fetch_entry(entry.Id));
    }

    [Fact]
    public async Task Malformed_snapshot_with_foreign_members_cannot_change_the_window()
    {
        await using var db = CreateDb();
        var timeline = Guid.NewGuid();
        var first = Entry(db, timeline, 1);
        var current = Entry(db, timeline, 3);
        var foreign = Entry(db, Guid.NewGuid(), 2);
        Snapshot(db, timeline, 1, first, foreign);
        await db.SaveChangesAsync();

        Assert.Equal([first.Id, current.Id], await new OptimizationAgentTools(db, timeline, current.Id).get_entry_ids());
    }

    [Fact]
    public async Task Agent_loads_persisted_anchored_context_in_CSharp_and_exposes_no_history_functions()
    {
        await using var db = CreateDb();
        var timeline = Guid.NewGuid();
        var current = Entry(db, timeline, 1);
        var future = Entry(db, timeline, 2);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var fake = new RecordingAgent();
        var tracker = new FunctionCallTracker();
        var optimizer = new OptimizeEntryAgent(db, timeline, current.Id, fake, tracker);
        Assert.Equal(0, fake.InitCalls);
        const string original = "Persisted-anchor wiring only, not model output.";

        await Assert.ThrowsAsync<NotSupportedException>(() => optimizer.RunAsync(original));

        Assert.Equal(1, fake.InitCalls);
        Assert.Empty(fake.Tools);
        Assert.Equal(["get_entry_ids", "fetch_entry"], tracker.Tools.Select(call => call.Name));
        Assert.Equal([current.Id], Assert.IsAssignableFrom<IEnumerable<Guid>>(tracker.Tools[0].Result));
        Assert.Equal(current.Content, Assert.IsType<string>(tracker.Tools[1].Result));
        Assert.All(tracker.Tools, call => { Assert.Null(call.ID); Assert.True(call.Executed); Assert.Null(call.Exception); });
        using var document = JsonDocument.Parse(fake.Message!);
        Assert.Equal(original, document.RootElement.GetProperty("original_message").GetString());
        var entry = Assert.Single(document.RootElement.GetProperty("review_context").GetProperty("entries").EnumerateArray());
        Assert.Equal(current.Id, entry.GetProperty("entry_id").GetGuid());
        Assert.NotEqual(future.Id, entry.GetProperty("entry_id").GetGuid());
        Assert.Equal(current.Content, entry.GetProperty("content").GetString());
        Assert.Empty(db.ChangeTracker.Entries());
    }

    private static ProjectTimelineDbContext CreateDb() => new(
        new DbContextOptionsBuilder<ProjectTimelineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static TimelineEntry Entry(ProjectTimelineDbContext db, Guid timeline, int number)
    {
        var entry = new TimelineEntry
        {
            TimelineId = timeline, EntryNumber = number, Title = $"Entry {number}", Content = $"Content {number}"
        };
        db.TimelineEntries.Add(entry);
        return entry;
    }

    private static void Snapshot(ProjectTimelineDbContext db, Guid timeline, int number, params TimelineEntry[] entries)
    {
        db.TimelineSnapshots.Add(new TimelineSnapshot
        {
            TimelineId = timeline, SnapshotNumber = number, CapturedAtUtc = DateTime.UtcNow,
            Entries = entries.Select(entry => new TimelineSnapshotEntry { Entry = entry, EntryId = entry.Id }).ToList()
        });
    }

    private sealed class RecordingAgent : ICustomAgent
    {
        public List<AITool> Tools { get; private set; } = [];
        public string Instructions { get; private set; } = "";
        public string? Message { get; private set; }
        public int InitCalls { get; private set; }

        public event EventHandler<FunctionCallEventArgs>? FunctionCalled
        {
            add { }
            remove { }
        }

        public void Init(string agentName, string model, string instructions, List<AITool> tools)
        {
            InitCalls++;
            Tools = tools;
            Instructions = instructions;
        }
        public Task<AgentResponse> RunAsync(string message) => throw new NotSupportedException();
        public Task<AgentResponse<T>> RunAsync<T>(string message) where T : class
        {
            Message = message;
            throw new NotSupportedException();
        }
    }
}
