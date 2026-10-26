using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.AI;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents.Tools;
using ProjectTimeline.DataAccessLogic;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class TimelineFactResearchToolsTests
{
    private static Task<TimelineFactInformationTools> Tools(TimelineFactReviewFixture f,
        TimelineFactResearchOptions? options = null, CancellationToken ct = default) =>
        TimelineFactInformationTools.CreateAsync(f.Db, new(f.Project.Id, f.Timeline.Id), f.Actor.Id, false, options, ct);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tool_descriptions_advertise_actual_batch_fetch_hit_and_context_bounds(bool reduced)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var limits = reduced ? new TimelineFactResearchOptions(maxDistinctFacts: 7, maxHitsPerSearch: 4,
            maxSourceContexts: 1, maxCharsPerContext: 123, maxTotalSourceContextChars: 456) : new();
        using var tools = await Tools(f, limits);
        Assert.Contains($"at most {limits.MaxHitsPerSearch} hits", tools.Functions.Single(x => x.Name == "search_facts").Description);
        var batch = tools.Functions.Single(x => x.Name == "search_facts_batch").Description;
        Assert.Contains("At most 8 queries", batch); // Per-batch bound is separate from the uncached-search allowance.
        Assert.Contains($"at most {limits.MaxHitsPerSearch} hits per query", batch);
        Assert.Contains($"up to {limits.MaxDistinctFacts} distinct factIds", tools.Functions.Single(x => x.Name == "fetch_facts").Description);
        var context = tools.Functions.Single(x => x.Name == "fetch_source_entry").Description;
        Assert.Contains($"at most {limits.MaxSourceContexts} source contexts", context);
        Assert.Contains($"{limits.MaxCharsPerContext} characters per context", context);
        Assert.Contains($"{limits.MaxTotalSourceContextChars} context characters total", context);
    }

    [Fact]
    public async Task Real_tools_support_multiple_queries_details_and_deduplicated_immutable_ledger()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var alice = await f.AcceptAsync("Alice owns delivery.", "alice");
        await f.NewSourceAsync();
        var bob = await f.AcceptAsync("Bob owns testing.", "bob");
        using var tools = await Tools(f);
        Assert.Equal(new[] { "search_facts", "fetch_facts", "fetch_source_entry", "search_facts_batch" }, tools.Functions.Select(x => x.Name));
        var search = tools.Functions[0];
        var empty = (JsonElement)(await search.InvokeAsync(new AIFunctionArguments { ["query"] = "unmatched" }))!;
        Assert.Equal(0, empty.GetProperty("Facts").GetArrayLength());
        await search.InvokeAsync(new AIFunctionArguments { ["query"] = "alice" });
        var snapshot = await tools.ValidateLedgerAsync();
        await search.InvokeAsync(new AIFunctionArguments { ["query"] = "bob" });
        await search.InvokeAsync(new AIFunctionArguments { ["query"] = "alice bob" });
        var detail = (JsonElement)(await tools.Functions[1].InvokeAsync(new AIFunctionArguments { ["factIds"] = new[] { alice.Id } }))!;
        Assert.Equal(alice.Id, detail.GetProperty("Facts")[0].GetProperty("Id").GetGuid());
        Assert.Single(snapshot.Facts);
        var final = await tools.ValidateLedgerAsync();
        Assert.Equal(2, final.Facts.Length);
        Assert.Contains(final.Facts, x => x.Id == bob.Id);
        Assert.Equal(5, final.ToolCalls);
        Assert.Equal(4, final.SearchQueries);
        await tools.search_facts("ALICE");
        Assert.Equal(4, (await tools.ValidateLedgerAsync()).SearchQueries);
    }

    [Theory]
    [InlineData("search_facts")]
    [InlineData("fetch_facts")]
    [InlineData("fetch_source_entry")]
    [InlineData("search_facts_batch")]
    public async Task Model_wire_omits_server_provenance_but_ledger_retains_original_records(string tool)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        using var tools = await Tools(f);
        if (tool is "fetch_facts" or "fetch_source_entry") await tools.search_facts("alice");
        var args = tool switch
        {
            "fetch_facts" => new AIFunctionArguments { ["factIds"] = new[] { fact.Id } },
            "fetch_source_entry" => new AIFunctionArguments { ["factId"] = fact.Id },
            "search_facts_batch" => new AIFunctionArguments { ["queries"] = new[] { "alice" } },
            _ => new AIFunctionArguments { ["query"] = "alice" }
        };
        var payload = (JsonElement)(await tools.Functions.Single(x => x.Name == tool).InvokeAsync(args))!;
        var json = payload.GetRawText();
        foreach (var field in new[] { "SourceFingerprint", "EntryId", "AuthorId", "CreationReviewId", "ExtractionJobId" })
            Assert.DoesNotContain(field, json);
        Assert.DoesNotContain(fact.SourceFingerprint, json);
        var ledger = await tools.ValidateLedgerAsync();
        var original = Assert.Single(ledger.Facts);
        Assert.Equal(fact.Id, original.Id);
        Assert.Equal(fact.EntryId, original.EntryId);
        Assert.Equal(fact.SourceFingerprint, original.SourceFingerprint);
        Assert.NotNull(fact.CreationReviewId);
        Assert.Equal(fact.CreationReviewId, original.CreationReviewId);
        Assert.Equal(fact.ExtractionJobId, original.ExtractionJobId);
        if (tool == "fetch_source_entry")
        {
            var wire = Assert.Single(payload.GetProperty("Contexts").EnumerateArray());
            var context = Assert.Single(ledger.Contexts);
            Assert.Equal(new[] { "EvidenceId", "EntryNumber", "AnchorFactIds", "Text", "Truncated", "EvidenceKind" },
                wire.EnumerateObject().Select(x => x.Name));
            Assert.Equal(context.EvidenceId, wire.GetProperty("EvidenceId").GetGuid());
            Assert.Equal(f.Entry.EntryNumber, wire.GetProperty("EntryNumber").GetInt32());
            Assert.Equal(fact.Id, wire.GetProperty("AnchorFactIds")[0].GetGuid());
            Assert.Equal(context.Text, wire.GetProperty("Text").GetString());
            Assert.Equal(context.Truncated, wire.GetProperty("Truncated").GetBoolean());
            Assert.Equal(context.EvidenceKind, wire.GetProperty("EvidenceKind").GetString());
            Assert.Equal(f.Entry.Id, context.EntryId);
            Assert.Equal(f.Actor.Id, context.AuthorId);
            Assert.Equal(fact.SourceFingerprint, context.SourceFingerprint);
        }
        else
        {
            var group = tool == "search_facts_batch" ? payload.GetProperty("Results")[0] : payload;
            var wire = Assert.Single(group.GetProperty("Facts").EnumerateArray());
            Assert.Equal(new[] { "Id", "EntryNumber", "Claim", "EntityKey", "EntityType", "SourceQuote", "SourceKind", "ClarificationEvidence" },
                wire.EnumerateObject().Select(x => x.Name));
            Assert.Equal(fact.Id, wire.GetProperty("Id").GetGuid());
            Assert.Equal(original.EntryNumber, wire.GetProperty("EntryNumber").GetInt32());
            Assert.Equal(original.Claim, wire.GetProperty("Claim").GetString());
            Assert.Equal(original.EntityKey, wire.GetProperty("EntityKey").GetString());
            Assert.Equal(original.EntityType, wire.GetProperty("EntityType").GetString());
            Assert.Equal(original.SourceQuote, wire.GetProperty("SourceQuote").GetString());
            Assert.Equal(original.SourceKind, wire.GetProperty("SourceKind").GetString());
            Assert.Equal(original.ClarificationEvidence, wire.GetProperty("ClarificationEvidence").GetString());
        }
    }

    [Theory]
    [InlineData("fetch_source_entry")]
    [InlineData("fetch_facts")]
    public async Task SDK_unknown_ID_result_contains_no_unemitted_data_and_debits_output_and_call_budgets(string tool)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        using var tools = await Tools(f);
        var function = tools.Functions.Single(x => x.Name == tool);
        var args = tool == "fetch_facts" ? new AIFunctionArguments { ["factIds"] = new[] { fact.Id } }
            : new AIFunctionArguments { ["factId"] = fact.Id };
        var error = (JsonElement)(await function.InvokeAsync(args))!;
        Assert.Equal("RecoverableError", error.GetProperty("Status").GetString());
        Assert.DoesNotContain(fact.Id.ToString(), error.GetRawText());
        Assert.DoesNotContain(f.Entry.Id.ToString(), error.GetRawText());
        Assert.DoesNotContain("Alice", error.GetRawText());
        Assert.DoesNotContain("SourceFingerprint", error.GetRawText());
        var search = await tools.search_facts("alice");
        var fetched = (JsonElement)(await function.InvokeAsync(args))!;
        var ledger = await tools.ValidateLedgerAsync();
        Assert.Equal(3, ledger.ToolCalls);
        Assert.Equal(error.GetRawText().Length + search.GetRawText().Length + fetched.GetRawText().Length, ledger.EmittedEvidenceChars);
        Assert.Single(ledger.Facts);
        using var limited = await Tools(f, new(maxToolCalls: 1));
        await limited.Functions.Single(x => x.Name == tool).InvokeAsync(args);
        Assert.Equal("ToolCallBudgetExhausted", (await Assert.ThrowsAsync<TimelineFactResearchException>(() => limited.search_facts("alice"))).Code);
        Assert.Equal("UnknownFactIdNotCorrected", (await Assert.ThrowsAsync<TimelineFactResearchException>(() => limited.ValidateLedgerAsync())).Code);
        using var tiny = await Tools(f, new(maxTotalSerializedToolEvidenceChars: 100));
        Assert.Equal("EvidenceBudgetExhausted", (await Assert.ThrowsAsync<TimelineFactResearchException>(async () =>
            await tiny.Functions.Single(x => x.Name == tool).InvokeAsync(args))).Code);
        await Assert.ThrowsAsync<TimelineFactResearchException>(() => tiny.ValidateLedgerAsync());
    }

    [Fact]
    public async Task Concurrent_unknown_IDs_share_recovery_allowance_and_exact_output_accounting()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        using var tools = await Tools(f);
        var search = await tools.search_facts("alice");
        var errors = await Task.WhenAll(
            Task.Run(async () => (JsonElement)(await tools.Functions[1].InvokeAsync(
                new AIFunctionArguments { ["factIds"] = new[] { Guid.NewGuid() } }))!),
            Task.Run(async () => (JsonElement)(await tools.Functions[2].InvokeAsync(
                new AIFunctionArguments { ["factId"] = Guid.NewGuid() }))!));
        Assert.All(errors, error => Assert.Equal("RecoverableError", error.GetProperty("Status").GetString()));
        Assert.Equal("UnknownFactIdNotCorrected", (await Assert.ThrowsAsync<TimelineFactResearchException>(
            () => tools.ValidateLedgerAsync())).Code);
        var detail = (JsonElement)(await tools.Functions[1].InvokeAsync(
            new AIFunctionArguments { ["factIds"] = new[] { fact.Id } }))!;
        var ledger = await tools.ValidateLedgerAsync();
        Assert.Equal(4, ledger.ToolCalls);
        Assert.Equal(1, ledger.SearchQueries);
        Assert.Equal(search.GetRawText().Length + detail.GetRawText().Length + errors.Sum(x => x.GetRawText().Length), ledger.EmittedEvidenceChars);
        Assert.Equal(fact.Id, Assert.Single(ledger.Facts).Id);
        Assert.Empty(ledger.Contexts);
        Assert.False(ledger.BudgetExhausted);
        // A successful correction does not reset the operation-wide allowance.
        Assert.Equal("UnknownFactRecoveryExhausted", (await Assert.ThrowsAsync<TimelineFactResearchException>(async () =>
            await tools.Functions[2].InvokeAsync(new AIFunctionArguments { ["factId"] = Guid.NewGuid() }))).Code);
        Assert.Equal("UnknownFactRecoveryExhausted", (await Assert.ThrowsAsync<TimelineFactResearchException>(
            () => tools.ValidateLedgerAsync())).Code);
    }

    [Fact]
    public async Task Ranking_prefers_matched_terms_over_entry_recency_and_translates_to_bounded_SQL()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var older = await f.AcceptAsync("Alice owns delivery.", "alice-delivery");
        await f.NewSourceAsync();
        await f.AcceptAsync("Bob owns testing.", "bob");
        using var tools = await Tools(f);
        var payload = await tools.search_facts("alice delivery testing");
        Assert.Equal(older.Id, payload.GetProperty("Facts")[0].GetProperty("Id").GetGuid());
        using var db = new ProjectTimelineDbContext(new DbContextOptionsBuilder<ProjectTimelineDbContext>()
            .UseNpgsql("Host=localhost;Database=offline_translation_only", o => o.UseVector()).Options);
        var search = new TimelineFactInformationSearch(db, new(f.Project.Id, f.Timeline.Id), f.Actor.Id, false);
        var query = (IQueryable<TimelineFactSearchHit>)typeof(TimelineFactInformationSearch)
            .GetMethod("ResearchQuery", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(search, [new[] { "alice", "delivery" }, 10])!;
        var sql = query.ToQueryString();
        Assert.Contains("@@", sql); Assert.Contains("ts_rank(", sql);
        Assert.Contains("LIMIT", sql); Assert.Contains("ORDER BY", sql);
        Assert.DoesNotContain("\"Content\"", sql); Assert.DoesNotContain("Embedding", sql);
    }

    [Fact]
    public async Task Context_requires_returned_active_anchor_and_has_separate_server_identity()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        using var tools = await Tools(f);
        await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.fetch_source_entry(fact.Id));
        await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.fetch_facts([fact.Id]));
        await tools.search_facts("alice");
        var first = (JsonElement)(await tools.Functions[2].InvokeAsync(new AIFunctionArguments { ["factId"] = fact.Id }))!;
        var context = first.GetProperty("Contexts")[0];
        Assert.NotEqual(fact.Id, context.GetProperty("EvidenceId").GetGuid());
        var original = Assert.Single((await tools.ValidateLedgerAsync()).Contexts);
        Assert.Equal(f.Entry.Id, original.EntryId);
        Assert.Equal(TimelineFactEvidence.Fingerprint(f.Entry), original.SourceFingerprint);
        Assert.Equal("SeparateSourceEntryContextNotFactProof", context.GetProperty("EvidenceKind").GetString());
        var second = await tools.fetch_source_entry(fact.Id);
        Assert.Equal(context.GetProperty("EvidenceId").GetGuid(), second.GetProperty("Contexts")[0].GetProperty("EvidenceId").GetGuid());
        Assert.Single((await tools.ValidateLedgerAsync()).Contexts);
    }

    [Theory]
    [InlineData("entry")]
    [InlineData("invalid")]
    [InlineData("pending")]
    [InlineData("revision")]
    [InlineData("revoked")]
    public async Task Current_mutations_prevent_context_and_final_evidence_release(string mutation)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        using var tools = await Tools(f);
        await tools.search_facts("alice");
        await tools.fetch_source_entry(fact.Id);
        await using (var raw = f.Unguarded())
        {
            if (mutation == "entry") (await raw.Set<TimelineEntry>().SingleAsync()).Content = "Changed current body";
            if (mutation == "invalid") (await raw.Set<TimelineFact>().SingleAsync()).Valid = false;
            if (mutation == "pending") (await raw.Set<TimelineFactProposal>().SingleAsync()).Status = "Pending";
            if (mutation == "revision") (await raw.Set<Timeline>().SingleAsync()).FactRevision++;
            if (mutation == "revoked") (await raw.Set<User>().SingleAsync()).IsActive = false;
            await raw.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.fetch_source_entry(fact.Id));
        await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.fetch_facts([fact.Id]));
        await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.ValidateLedgerAsync());
    }

    [Fact]
    public async Task Mutation_before_first_context_read_withholds_current_text_and_final_fact_ledger()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        using var tools = await Tools(f);
        await tools.search_facts("alice");
        await using (var raw = f.Unguarded())
        {
            (await raw.Set<TimelineEntry>().SingleAsync()).Content = "PRIVATE_CHANGED_BODY";
            await raw.SaveChangesAsync();
        }
        var error = await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.fetch_source_entry(fact.Id));
        Assert.Equal("SourceContextUnavailable", error.Code);
        Assert.DoesNotContain("PRIVATE_CHANGED_BODY", error.Message);
        await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.ValidateLedgerAsync());
    }

    [Fact]
    public async Task Character_caps_include_serialized_envelope_and_context_clipping_is_explicit()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        using var tools = await Tools(f, new(maxCharsPerContext: 12, maxTotalSourceContextChars: 12));
        var search = await tools.search_facts("alice");
        var source = await tools.fetch_source_entry(fact.Id);
        var context = source.GetProperty("Contexts")[0];
        Assert.True(context.GetProperty("Truncated").GetBoolean());
        Assert.Equal(12, context.GetProperty("Text").GetString()!.Length);
        var ledger = await tools.ValidateLedgerAsync();
        Assert.Equal(search.GetRawText().Length + source.GetRawText().Length, ledger.EmittedEvidenceChars);
        using var limited = await Tools(f, new(maxTotalSerializedToolEvidenceChars: search.GetRawText().Length - 1));
        Assert.Equal("EvidenceBudgetExhausted", (await Assert.ThrowsAsync<TimelineFactResearchException>(() => limited.search_facts("alice"))).Code);
        Assert.Empty((await limited.ValidateLedgerAsync()).Facts);
    }

    [Fact]
    public async Task Clarification_quote_is_review_answer_evidence_not_raw_entry_proof()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var service = new TimelineFactService(f.Db);
        var revision = await f.Db.Timelines.Select(x => x.FactRevision).SingleAsync();
        var proposal = await service.PrepareAsync(f.Scope, f.Actor.Id, false, revision,
            new([new("Carol approved", "carol", "Person", "Carol approved", "Clarification")], [], [new("Who approved?", "Carol approved")]));
        await service.AcceptAsync(f.Scope, proposal.Id, f.Actor.Id, false);
        using var tools = await Tools(f);
        var result = await tools.search_facts("carol");
        var fact = result.GetProperty("Facts")[0];
        Assert.Equal("Clarification", fact.GetProperty("SourceKind").GetString());
        Assert.Contains("Carol approved", fact.GetProperty("ClarificationEvidence").GetString());
        var context = (await tools.fetch_source_entry(fact.GetProperty("Id").GetGuid())).GetProperty("Contexts")[0];
        Assert.DoesNotContain("Carol approved", context.GetProperty("Text").GetString());
        Assert.Equal("SeparateSourceEntryContextNotFactProof", context.GetProperty("EvidenceKind").GetString());
        Assert.Single((await tools.ValidateLedgerAsync()).Contexts);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("pending")]
    [InlineData("foreignSource")]
    [InlineData("foreignReview")]
    public async Task Ineligible_or_foreign_facts_never_enter_the_ledger(string change)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        await using (var raw = f.Unguarded())
        {
            var current = await raw.Set<TimelineFact>().SingleAsync();
            var review = await raw.Set<TimelineFactProposal>().SingleAsync();
            var foreignProject = new Project { Code = "FOREIGN", Name = "Other", OwnerId = f.Actor.Id };
            var foreignTimeline = new Timeline { Code = "FOREIGN", Name = "Other", ProjectId = foreignProject.Id };
            var foreignEntry = new TimelineEntry { TimelineId = foreignTimeline.Id, AuthorId = f.Actor.Id, Content = "PRIVATE_SENTINEL" };
            raw.AddRange(foreignProject, foreignTimeline, foreignEntry);
            if (change == "invalid") current.Valid = false;
            if (change == "pending") review.Status = "Pending";
            if (change == "foreignSource") current.EntryId = foreignEntry.Id;
            if (change == "foreignReview") review.EntryId = foreignEntry.Id;
            await raw.SaveChangesAsync();
        }
        using var tools = await Tools(f);
        Assert.Empty((await tools.search_facts("alice")).GetProperty("Facts").EnumerateArray());
        var error = await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.fetch_source_entry(fact.Id));
        Assert.Equal("UnknownFactId", error.Code);
        Assert.DoesNotContain("PRIVATE_SENTINEL", error.Message);
        Assert.Empty((await tools.ValidateLedgerAsync()).Facts);
    }

    [Fact]
    public async Task Fact_and_hit_caps_retain_only_emitted_evidence_and_mark_exhaustion()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await f.AcceptAsync("Alice owns delivery.", "alice");
        await f.NewSourceAsync();
        await f.AcceptAsync("Bob owns testing.", "bob");
        using var hits = await Tools(f, new(maxHitsPerSearch: 1));
        var payload = await hits.search_facts("owns");
        Assert.True(payload.GetProperty("Truncated").GetBoolean());
        Assert.Single((await hits.ValidateLedgerAsync()).Facts);
        using var distinct = await Tools(f, new(maxDistinctFacts: 1));
        var partial = await distinct.search_facts("owns");
        Assert.Equal("BudgetExhausted", partial.GetProperty("Status").GetString());
        Assert.Single(partial.GetProperty("Facts").EnumerateArray());
        var ledger = await distinct.ValidateLedgerAsync();
        Assert.Single(ledger.Facts); Assert.True(ledger.BudgetExhausted);
        Assert.Equal("FactBudgetExhausted", (await Assert.ThrowsAsync<TimelineFactResearchException>(() => distinct.search_facts("alice"))).Code);
        Assert.Single((await distinct.ValidateLedgerAsync()).Facts);
    }

    [Fact]
    public async Task Total_serialized_budget_accepts_exact_boundary_and_counts_repeated_envelopes()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await f.AcceptAsync("Alice owns delivery.", "alice");
        using var baseline = await Tools(f);
        var size = (await baseline.search_facts("alice")).GetRawText().Length;
        using var exact = await Tools(f, new(maxTotalSerializedToolEvidenceChars: size));
        Assert.Equal(size, (await exact.search_facts("alice")).GetRawText().Length);
        Assert.Equal("EvidenceBudgetExhausted", (await Assert.ThrowsAsync<TimelineFactResearchException>(() => exact.search_facts("alice"))).Code);
        var ledger = await exact.ValidateLedgerAsync();
        Assert.Equal(size, ledger.EmittedEvidenceChars); Assert.Single(ledger.Facts);
        using var twice = await Tools(f, new(maxTotalSerializedToolEvidenceChars: size * 2));
        await twice.search_facts("alice"); await twice.search_facts("alice");
        Assert.Equal(size * 2, (await twice.ValidateLedgerAsync()).EmittedEvidenceChars);
        Assert.Single((await twice.ValidateLedgerAsync()).Facts);
    }

    [Fact]
    public async Task JSON_escaping_is_included_when_clipping_context_to_remaining_output_budget()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        f.Entry.Content += string.Concat(Enumerable.Repeat("\"<>&\\\n", 200));
        await f.Db.SaveChangesAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        using var baseline = await Tools(f);
        var searchSize = (await baseline.search_facts("alice")).GetRawText().Length;
        var contextSize = (await baseline.fetch_source_entry(fact.Id)).GetRawText().Length;
        using var exact = await Tools(f, new(maxTotalSerializedToolEvidenceChars: searchSize + contextSize));
        await exact.search_facts("alice");
        var full = await exact.fetch_source_entry(fact.Id);
        Assert.False(full.GetProperty("Contexts")[0].GetProperty("Truncated").GetBoolean());
        Assert.Equal(searchSize + contextSize, (await exact.ValidateLedgerAsync()).EmittedEvidenceChars);
        var budget = searchSize + contextSize - 1;
        using var tools = await Tools(f, new(maxTotalSerializedToolEvidenceChars: budget));
        var first = (JsonElement)(await tools.Functions[0].InvokeAsync(new AIFunctionArguments { ["query"] = "alice" }))!;
        var context = (JsonElement)(await tools.Functions[2].InvokeAsync(new AIFunctionArguments { ["factId"] = fact.Id }))!;
        Assert.True(context.GetProperty("Contexts")[0].GetProperty("Truncated").GetBoolean());
        Assert.True(first.GetRawText().Length + context.GetRawText().Length <= budget);
        Assert.Equal(first.GetRawText().Length + context.GetRawText().Length, (await tools.ValidateLedgerAsync()).EmittedEvidenceChars);
        var fullText = full.GetProperty("Contexts")[0].GetProperty("Text").GetString()!;
        var nextLength = context.GetProperty("Contexts")[0].GetProperty("Text").GetString()!.Length + 1;
        var next = System.Text.Json.Nodes.JsonNode.Parse(context.GetRawText())!;
        next["Contexts"]![0]!["Text"] = fullText[..nextLength];
        next["Contexts"]![0]!["Truncated"] = nextLength < fullText.Length;
        next["Truncated"] = nextLength < fullText.Length;
        Assert.True(first.GetRawText().Length + next.ToJsonString().Length > budget);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Distinct_context_and_aggregate_character_caps_preserve_available_context(bool countCap)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var first = await f.AcceptAsync("Alice owns delivery.", "alice");
        await f.NewSourceAsync();
        var second = await f.AcceptAsync("Bob owns testing.", "bob");
        using var tools = await Tools(f, countCap ? new(maxSourceContexts: 1) : new(maxCharsPerContext: 10, maxTotalSourceContextChars: 10));
        await tools.search_facts("owns");
        await tools.fetch_source_entry(first.Id);
        Assert.Equal("SourceContextBudgetExhausted", (await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.fetch_source_entry(second.Id))).Code);
        var snapshot = await tools.ValidateLedgerAsync();
        Assert.Single(snapshot.Contexts); Assert.True(snapshot.BudgetExhausted);
        if (!countCap) Assert.Equal(10, snapshot.SourceContextChars);
    }

    [Fact]
    public async Task Malformed_SDK_argument_binding_is_sanitized_and_counts_before_any_read()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        using var tools = await Tools(f, new(maxToolCalls: 2));
        var error = await Assert.ThrowsAsync<TimelineFactResearchException>(async () =>
            await tools.Functions[2].InvokeAsync(new AIFunctionArguments { ["factId"] = "PRIVATE_SENTINEL_not_a_guid" }));
        Assert.DoesNotContain("PRIVATE_SENTINEL", error.Message);
        await Assert.ThrowsAsync<TimelineFactResearchException>(async () => await tools.Functions[0].InvokeAsync(new AIFunctionArguments()));
        var exhausted = await Assert.ThrowsAsync<TimelineFactResearchException>(async () =>
            await tools.Functions[0].InvokeAsync(new AIFunctionArguments { ["query"] = "alice" }));
        Assert.Equal("ToolCallBudgetExhausted", exhausted.Code);
        // Unlike the explicitly recoverable unknown-ID result, binding failures remain fatal
        // even when the SDK captures the exception and the model tries to finish anyway.
        Assert.Equal("EvidenceReadFailed", (await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.ValidateLedgerAsync())).Code);
    }

    [Fact]
    public async Task Rejected_probe_and_invalid_query_attempts_consume_tool_and_query_limits()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        using var tools = await Tools(f, new(maxToolCalls: 4, maxSearchQueries: 1));
        Assert.Equal("InvalidQuery", (await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.search_facts("!?"))).Code);
        Assert.Equal("SearchQueryBudgetExhausted", (await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.search_facts("alice"))).Code);
        await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.fetch_facts([Guid.NewGuid()]));
        await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.fetch_source_entry(Guid.NewGuid()));
        Assert.Equal("ToolCallBudgetExhausted", (await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.search_facts("alice"))).Code);
        Assert.Equal(5, (await tools.ValidateLedgerAsync()).ToolCalls);
        Assert.Empty((await tools.ValidateLedgerAsync()).Facts);
    }

    [Fact]
    public async Task Current_admin_role_project_access_and_timeline_ownership_are_not_captured_authority()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await f.AcceptAsync("Alice owns delivery.", "alice");
        var role = new UserRole { RoleName = "Admin" };
        var user = new User { Email = "research-admin@example.test", PasswordHash = "test", IDUserRole = role.Id, UserRole = role };
        f.Db.AddRange(role, user); await f.Db.SaveChangesAsync();
        using var tools = await TimelineFactInformationTools.CreateAsync(f.Db, new(f.Project.Id, f.Timeline.Id), user.Id, true);
        await tools.search_facts("alice");
        role.RoleName = "User"; await f.Db.SaveChangesAsync();
        Assert.Equal("AccessDenied", (await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.search_facts("alice"))).Code);
        await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.ValidateLedgerAsync());
        await Assert.ThrowsAsync<TimelineFactResearchException>(() => TimelineFactInformationTools.CreateAsync(f.Db, new(f.Project.Id, f.Timeline.Id), user.Id, true));
        using var owner = await Tools(f);
        await owner.search_facts("alice");
        await using (var raw = f.Unguarded())
        {
            (await raw.Set<Timeline>().SingleAsync()).ProjectId = Guid.NewGuid();
            await raw.SaveChangesAsync();
        }
        Assert.Equal("ScopeUnavailable", (await Assert.ThrowsAsync<TimelineFactResearchException>(() => owner.ValidateLedgerAsync())).Code);
    }

    [Fact]
    public void Options_are_immutable_validated_server_bounds_not_model_input()
    {
        var options = new TimelineFactResearchOptions();
        Assert.Equal((20, 20, 8, 40, 10, 3, 4000, 12000, 32000, 1500),
            (options.MaxModelCalls, options.MaxToolCalls, options.MaxSearchQueries, options.MaxDistinctFacts,
             options.MaxHitsPerSearch, options.MaxSourceContexts, options.MaxCharsPerContext,
             options.MaxTotalSourceContextChars, options.MaxTotalSerializedToolEvidenceChars, options.MaxOutputTokens));
        Assert.Equal(TimeSpan.FromSeconds(60), options.Deadline);
        Assert.All(typeof(TimelineFactResearchOptions).GetProperties(), p => Assert.Null(p.SetMethod));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimelineFactResearchOptions(maxToolCalls: 21));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimelineFactResearchOptions(maxDistinctFacts: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimelineFactResearchOptions(maxTotalSerializedToolEvidenceChars: 32001));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimelineFactResearchOptions(deadline: TimeSpan.FromSeconds(61)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(10)]
    [InlineData(20)]
    public void Model_call_budget_accepts_positive_values_through_twenty(int maxModelCalls)
    {
        Assert.Equal(maxModelCalls, new TimelineFactResearchOptions(maxModelCalls: maxModelCalls).MaxModelCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void Model_call_budget_rejects_values_outside_server_bounds(int maxModelCalls)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new TimelineFactResearchOptions(maxModelCalls: maxModelCalls));
        Assert.Equal("maxModelCalls", error.ParamName);
    }

    [Fact]
    public async Task Gate_serializes_actual_parallel_SDK_reads_and_final_validation_with_cancellable_waiters()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await f.AcceptAsync("Alice owns delivery.", "alice");
        using var probe = new BlockingQuery();
        var options = (DbContextOptions<ProjectTimelineDbContext>)f.Db.GetService<IDbContextOptions>();
        await using var db = new ProjectTimelineDbContext(new DbContextOptionsBuilder<ProjectTimelineDbContext>(options).AddInterceptors(probe).Options);
        using var tools = await TimelineFactInformationTools.CreateAsync(db, new(f.Project.Id, f.Timeline.Id), f.Actor.Id, false);
        probe.Armed = true;
        var first = Task.Run(async () => await tools.Functions[0].InvokeAsync(new AIFunctionArguments { ["query"] = "alice" }));
        Task<object?>? second = null;
        try
        {
            Assert.True(await Task.Run(() => probe.Started.Wait(TimeSpan.FromSeconds(10))));
            using var cts = new CancellationTokenSource();
            second = Task.Run(async () => await tools.Functions[0].InvokeAsync(new AIFunctionArguments { ["query"] = "delivery" }));
            var cancelled = tools.Functions[0].InvokeAsync(new AIFunctionArguments { ["query"] = "alice" }, cts.Token).AsTask();
            var validation = tools.ValidateLedgerAsync();
            Assert.False(validation.IsCompleted);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            probe.Release.Set();
            await first; await second; await validation;
            Assert.Empty(db.ChangeTracker.Entries());
            Assert.Equal(3, (await tools.ValidateLedgerAsync()).ToolCalls);
        }
        finally { probe.Release.Set(); await first; if (second is not null) await second; }
    }

    private sealed class BlockingQuery : IQueryExpressionInterceptor, IDisposable
    {
        public bool Armed;
        public ManualResetEventSlim Started { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public Expression QueryCompilationStarting(Expression expression, QueryExpressionEventData eventData)
        {
            if (Armed)
            {
                Armed = false; Started.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Test query barrier timed out.");
            }
            return expression;
        }
        public void Dispose() { Started.Dispose(); Release.Dispose(); }
    }

    [Fact]
    public async Task Parallel_invocation_is_serialized_and_cancelled_or_rejected_attempts_count()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await f.AcceptAsync("Alice owns delivery.", "alice");
        using var tools = await Tools(f, new(maxToolCalls: 4));
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => tools.search_facts("alice")));
        var snapshot = await tools.ValidateLedgerAsync();
        Assert.Equal(4, snapshot.ToolCalls); Assert.Single(snapshot.Facts);
        await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.fetch_facts([Guid.NewGuid()]));
        Assert.Equal(5, (await tools.ValidateLedgerAsync()).ToolCalls);
        using var cts = new CancellationTokenSource();
        using var cancelled = await Tools(f, ct: cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.search_facts("alice"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.ValidateLedgerAsync());
    }
}
