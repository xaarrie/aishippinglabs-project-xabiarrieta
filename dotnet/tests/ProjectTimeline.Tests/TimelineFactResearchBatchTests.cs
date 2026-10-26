using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.EntityFrameworkCore;
using ProjectTimeline.DataAccessLogic;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents.Tools;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class TimelineFactResearchBatchTests
{
    private static Task<TimelineFactInformationTools> Tools(TimelineFactReviewFixture f, TimelineFactResearchOptions? options = null) =>
        TimelineFactInformationTools.CreateAsync(f.Db, new(f.Project.Id, f.Timeline.Id), f.Actor.Id, false, options);
    private static async Task<JsonElement> Batch(TimelineFactInformationTools tools, string[] queries, int limit = 10) =>
        (JsonElement)(await tools.Functions.Single(x => x.Name == "search_facts_batch").InvokeAsync(
            new AIFunctionArguments { ["queries"] = queries, ["limit"] = limit }))!;

    [Fact]
    public void Default_and_maximum_are_eight_without_relaxing_other_bounds()
    {
        Assert.Equal(8, new TimelineFactResearchOptions().MaxSearchQueries);
        Assert.Equal(8, new TimelineFactResearchOptions(maxSearchQueries: 8).MaxSearchQueries);
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimelineFactResearchOptions(maxSearchQueries: 9));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimelineFactResearchOptions(maxToolCalls: 21));
    }

    [Fact]
    public async Task Groups_cache_normalization_limit_keys_and_exact_aggregate_output()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await f.AcceptAsync("Alice owns delivery.", "alice");
        using var tools = await Tools(f);
        var batch = await Batch(tools, ["Alice", " alice ", "missing"], 1);
        var groups = batch.GetProperty("Results");
        Assert.Equal(3, groups.GetArrayLength());
        Assert.Single(groups[0].GetProperty("Facts").EnumerateArray());
        Assert.Empty(groups[2].GetProperty("Facts").EnumerateArray());
        var same = await Batch(tools, ["ALICE"], 1);
        var other = await Batch(tools, ["alice"], 2);
        var direct = await tools.search_facts("alice");
        var ledger = await tools.ValidateLedgerAsync();
        Assert.Equal(4, ledger.SearchQueries);
        Assert.Equal(4, ledger.ToolCalls);
        Assert.Single(ledger.Facts);
        Assert.Equal(new[] { batch, same, other, direct }.Sum(x => x.GetRawText().Length), ledger.EmittedEvidenceChars);
    }

    [Fact]
    public async Task Ninth_uncached_query_stops_but_cached_repeats_remain_bounded()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        using var tools = await Tools(f);
        for (var i = 0; i < 8; i++) await tools.search_facts("query" + i);
        Assert.Equal("SearchQueryBudgetExhausted", (await Assert.ThrowsAsync<TimelineFactResearchException>(
            () => tools.search_facts("ninth"))).Code);
        await tools.search_facts("query0");
        await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.search_facts("tenth"));
        var ledger = await tools.ValidateLedgerAsync();
        Assert.Equal(8, ledger.SearchQueries);
        Assert.True(ledger.BudgetExhausted);
        Assert.Empty(ledger.Facts);
    }

    [Fact]
    public async Task Failed_attempts_are_not_cached_and_empty_successes_are_cached()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        using var tools = await Tools(f);
        for (var i = 0; i < 2; i++)
            Assert.Equal("InvalidQuery", (await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.search_facts("!?"))).Code);
        await tools.search_facts("missing");
        await tools.search_facts("MISSING");
        Assert.Equal(3, (await tools.ValidateLedgerAsync()).SearchQueries);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("revision")]
    [InlineData("access")]
    [InlineData("fact")]
    public async Task Cached_search_revalidates_current_authority_and_evidence(string change)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await f.AcceptAsync("Alice owns delivery.", "alice");
        using var tools = await Tools(f);
        await Batch(tools, ["alice"]);
        await using (var raw = f.Unguarded())
        {
            if (change == "source") (await raw.Set<TimelineEntry>().SingleAsync()).Content += "changed";
            if (change == "revision") (await raw.Set<Timeline>().SingleAsync()).FactRevision++;
            if (change == "access") (await raw.Set<User>().SingleAsync()).IsActive = false;
            if (change == "fact") (await raw.Set<TimelineFact>().SingleAsync()).Valid = false;
            await raw.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<TimelineFactResearchException>(() => Batch(tools, ["ALICE"]));
        await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.ValidateLedgerAsync());
    }

    [Fact]
    public async Task Aggregate_fact_cap_keeps_only_emitted_IDs_and_effective_limit_is_the_cache_key()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await f.AcceptAsync("Alice owns delivery.", "alice");
        await f.NewSourceAsync();
        var bob = await f.AcceptAsync("Bob owns testing.", "bob");
        using var tools = await Tools(f, new(maxDistinctFacts: 1));
        var result = await Batch(tools, ["alice", "bob"]);
        Assert.Equal("BudgetExhausted", result.GetProperty("Status").GetString());
        Assert.Single((await tools.ValidateLedgerAsync()).Facts);
        await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.fetch_facts([bob.Id]));
        using var limits = await Tools(f, new(maxHitsPerSearch: 1));
        await Batch(limits, ["alice"], 10);
        await Batch(limits, ["alice"], 1);
        Assert.Equal(1, (await limits.ValidateLedgerAsync()).SearchQueries);
    }

    [Fact]
    public async Task Aggregate_output_cap_never_commits_hidden_facts_or_double_charges()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await f.AcceptAsync("Alice owns delivery.", "alice");
        const string query = "alice \"<>&\\";
        using var baseline = await Tools(f);
        var measured = await Batch(baseline, [query]);
        // Batch admission reserves its longest status and false (longer than true) truncation flag.
        var reservedSize = measured.GetRawText().Replace("\"Available\"", "\"BudgetExhausted\"", StringComparison.Ordinal).Length;
        using var exact = await Tools(f, new(maxTotalSerializedToolEvidenceChars: reservedSize));
        var admitted = await Batch(exact, [query]);
        Assert.Single((await exact.ValidateLedgerAsync()).Facts);
        Assert.Equal(measured.GetRawText(), admitted.GetRawText());
        Assert.Equal(admitted.GetRawText().Length, (await exact.ValidateLedgerAsync()).EmittedEvidenceChars);
        var budget = reservedSize - 1;
        using var tools = await Tools(f, new(maxTotalSerializedToolEvidenceChars: budget));
        var result = await Batch(tools, [query, "missing"]);
        var ledger = await tools.ValidateLedgerAsync();
        Assert.Empty(ledger.Facts);
        Assert.True(ledger.BudgetExhausted);
        Assert.Equal(result.GetRawText().Length, ledger.EmittedEvidenceChars);
        Assert.InRange(ledger.EmittedEvidenceChars, 1, budget);
        var id = measured.GetProperty("Results")[0].GetProperty("Facts")[0].GetProperty("Id").GetGuid();
        Assert.Equal("UnknownFactId", (await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.fetch_facts([id]))).Code);
    }

    [Fact]
    public async Task Overlapping_batches_serialize_dedupe_and_charge_one_output_each()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await f.AcceptAsync("Alice owns delivery.", "alice");
        using var tools = await Tools(f);
        var results = await Task.WhenAll(Batch(tools, ["alice", "missing"]), Batch(tools, ["ALICE", "other"]));
        var ledger = await tools.ValidateLedgerAsync();
        Assert.Equal(3, ledger.SearchQueries);
        Assert.Equal(2, ledger.ToolCalls);
        Assert.Equal(results.Sum(x => x.GetRawText().Length), ledger.EmittedEvidenceChars);
    }
}
