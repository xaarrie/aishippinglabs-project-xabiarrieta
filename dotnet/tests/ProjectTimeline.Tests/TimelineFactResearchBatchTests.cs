using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.EntityFrameworkCore;
using ProjectTimeline.DataAccessLogic;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents.AgentModels;
using ProjectTimeline.BusinessLogic.Agents.Tools;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class TimelineFactResearchBatchTests
{
    private static Task<TimelineFactInformationTools> Tools(TimelineFactReviewFixture f, TimelineFactResearchOptions? options = null) =>
        TimelineFactInformationTools.CreateAsync(f.Db, new(f.Project.Id, f.Timeline.Id), f.Actor.Id, false, options);
    private static async Task<JsonElement> Batch(TimelineFactInformationTools tools, string[] queries, int limit = 10) =>
        (JsonElement)(await tools.Functions[0].InvokeAsync(new AIFunctionArguments { ["queries"] = queries, ["limit"] = limit }))!;
    private static async Task<JsonElement> Sources(TimelineFactInformationTools tools, object? ids, string binding = "sdk-json") =>
        binding == "direct" ? await tools.fetch_source_entry((Guid[])ids!) :
        (JsonElement)(await tools.Functions[1].InvokeAsync(new AIFunctionArguments { ["entryIds"] = binding switch
        { "sdk-array" => ids, "sdk-list" => ((Guid[])ids!).ToList(), _ => JsonSerializer.SerializeToElement(ids) } }))!;

    [Fact]
    public async Task Single_pass_exposes_only_two_tools_and_entryIds_source_schema()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        using var tools = await Tools(f);
        Assert.Equal(new[] { "search_facts_batch", "fetch_source_entry" }, tools.Functions.Select(x => x.Name));
        var function = tools.Functions[1];
        var properties = function.JsonSchema.GetProperty("properties");
        Assert.False(properties.TryGetProperty("factIds", out _));
        Assert.Equal("array", properties.GetProperty("entryIds").GetProperty("type").GetString());
        Assert.Equal("string", properties.GetProperty("entryIds").GetProperty("items").GetProperty("type").GetString());
        Assert.Contains(function.JsonSchema.GetProperty("required").EnumerateArray(), x => x.GetString() == "entryIds");
        Assert.Contains("complete distinct entryIds", function.Description);
        var citations = typeof(TimelineFactInformationResult).GetProperty(nameof(TimelineFactInformationResult.CitedContextEvidenceIds))!;
        Assert.Contains("fetch_source_entry(entryIds)", citations.GetCustomAttribute<DescriptionAttribute>()!.Description);
    }

    [Fact]
    public async Task Single_batch_accepts_more_than_twenty_expressions_with_honest_unlimited_metadata()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        using var tools = await Tools(f);
        var result = await Batch(tools, Enumerable.Range(0, 25).Select(i => "missing" + i).ToArray());
        Assert.Equal(25, result.GetProperty("Results").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("RemainingSearchQueries").ValueKind);
        Assert.Equal(25, (await tools.ValidateLedgerAsync()).SearchQueries);
        Assert.Null(tools.Options.MaxSearchQueries);
    }

    [Fact]
    public async Task One_shot_rejects_a_second_search_even_after_an_empty_first_batch()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        using var tools = await Tools(f);
        await Batch(tools, ["missing"]);
        Assert.Equal("InvalidResearchPhase", (await Assert.ThrowsAsync<TimelineFactResearchException>(() => Batch(tools, ["other"]))).Code);
        await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.ValidateLedgerAsync());
    }

    [Fact]
    public async Task Aggregate_fetch_accepts_all_four_emitted_entryIds_once()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            if (i > 0) await f.NewSourceAsync();
            ids.Add((await f.AcceptAsync("Alice owns delivery.", "alice" + i)).EntryId);
        }
        using var tools = await Tools(f);
        var batch = await Batch(tools, ["alice"]);
        Assert.All(batch.GetProperty("Results")[0].GetProperty("Facts").EnumerateArray(), x => Assert.Contains(x.GetProperty("EntryId").GetGuid(), ids));
        var source = await Sources(tools, ids);
        Assert.Equal(4, source.GetProperty("Contexts").GetArrayLength());
        Assert.Empty(source.GetProperty("OmittedEntryIds").EnumerateArray());
        Assert.Equal(4, (await tools.ValidateLedgerAsync()).Contexts.Length);
    }

    [Theory]
    [InlineData("direct", false)]
    [InlineData("sdk-array", false)]
    [InlineData("sdk-list", false)]
    [InlineData("sdk-json", false)]
    [InlineData("direct", true)]
    [InlineData("sdk-array", true)]
    [InlineData("sdk-list", true)]
    [InlineData("sdk-json", true)]
    public async Task Duplicate_sources_normalize_before_limits_and_preserve_first_occurrence_order(string binding, bool multipleEntries)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var service = new TimelineFactService(f.Db);
        var proposal = await service.PrepareAsync(f.Scope, f.Actor.Id, false, 0,
            new([new("Alice owns delivery.", "alice", "Person", "Alice owns delivery."),
                new("Bob owns testing.", "bob", "Person", "Bob owns testing.")], [], []));
        await service.AcceptAsync(f.Scope, proposal.Id, f.Actor.Id, false);
        var first = f.Entry.Id;
        var second = first;
        if (multipleEntries)
        {
            await f.NewSourceAsync();
            second = (await f.AcceptAsync("Alice owns delivery.", "alice-later")).EntryId;
        }
        using var tools = await Tools(f, new(maxDistinctFacts: multipleEntries ? 3 : 2));
        var search = await Batch(tools, ["owns"]);
        var ids = new[] { second, first, second, first }; // Raw count exceeds the lowered cap in both cases.
        var result = await Sources(tools, ids, binding);
        var ledger = await tools.ValidateLedgerAsync();
        var expected = multipleEntries ? new[] { second, first } : new[] { first };
        var wire = result.GetProperty("Contexts").EnumerateArray().ToArray();
        Assert.Equal(expected, wire.Select(x => ledger.Contexts.Single(c => c.EvidenceId == x.GetProperty("EvidenceId").GetGuid()).EntryId));
        Assert.Equal(expected.Order(), ledger.Contexts.Select(x => x.EntryId).Order());
        foreach (var context in ledger.Contexts)
        {
            Assert.Equal(ledger.Facts.Where(x => x.EntryId == context.EntryId).Select(x => x.Id).Order(), context.AnchorFactIds.Order());
            Assert.Equal(context.AnchorFactIds, wire.Single(x => x.GetProperty("EvidenceId").GetGuid() == context.EvidenceId)
                .GetProperty("AnchorFactIds").EnumerateArray().Select(x => x.GetGuid()));
        }
        Assert.Empty(result.GetProperty("OmittedEntryIds").EnumerateArray());
        Assert.False(result.GetProperty("Truncated").GetBoolean());
        Assert.Equal(2, ledger.ToolCalls);
        Assert.Equal(search.GetRawText().Length + result.GetRawText().Length, ledger.EmittedEvidenceChars);
        Assert.Equal(ledger.Contexts.Sum(x => x.Text.Length), ledger.SourceContextChars);
    }

    [Theory]
    [InlineData("direct", "missing")]
    [InlineData("sdk-array", "missing")]
    [InlineData("sdk-list", "missing")]
    [InlineData("sdk-json", "missing")]
    [InlineData("direct", "unknown")]
    [InlineData("sdk-array", "unknown")]
    [InlineData("sdk-list", "unknown")]
    [InlineData("sdk-json", "unknown")]
    [InlineData("direct", "oversized")]
    [InlineData("sdk-array", "oversized")]
    [InlineData("sdk-list", "oversized")]
    [InlineData("sdk-json", "oversized")]
    public async Task Duplicate_sources_do_not_hide_missing_unknown_or_excess_distinct_entries(string binding, string kind)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var first = await f.AcceptAsync("Alice owns delivery.", "alice");
        await f.NewSourceAsync();
        var second = await f.AcceptAsync("Bob owns testing.", "bob");
        using var tools = await Tools(f, new(maxDistinctFacts: 3));
        await Batch(tools, ["owns"]);
        var ids = kind switch
        {
            "missing" => new[] { first.EntryId, first.EntryId, first.EntryId, first.EntryId },
            "unknown" => new[] { first.EntryId, second.EntryId, first.EntryId, Guid.NewGuid() },
            _ => new[] { first.EntryId, second.EntryId, first.EntryId, Guid.NewGuid(), Guid.NewGuid() }
        };
        var error = await Assert.ThrowsAsync<TimelineFactResearchException>(() => Sources(tools, ids, binding));
        Assert.Equal("UnknownOrInvalidEntryIds", error.Code);
        Assert.Equal(error.Code, (await Assert.ThrowsAsync<TimelineFactResearchException>(() =>
            Sources(tools, new[] { first.EntryId, second.EntryId }, binding))).Code);
        Assert.Equal(error.Code, (await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.ValidateLedgerAsync())).Code);
    }

    [Fact]
    public async Task Source_batch_groups_all_same_entry_anchors_and_counts_only_actual_output()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var service = new TimelineFactService(f.Db);
        var proposal = await service.PrepareAsync(f.Scope, f.Actor.Id, false, 0,
            new([new("Alice owns delivery.", "alice", "Person", "Alice owns delivery."),
                new("Bob owns testing.", "bob", "Person", "Bob owns testing.")], [], []));
        await service.AcceptAsync(f.Scope, proposal.Id, f.Actor.Id, false);
        var anchors = await f.Db.TimelineFacts.OrderBy(x => x.EntityKey).ToArrayAsync();
        var firstEntry = f.Entry.Id;
        await f.NewSourceAsync();
        var third = await f.AcceptAsync("Alice owns delivery.", "alice-later");
        using var tools = await Tools(f);
        var search = await Batch(tools, ["owns", "alice"]);
        var batch = await Sources(tools, new[] { firstEntry, third.EntryId });
        var wire = batch.GetProperty("Contexts");
        Assert.Equal(2, wire.GetArrayLength());
        Assert.Equal(anchors.Select(x => x.Id).Order(), wire[0].GetProperty("AnchorFactIds").EnumerateArray().Select(x => x.GetGuid()).Order());
        var ledger = await tools.ValidateLedgerAsync();
        Assert.Equal(firstEntry, ledger.Contexts.Single(x => x.AnchorFactIds.Length == 2).EntryId);
        Assert.Equal(2, ledger.ToolCalls); Assert.Equal(2, ledger.SearchQueries);
        Assert.Equal(search.GetRawText().Length + batch.GetRawText().Length, ledger.EmittedEvidenceChars);
        Assert.Equal(ledger.Contexts.Sum(x => x.Text.Length), ledger.SourceContextChars);
        Assert.All(ledger.Contexts, x => { Assert.NotEqual(Guid.Empty, x.EvidenceId); Assert.Equal(f.Actor.Id, x.AuthorId); });
        Assert.DoesNotContain("SourceFingerprint", batch.GetRawText());
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("factId")]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("unemitted")]
    public async Task Invalid_complete_entry_set_is_fatal_without_a_prefix_or_retry(string kind)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var first = await f.AcceptAsync("Alice owns delivery.", "alice");
        await f.NewSourceAsync();
        var second = await f.AcceptAsync("Bob owns testing.", "bob");
        var foreign = new TimelineEntry { TimelineId = Guid.NewGuid(), Content = "PRIVATE_FOREIGN" };
        using (var raw = f.Unguarded()) { raw.Add(foreign); raw.SaveChanges(); }
        using var tools = await Tools(f);
        await Batch(tools, [kind == "unemitted" ? "alice" : "owns"]);
        var ids = kind == "missing" ? new[] { first.EntryId } : new[] { first.EntryId,
            kind == "factId" ? second.Id : kind == "foreign" ? foreign.Id : kind == "unemitted" ? second.EntryId : Guid.NewGuid() };
        var error = await Assert.ThrowsAsync<TimelineFactResearchException>(() => Sources(tools, ids));
        Assert.Equal("UnknownOrInvalidEntryIds", error.Code);
        Assert.DoesNotContain("PRIVATE", error.Message);
        Assert.Equal(error.Code, (await Assert.ThrowsAsync<TimelineFactResearchException>(() => Sources(tools, new[] { first.EntryId, second.EntryId }))).Code);
        await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.ValidateLedgerAsync());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("oversized")]
    [InlineData("malformed")]
    [InlineData("malformedTail")]
    [InlineData("scalar")]
    public async Task Invalid_source_batches_are_sanitized_and_consume_the_phase(string kind)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        using var tools = await Tools(f, new(maxDistinctFacts: 3));
        await Batch(tools, ["alice"]);
        object? ids = kind switch
        {
            "null" => null, "empty" => Array.Empty<Guid>(),
            "oversized" => Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray(),
            "malformed" => new[] { "PRIVATE_INVALID_GUID" },
            "malformedTail" => new[] { fact.EntryId.ToString(), fact.EntryId.ToString(), "PRIVATE_INVALID_GUID" },
            _ => fact.EntryId.ToString()
        };
        var error = await Assert.ThrowsAsync<TimelineFactResearchException>(() => Sources(tools, ids));
        Assert.Equal(kind is "malformed" or "malformedTail" or "scalar" ? "EvidenceReadFailed" : "UnknownOrInvalidEntryIds", error.Code);
        Assert.DoesNotContain("PRIVATE", error.Message);
        Assert.Equal(error.Code, (await Assert.ThrowsAsync<TimelineFactResearchException>(() => Sources(tools, new[] { fact.EntryId }))).Code);
        await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.ValidateLedgerAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Source_aggregate_exact_boundary_includes_envelope_escaping_and_only_returned_text(int delta)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        f.Entry.Content += string.Concat(Enumerable.Repeat("\"<>&\\\n", 20));
        await f.Db.SaveChangesAsync();
        var first = await f.AcceptAsync("Alice owns delivery.", "alice");
        await f.NewSourceAsync();
        var second = await f.AcceptAsync("Bob owns testing.", "bob");
        var ids = new[] { first.EntryId, second.EntryId, first.EntryId, second.EntryId };
        using var baseline = await Tools(f);
        var searchSize = (await Batch(baseline, ["owns"])).GetRawText().Length;
        var full = await Sources(baseline, ids);
        var budget = searchSize + full.GetRawText().Length + delta;
        using var tools = await Tools(f, new(maxTotalSerializedToolEvidenceChars: budget));
        var search = await Batch(tools, ["owns"]);
        var result = await Sources(tools, ids);
        var contexts = result.GetProperty("Contexts");
        Assert.Equal(2, contexts.GetArrayLength());
        Assert.Equal(delta != 0, contexts[1].GetProperty("Truncated").GetBoolean());
        Assert.Equal(full.GetProperty("Contexts")[0].GetProperty("Text").GetString(), contexts[0].GetProperty("Text").GetString());
        var ledger = await tools.ValidateLedgerAsync();
        Assert.Equal(search.GetRawText().Length + result.GetRawText().Length, ledger.EmittedEvidenceChars);
        Assert.InRange(ledger.EmittedEvidenceChars, 1, budget);
        Assert.Equal(contexts.EnumerateArray().Sum(x => x.GetProperty("Text").GetString()!.Length), ledger.SourceContextChars);
        Assert.Equal(delta != 0, result.GetProperty("Truncated").GetBoolean());
    }

    [Fact]
    public async Task Global_budget_returns_explicit_omissions_without_old_context_text_caps()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        f.Entry.Content += new string('x', 15000);
        await f.Db.SaveChangesAsync();
        var first = await f.AcceptAsync("Alice owns delivery.", "alice");
        await f.NewSourceAsync();
        var second = await f.AcceptAsync("Bob owns testing.", "bob");
        using var full = await Tools(f);
        await Batch(full, ["owns"]);
        var all = await Sources(full, new[] { first.EntryId, second.EntryId });
        Assert.True(all.GetProperty("Contexts")[0].GetProperty("Text").GetString()!.Length > 12000);
        Assert.False(all.GetProperty("Truncated").GetBoolean());
        using var tools = await Tools(f, new(maxTotalSerializedToolEvidenceChars: 1600));
        var batch = await Batch(tools, ["owns"]);
        var entries = batch.GetProperty("Results").EnumerateArray().SelectMany(x => x.GetProperty("Facts").EnumerateArray())
            .Select(x => x.GetProperty("EntryId").GetGuid()).Distinct().ToArray();
        var result = await Sources(tools, entries.Concat(entries).ToArray());
        Assert.Equal("Partial", result.GetProperty("Status").GetString());
        Assert.True(result.GetProperty("Truncated").GetBoolean());
        Assert.NotEmpty(result.GetProperty("OmittedEntryIds").EnumerateArray());
        var ledger = await tools.ValidateLedgerAsync();
        Assert.Equal(entries.Order(), ledger.Contexts.Select(x => x.EntryId)
            .Concat(result.GetProperty("OmittedEntryIds").EnumerateArray().Select(x => x.GetGuid())).Order());
        Assert.Equal(2, ledger.ToolCalls);
        Assert.True(ledger.Truncated); Assert.True(ledger.BudgetExhausted);
        Assert.InRange(ledger.EmittedEvidenceChars, 1, 1600);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("source")]
    [InlineData("fact")]
    [InlineData("access")]
    [InlineData("revision")]
    public async Task Invalid_last_entry_is_checked_even_when_global_budget_would_omit_it(string change)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var first = await f.AcceptAsync("Alice owns delivery.", "alice");
        await f.NewSourceAsync();
        var last = await f.AcceptAsync("Bob owns testing.", "bob");
        using var tools = await Tools(f, new(maxTotalSerializedToolEvidenceChars: 1600));
        await Batch(tools, ["owns"]);
        using var raw = f.Unguarded();
        if (change == "source") raw.Set<TimelineEntry>().Single(x => x.Id == last.EntryId).Content += "PRIVATE_CHANGED";
        if (change == "fact") raw.Set<TimelineFact>().Single(x => x.Id == last.Id).Valid = false;
        if (change == "access") raw.Set<User>().Single().IsActive = false;
        if (change == "revision") raw.Set<Timeline>().Single().FactRevision++;
        raw.SaveChanges();
        var error = await Assert.ThrowsAsync<TimelineFactResearchException>(() =>
            tools.fetch_source_entry([first.EntryId, change == "unknown" ? Guid.NewGuid() : last.EntryId]));
        Assert.Equal(change switch { "unknown" => "UnknownOrInvalidEntryIds", "source" => "SourceContextUnavailable",
            "fact" => "EvidenceChanged", "revision" => "FactRevisionChanged", _ => "AccessDenied" }, error.Code);
        Assert.DoesNotContain("PRIVATE", error.Message);
        await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.ValidateLedgerAsync());
    }

    [Fact]
    public async Task Groups_cache_normalization_within_batch_and_exact_aggregate_output()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        using var tools = await Tools(f);
        var batch = await Batch(tools, ["Alice", " alice ", "missing"], 1);
        var groups = batch.GetProperty("Results");
        Assert.Equal(3, groups.GetArrayLength());
        Assert.Single(groups[0].GetProperty("Facts").EnumerateArray());
        Assert.Empty(groups[2].GetProperty("Facts").EnumerateArray());
        Assert.Equal(groups[0].GetProperty("Facts").GetRawText(), groups[1].GetProperty("Facts").GetRawText());
        var source = await Sources(tools, new[] { fact.EntryId });
        var ledger = await tools.ValidateLedgerAsync();
        Assert.Equal(2, ledger.SearchQueries); Assert.Equal(2, ledger.ToolCalls); Assert.Single(ledger.Facts);
        Assert.Equal(batch.GetRawText().Length + source.GetRawText().Length, ledger.EmittedEvidenceChars);
    }

    [Fact]
    public async Task Aggregate_fact_cap_keeps_only_emitted_IDs_and_allows_required_fetch()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await f.AcceptAsync("Alice owns delivery.", "alice");
        await f.NewSourceAsync();
        var bob = await f.AcceptAsync("Bob owns testing.", "bob");
        using var tools = await Tools(f, new(maxDistinctFacts: 1));
        var result = await Batch(tools, ["alice", "bob"]);
        Assert.Equal("Partial", result.GetProperty("Status").GetString());
        var hit = result.GetProperty("Results")[0].GetProperty("Facts")[0];
        await Sources(tools, new[] { hit.GetProperty("EntryId").GetGuid() });
        var ledger = await tools.ValidateLedgerAsync();
        Assert.Single(ledger.Facts); Assert.DoesNotContain(ledger.Facts, x => x.Id == bob.Id);
        Assert.Equal(2, ledger.SearchQueries);
        Assert.True(ledger.BudgetExhausted);
    }

    [Fact]
    public async Task Per_expression_validation_cannot_be_skipped_by_earlier_resource_clipping()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await f.AcceptAsync("Alice owns delivery.", "alice");
        await f.NewSourceAsync();
        await f.AcceptAsync("Bob owns testing.", "bob");
        using var tools = await Tools(f, new(maxDistinctFacts: 1));
        Assert.Equal("InvalidQuery", (await Assert.ThrowsAsync<TimelineFactResearchException>(() =>
            Batch(tools, ["alice", "bob", new string('x', 301)]))).Code);
        await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.ValidateLedgerAsync());
    }

    [Fact]
    public async Task Output_clipping_never_commits_hidden_facts_and_reserves_fetch_omissions()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await f.AcceptAsync("Alice owns delivery.", "alice");
        using var tools = await Tools(f, new(maxTotalSerializedToolEvidenceChars: 400));
        var result = await Batch(tools, ["alice \"<>&\\", "missing"]);
        var ledger = await tools.ValidateLedgerAsync();
        Assert.Empty(ledger.Facts); Assert.True(ledger.BudgetExhausted);
        Assert.Equal(1, ledger.SearchQueries); // Unexecuted expressions do not inflate query accounting.
        Assert.True(result.GetProperty("Truncated").GetBoolean());
        Assert.Equal(result.GetRawText().Length, ledger.EmittedEvidenceChars);
        Assert.InRange(ledger.EmittedEvidenceChars, 1, 400);
    }
}
