using ProjectTimeline.Tests.AgentTests.Support;
using Xunit;

namespace ProjectTimeline.Tests.AgentTests;

public sealed class FakeConversationRepositoryTests
{
    [Theory]
    [InlineData("software-functionality")]
    [InlineData("software-ambiguous-control")]
    [InlineData("travel-detailed-conditions")]
    [InlineData("business-partial-answers")]
    public async Task Seeded_window_is_chronological_inclusive_and_rejects_boundary_future_foreign_and_missing_entries(string fixtureId)
    {
        var fixture = ConversationFixtures.Get(fixtureId);
        var repository = fixture.CreateRepository();
        var tools = repository.SelectAnchor(fixture.TimelineId, fixture.CurrentEntryId);
        var allowed = fixture.Entries.Where(entry => entry.TimelineId == fixture.TimelineId && entry.Sequence is 20 or 30)
            .OrderBy(entry => entry.Sequence).ToArray();

        Assert.Equal(allowed.Select(entry => entry.Id), await tools.get_entry_ids());
        foreach (var entry in allowed)
            Assert.Equal(entry.Content, await tools.fetch_entry(entry.Id));
        foreach (var excluded in fixture.Entries.Except(allowed).Select(entry => entry.Id).Append(Guid.NewGuid()))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => tools.fetch_entry(excluded));
            Assert.Contains("review context", error.Message);
        }
        Assert.Equal(repository.OriginalState, repository.CaptureState());
    }

    [Fact]
    public async Task Anchor_selection_is_per_adapter_and_can_only_select_persisted_entries_in_its_timeline()
    {
        var fixture = ConversationFixtures.Get("software-functionality");
        var repository = fixture.CreateRepository();
        var history = fixture.Entries.Single(entry => entry.Sequence == 20);
        var foreign = fixture.Entries.Single(entry => entry.TimelineId != fixture.TimelineId);
        var currentTools = repository.SelectAnchor(fixture.TimelineId, fixture.CurrentEntryId);
        var earlierTools = repository.SelectAnchor(fixture.TimelineId, history.Id);
        var foreignTools = repository.SelectAnchor(foreign.TimelineId, foreign.Id);

        Assert.Equal([history.Id], await earlierTools.get_entry_ids());
        Assert.Equal([history.Id, fixture.CurrentEntryId], await currentTools.get_entry_ids());
        Assert.Equal([foreign.Id], await foreignTools.get_entry_ids());
        await Assert.ThrowsAsync<InvalidOperationException>(() => earlierTools.fetch_entry(fixture.CurrentEntryId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => currentTools.fetch_entry(foreign.Id));
        foreach (var invalidId in new[] { foreign.Id, Guid.NewGuid() })
        {
            var invalidTools = repository.SelectAnchor(fixture.TimelineId, invalidId);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => invalidTools.get_entry_ids());
            Assert.Contains("Current entry", error.Message);
            await Assert.ThrowsAsync<InvalidOperationException>(() => invalidTools.fetch_entry(history.Id));
        }
        Assert.Equal(repository.OriginalState, repository.CaptureState());
    }

    [Fact]
    public async Task No_snapshot_exposes_only_same_timeline_history_through_the_anchor()
    {
        var fixture = ConversationFixtures.Get("travel-detailed-conditions");
        var repository = new FakeConversationRepository(fixture.Entries);
        var tools = repository.SelectAnchor(fixture.TimelineId, fixture.CurrentEntryId);
        var expected = fixture.Entries.Where(entry => entry.TimelineId == fixture.TimelineId && entry.Sequence <= 30)
            .OrderBy(entry => entry.Sequence);

        Assert.Equal(expected.Select(entry => entry.Id), await tools.get_entry_ids());
        Assert.Equal(repository.OriginalState, repository.CaptureState());
    }

    [Fact]
    public async Task Boundary_uses_largest_strictly_preceding_snapshot_max_and_ignores_invalid_snapshots()
    {
        var timeline = Guid.NewGuid();
        var entries = Enumerable.Range(1, 9).Select(sequence => new PersistedConversationEntry(
            Guid.NewGuid(), timeline, sequence, $"Planning {sequence}", $"Synthetic content {sequence}")).ToArray();
        var foreign = new PersistedConversationEntry(Guid.NewGuid(), Guid.NewGuid(), 4, "Foreign", "Not permitted");
        ConversationSnapshot Snapshot(params int[] numbers) => new(timeline, numbers.Select(number => entries[number - 1].Id).ToArray());
        var repository = new FakeConversationRepository(entries.Append(foreign), [
            Snapshot(1, 2), Snapshot(1, 5), // Noncontiguous membership still creates a boundary at 5.
            Snapshot(3, 8), Snapshot(1, 7), // Future/current membership invalidates the whole snapshot.
            Snapshot(), new(timeline, [entries[5].Id, foreign.Id]),
            new(timeline, [entries[5].Id, Guid.NewGuid()]), new(foreign.TimelineId, [foreign.Id])
        ]);
        var tools = repository.SelectAnchor(timeline, entries[6].Id);

        Assert.Equal([entries[5].Id, entries[6].Id], await tools.get_entry_ids());
        foreach (var outsideBoundary in entries.Take(5))
            await Assert.ThrowsAsync<InvalidOperationException>(() => tools.fetch_entry(outsideBoundary.Id));
        Assert.Equal(entries[5].Content, await tools.fetch_entry(entries[5].Id));
        Assert.Equal(repository.OriginalState, repository.CaptureState());
    }

    [Fact]
    public async Task Caller_owned_seeds_and_returned_ID_lists_cannot_mutate_persisted_state()
    {
        var fixture = ConversationFixtures.Get("business-partial-answers");
        var entries = fixture.Entries.ToArray();
        var members = fixture.Snapshots[0].EntryIds.ToArray();
        var snapshots = new[] { new ConversationSnapshot(fixture.TimelineId, members) };
        var repository = new FakeConversationRepository(entries, snapshots);
        var tools = repository.SelectAnchor(fixture.TimelineId, fixture.CurrentEntryId);
        var expected = await tools.get_entry_ids();
        entries[0] = entries[0] with { Content = "Caller changed its own array" };
        members[0] = fixture.CurrentEntryId;
        snapshots[0] = new(fixture.TimelineId, []);
        var returned = await tools.get_entry_ids();
        returned.Clear();
        returned.Add(Guid.NewGuid());

        Assert.Equal(expected, await tools.get_entry_ids());
        Assert.Equal(fixture.Current.Content, await tools.fetch_entry(fixture.CurrentEntryId));
        Assert.Equal(repository.OriginalState, repository.CaptureState());
        Assert.True(((ICollection<PersistedConversationEntry>)repository.Entries).IsReadOnly);
        Assert.True(((ICollection<Guid>)repository.Snapshots[0].EntryIds).IsReadOnly);
    }

    [Fact]
    public async Task List_and_per_entry_failures_are_independently_configurable_without_changing_source_state()
    {
        var fixture = ConversationFixtures.Get("business-partial-answers");
        var repository = fixture.CreateRepository();
        var tools = repository.SelectAnchor(fixture.TimelineId, fixture.CurrentEntryId);
        var ids = await tools.get_entry_ids();
        var listFailure = new InvalidOperationException("Synthetic ID-list failure");
        var fetchFailure = new InvalidOperationException("Synthetic later-entry failure");
        repository.IdListFailure = listFailure;
        Assert.Same(listFailure, await Assert.ThrowsAsync<InvalidOperationException>(() => tools.get_entry_ids()));
        repository.IdListFailure = null;
        Assert.Equal(ids, await tools.get_entry_ids());
        repository.EntryFailures[ids[1]] = fetchFailure;
        Assert.Equal(fixture.Entries.Single(entry => entry.Id == ids[0]).Content, await tools.fetch_entry(ids[0]));
        Assert.Same(fetchFailure, await Assert.ThrowsAsync<InvalidOperationException>(() => tools.fetch_entry(ids[1])));
        // Failure settings belong to one repository, not a static/domain-specific fake.
        var independent = fixture.CreateRepository();
        Assert.Equal(fixture.Current.Content, await independent.SelectAnchor(fixture.TimelineId, fixture.CurrentEntryId).fetch_entry(ids[1]));
        repository.EntryFailures.Clear();
        Assert.Equal(fixture.Current.Content, await tools.fetch_entry(ids[1]));
        Assert.Equal(repository.OriginalState, repository.CaptureState());
        Assert.Equal(independent.OriginalState, independent.CaptureState());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Invalid_seed_identity_or_per_timeline_sequence_is_rejected(bool duplicateIdentity)
    {
        var entry = new PersistedConversationEntry(Guid.NewGuid(), Guid.NewGuid(), 1, "Title", "Synthetic source");
        var duplicate = duplicateIdentity ? entry with { Sequence = 2 } : entry with { Id = Guid.NewGuid() };
        Assert.Throws<ArgumentException>(() => new FakeConversationRepository([entry, duplicate]));
    }
}
