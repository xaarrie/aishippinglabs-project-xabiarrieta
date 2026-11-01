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
    public async Task Tool_descriptions_advertise_effective_limits_not_obsolete_context_caps(bool reduced)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var limits = reduced ? new TimelineFactResearchOptions(maxDistinctFacts: 7, maxHitsPerSearch: 4) : new();
        using var tools = await Tools(f, limits);
        Assert.Contains("no expression count ceiling", tools.Functions[0].Description);
        Assert.Contains($"at most {limits.MaxHitsPerSearch} hits per query", tools.Functions[0].Description);
        Assert.Contains("complete distinct entryIds", tools.Functions[1].Description);
        Assert.Contains("Duplicate source EntryIds are normalized", tools.Functions[1].Description);
        Assert.Contains("remaining global output/time budgets", tools.Functions[1].Description);
        Assert.Equal(limits.MaxDistinctFacts, limits.MaxSourceContexts);
        Assert.Equal(limits.MaxTotalSerializedToolEvidenceChars, limits.MaxCharsPerContext);
        Assert.Equal(limits.MaxTotalSerializedToolEvidenceChars, limits.MaxTotalSourceContextChars);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Model_wire_exposes_entryId_only_on_facts_and_ledger_retains_hidden_provenance(bool source)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        using var tools = await Tools(f);
        var batch = await tools.search_facts_batch(["alice"]);
        var payload = await tools.fetch_source_entry([fact.EntryId]);
        var json = (source ? payload : batch).GetRawText();
        foreach (var field in new[] { "SourceFingerprint", "AuthorId", "CreationReviewId", "ExtractionJobId" })
            Assert.DoesNotContain(field, json);
        Assert.DoesNotContain(fact.SourceFingerprint, json);
        var ledger = await tools.ValidateLedgerAsync();
        var original = Assert.Single(ledger.Facts);
        Assert.Equal(fact.Id, original.Id); Assert.Equal(fact.EntryId, original.EntryId);
        Assert.Equal(fact.SourceFingerprint, original.SourceFingerprint);
        Assert.NotNull(fact.CreationReviewId);
        Assert.Equal(fact.CreationReviewId, original.CreationReviewId);
        Assert.Equal(fact.ExtractionJobId, original.ExtractionJobId);
        if (source)
        {
            var wire = Assert.Single(payload.GetProperty("Contexts").EnumerateArray());
            var context = Assert.Single(ledger.Contexts);
            Assert.Equal(new[] { "EvidenceId", "EntryNumber", "AnchorFactIds", "Text", "Truncated", "EvidenceKind" }, wire.EnumerateObject().Select(x => x.Name));
            Assert.Equal(context.EvidenceId, wire.GetProperty("EvidenceId").GetGuid());
            Assert.NotEqual(fact.Id, context.EvidenceId);
            Assert.Equal(f.Entry.EntryNumber, wire.GetProperty("EntryNumber").GetInt32());
            Assert.Equal(fact.Id, wire.GetProperty("AnchorFactIds")[0].GetGuid());
            Assert.Equal(context.Text, wire.GetProperty("Text").GetString());
            Assert.Equal(context.Truncated, wire.GetProperty("Truncated").GetBoolean());
            Assert.Equal(context.EvidenceKind, wire.GetProperty("EvidenceKind").GetString());
            Assert.Equal(f.Entry.Id, context.EntryId); Assert.Equal(f.Actor.Id, context.AuthorId);
            Assert.Equal(fact.SourceFingerprint, context.SourceFingerprint);
        }
        else
        {
            var wire = Assert.Single(batch.GetProperty("Results")[0].GetProperty("Facts").EnumerateArray());
            Assert.Equal(new[] { "Id", "EntryId", "EntryNumber", "Claim", "EntityKey", "EntityType", "SourceQuote", "SourceKind", "ClarificationEvidence" }, wire.EnumerateObject().Select(x => x.Name));
            Assert.Equal(original.Id, wire.GetProperty("Id").GetGuid());
            Assert.Equal(original.EntryId, wire.GetProperty("EntryId").GetGuid());
            Assert.Equal(original.Claim, wire.GetProperty("Claim").GetString());
            Assert.Equal(original.EntityKey, wire.GetProperty("EntityKey").GetString());
            Assert.Equal(original.EntityType, wire.GetProperty("EntityType").GetString());
            Assert.Equal(original.SourceQuote, wire.GetProperty("SourceQuote").GetString());
            Assert.Equal(original.SourceKind, wire.GetProperty("SourceKind").GetString());
            Assert.Equal(original.ClarificationEvidence, wire.GetProperty("ClarificationEvidence").GetString());
        }
    }

    [Fact]
    public async Task Ranking_prefers_matched_terms_over_entry_recency_and_translates_to_bounded_SQL()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var older = await f.AcceptAsync("Alice owns delivery.", "alice-delivery");
        await f.NewSourceAsync();
        await f.AcceptAsync("Bob owns testing.", "bob");
        using var tools = await Tools(f);
        var payload = await tools.search_facts_batch(["alice delivery testing"]);
        Assert.Equal(older.Id, payload.GetProperty("Results")[0].GetProperty("Facts")[0].GetProperty("Id").GetGuid());
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

    [Theory]
    [InlineData("entry")]
    [InlineData("invalid")]
    [InlineData("pending")]
    [InlineData("revision")]
    [InlineData("revoked")]
    public async Task Current_mutations_prevent_final_evidence_release(string mutation)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        using var tools = await Tools(f);
        await tools.search_facts_batch(["alice"]);
        await tools.fetch_source_entry([fact.EntryId]);
        await using (var raw = f.Unguarded())
        {
            if (mutation == "entry") (await raw.Set<TimelineEntry>().SingleAsync()).Content = "PRIVATE_CHANGED_BODY";
            if (mutation == "invalid") (await raw.Set<TimelineFact>().SingleAsync()).Valid = false;
            if (mutation == "pending") (await raw.Set<TimelineFactProposal>().SingleAsync()).Status = "Pending";
            if (mutation == "revision") (await raw.Set<Timeline>().SingleAsync()).FactRevision++;
            if (mutation == "revoked") (await raw.Set<User>().SingleAsync()).IsActive = false;
            await raw.SaveChangesAsync();
        }
        var error = await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.ValidateLedgerAsync());
        Assert.Equal(mutation switch { "entry" => "SourceContextUnavailable", "revision" => "FactRevisionChanged", "revoked" => "AccessDenied", _ => "EvidenceChanged" }, error.Code);
        Assert.DoesNotContain("PRIVATE", error.Message);
    }

    [Fact]
    public async Task Clarification_quote_is_review_answer_evidence_not_raw_entry_proof()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var service = new TimelineFactService(f.Db);
        var proposal = await service.PrepareAsync(f.Scope, f.Actor.Id, false, 0,
            new([new("Carol approved", "carol", "Person", "Carol approved", "Clarification")], [], [new("Who approved?", "Carol approved")]));
        await service.AcceptAsync(f.Scope, proposal.Id, f.Actor.Id, false);
        using var tools = await Tools(f);
        var result = await tools.search_facts_batch(["carol"]);
        var fact = result.GetProperty("Results")[0].GetProperty("Facts")[0];
        Assert.Equal("Clarification", fact.GetProperty("SourceKind").GetString());
        Assert.Contains("Carol approved", fact.GetProperty("ClarificationEvidence").GetString());
        var context = (await tools.fetch_source_entry([fact.GetProperty("EntryId").GetGuid()])).GetProperty("Contexts")[0];
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
        await f.AcceptAsync("Alice owns delivery.", "alice");
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
        Assert.Empty((await tools.search_facts_batch(["alice"])).GetProperty("Results")[0].GetProperty("Facts").EnumerateArray());
        Assert.Empty((await tools.ValidateLedgerAsync()).Facts);
    }

    [Fact]
    public async Task Hit_cap_marks_partial_but_still_allows_fetch_and_immutable_ledger_snapshots()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await f.AcceptAsync("Alice owns delivery.", "alice");
        await f.NewSourceAsync();
        await f.AcceptAsync("Bob owns testing.", "bob");
        using var tools = await Tools(f, new(maxHitsPerSearch: 1));
        var payload = await tools.search_facts_batch(["owns"]);
        Assert.True(payload.GetProperty("Truncated").GetBoolean());
        var id = payload.GetProperty("Results")[0].GetProperty("Facts")[0].GetProperty("EntryId").GetGuid();
        await tools.fetch_source_entry([id]);
        var snapshot = await tools.ValidateLedgerAsync();
        Assert.Single(snapshot.Facts); Assert.Single(snapshot.Contexts);
        await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.fetch_source_entry([id]));
        Assert.Equal(2, snapshot.ToolCalls); // A returned immutable snapshot is never rewritten.
        await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.ValidateLedgerAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Malformed_SDK_binding_consumes_phase_before_binding_and_forbids_retries(bool source)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        using var tools = await Tools(f);
        if (source) await tools.search_facts_batch(["alice"]);
        var function = tools.Functions[source ? 1 : 0];
        var error = await Assert.ThrowsAsync<TimelineFactResearchException>(async () =>
            await function.InvokeAsync(source ? new AIFunctionArguments { ["entryIds"] = new[] { "PRIVATE_not_a_guid" } } : new AIFunctionArguments()));
        Assert.Equal("EvidenceReadFailed", error.Code); Assert.DoesNotContain("PRIVATE", error.Message);
        var retry = await Assert.ThrowsAsync<TimelineFactResearchException>(async () => await function.InvokeAsync(source
            ? new AIFunctionArguments { ["entryIds"] = new[] { fact.EntryId } } : new AIFunctionArguments { ["queries"] = new[] { "alice" } }));
        Assert.Equal(error.Code, retry.Code);
        Assert.Equal(error.Code, (await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.ValidateLedgerAsync())).Code);
    }

    [Theory]
    [InlineData("sdk-array")]
    [InlineData("sdk-list")]
    [InlineData("sdk-json")]
    public async Task Malformed_source_tail_after_duplicates_is_not_filtered_and_consumes_phase(string binding)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        using var tools = await Tools(f, new(maxDistinctFacts: 3));
        await tools.search_facts_batch(["alice"]);
        var id = fact.EntryId.ToString();
        var ids = new[] { id, id, id, id, "PRIVATE_INVALID_GUID" }; // Invalid tail is beyond the raw-count cap.
        var args = new AIFunctionArguments { ["entryIds"] = binding switch
        { "sdk-list" => ids.ToList(), "sdk-json" => JsonSerializer.SerializeToElement(ids), _ => ids } };
        var error = await Assert.ThrowsAsync<TimelineFactResearchException>(async () => await tools.Functions[1].InvokeAsync(args));
        Assert.Equal("EvidenceReadFailed", error.Code);
        Assert.DoesNotContain("PRIVATE", error.Message);
        Assert.Equal(error.Code, (await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.fetch_source_entry([fact.EntryId]))).Code);
        Assert.Equal(error.Code, (await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.ValidateLedgerAsync())).Code);
    }

    [Theory]
    [InlineData("!?")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("oversized")]
    public async Task Invalid_expression_is_fatal_and_cannot_earn_a_second_batch(string? query)
    {
        if (query == "oversized") query = new string('x', 301);
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        using var tools = await Tools(f);
        Assert.Equal("InvalidQuery", (await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.search_facts_batch([query!]))).Code);
        await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.search_facts_batch(["alice"]));
        await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.ValidateLedgerAsync());
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
        await tools.search_facts_batch(["alice"]);
        role.RoleName = "User"; await f.Db.SaveChangesAsync();
        Assert.Equal("AccessDenied", (await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.ValidateLedgerAsync())).Code);
        await Assert.ThrowsAsync<TimelineFactResearchException>(() => TimelineFactInformationTools.CreateAsync(f.Db, new(f.Project.Id, f.Timeline.Id), user.Id, true));
        using var owner = await Tools(f);
        await owner.search_facts_batch(["alice"]);
        await using (var raw = f.Unguarded())
        {
            (await raw.Set<Timeline>().SingleAsync()).ProjectId = Guid.NewGuid();
            await raw.SaveChangesAsync();
        }
        Assert.Equal("ScopeUnavailable", (await Assert.ThrowsAsync<TimelineFactResearchException>(() => owner.ValidateLedgerAsync())).Code);
    }

    [Fact]
    public void Options_are_immutable_validated_server_bounds_with_derived_context_metadata()
    {
        var options = new TimelineFactResearchOptions();
        Assert.Equal((20, 20, 40, 10, 40, 32000, 32000, 32000, 1500),
            (options.MaxModelCalls, options.MaxToolCalls, options.MaxDistinctFacts, options.MaxHitsPerSearch,
                options.MaxSourceContexts, options.MaxCharsPerContext, options.MaxTotalSourceContextChars,
                options.MaxTotalSerializedToolEvidenceChars, options.MaxOutputTokens));
        Assert.Null(options.MaxSearchQueries);
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
    public void Model_call_budget_accepts_positive_values_through_twenty(int maxModelCalls) =>
        Assert.Equal(maxModelCalls, new TimelineFactResearchOptions(maxModelCalls: maxModelCalls).MaxModelCalls);

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void Model_call_budget_rejects_values_outside_server_bounds(int maxModelCalls) =>
        Assert.Equal("maxModelCalls", Assert.Throws<ArgumentOutOfRangeException>(() => new TimelineFactResearchOptions(maxModelCalls: maxModelCalls)).ParamName);

    [Theory]
    [InlineData("direct")]
    [InlineData("sdk-array")]
    [InlineData("sdk-list")]
    [InlineData("sdk-json")]
    public async Task Gate_serializes_search_fetch_binding_snapshots_and_final_validation(string binding)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var first = await f.AcceptAsync("Alice owns delivery.", "alice");
        await f.NewSourceAsync();
        var second = await f.AcceptAsync("Bob owns testing.", "bob");
        using var probe = new BlockingQuery();
        var options = (DbContextOptions<ProjectTimelineDbContext>)f.Db.GetService<IDbContextOptions>();
        await using var db = new ProjectTimelineDbContext(new DbContextOptionsBuilder<ProjectTimelineDbContext>(options)
            .EnableServiceProviderCaching(false).AddInterceptors(probe).Options);
        using var tools = await TimelineFactInformationTools.CreateAsync(db, new(f.Project.Id, f.Timeline.Id), f.Actor.Id, false,
            new(maxDistinctFacts: 2));
        probe.Armed = true;
        var search = Task.Run(() => tools.search_facts_batch(["owns"]));
        Task<JsonElement>? fetch = null;
        try
        {
            Assert.True(await Task.Run(() => probe.Started.Wait(TimeSpan.FromSeconds(10))));
            var ids = new[] { second.EntryId, first.EntryId, second.EntryId, first.EntryId };
            var list = ids.ToList();
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ids));
            fetch = binding == "direct" ? tools.fetch_source_entry(ids) : Invoke();
            async Task<JsonElement> Invoke() => (JsonElement)(await tools.Functions[1].InvokeAsync(new AIFunctionArguments
                { ["entryIds"] = binding switch { "sdk-list" => list, "sdk-json" => json.RootElement, _ => ids } }))!;
            ids[1] = Guid.NewGuid(); list[1] = Guid.NewGuid(); json.Dispose();
            using var cts = new CancellationTokenSource();
            var cancelled = tools.fetch_source_entry([first.EntryId], cts.Token);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            var validation = tools.ValidateLedgerAsync();
            Assert.False(fetch.IsCompleted); Assert.False(validation.IsCompleted);
            probe.Release.Set();
            var batchOutput = await search;
            var fetchOutput = await fetch;
            var ledger = await validation;
            Assert.Equal(2, ledger.Contexts.Length); Assert.Equal(3, ledger.ToolCalls);
            Assert.Equal(new[] { second.EntryId, first.EntryId }, fetchOutput.GetProperty("Contexts").EnumerateArray()
                .Select(x => ledger.Contexts.Single(c => c.EvidenceId == x.GetProperty("EvidenceId").GetGuid()).EntryId));
            Assert.Equal(batchOutput.GetRawText().Length + fetchOutput.GetRawText().Length, ledger.EmittedEvidenceChars);
            Assert.Equal(ledger.Contexts.Sum(x => x.Text.Length), ledger.SourceContextChars);
            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally { probe.Release.Set(); await search; if (fetch is not null) await fetch; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_phase_calls_enforce_both_orderings(bool fetchFirst)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        using var probe = new BlockingQuery();
        var options = (DbContextOptions<ProjectTimelineDbContext>)f.Db.GetService<IDbContextOptions>();
        await using var db = new ProjectTimelineDbContext(new DbContextOptionsBuilder<ProjectTimelineDbContext>(options)
            .EnableServiceProviderCaching(false).AddInterceptors(probe).Options);
        using var tools = await TimelineFactInformationTools.CreateAsync(db, new(f.Project.Id, f.Timeline.Id), f.Actor.Id, false);
        var operationGate = (SemaphoreSlim)typeof(TimelineFactInformationTools).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tools)!;
        await operationGate.WaitAsync();
        var first = Record.ExceptionAsync(() => fetchFirst ? tools.fetch_source_entry([fact.EntryId]) : tools.search_facts_batch(["alice"]));
        var second = Record.ExceptionAsync(() => fetchFirst ? tools.search_facts_batch(["alice"]) : tools.fetch_source_entry([fact.EntryId]));
        Assert.False(first.IsCompleted); Assert.False(second.IsCompleted);
        operationGate.Release();
        try
        {
            if (fetchFirst)
            {
                Assert.IsType<TimelineFactResearchException>(await first);
                Assert.IsType<TimelineFactResearchException>(await second);
                await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.ValidateLedgerAsync());
            }
            else { Assert.Null(await first); Assert.Null(await second); Assert.Equal(2, (await tools.ValidateLedgerAsync()).ToolCalls); }
        }
        finally { await first; await second; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_repeat_calls_allow_at_most_one_attempted_phase(bool source)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        using var tools = await Tools(f);
        if (source) await tools.search_facts_batch(["alice"]);
        Task<JsonElement> Invoke() => source ? tools.fetch_source_entry([fact.EntryId]) : tools.search_facts_batch(["alice"]);
        var operationGate = (SemaphoreSlim)typeof(TimelineFactInformationTools).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tools)!;
        await operationGate.WaitAsync();
        var first = Record.ExceptionAsync(Invoke);
        var second = Record.ExceptionAsync(Invoke);
        Assert.False(first.IsCompleted); Assert.False(second.IsCompleted);
        operationGate.Release();
        var results = await Task.WhenAll(first, second);
        Assert.Single(results, x => x is null);
        Assert.Equal("InvalidResearchPhase", Assert.IsType<TimelineFactResearchException>(Assert.Single(results, x => x is not null)).Code);
        await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.ValidateLedgerAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Final_validation_requires_both_phases_and_lowered_tool_budget_cannot_skip_fetch(bool toolLimit)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        using var tools = await Tools(f, new(maxToolCalls: toolLimit ? 1 : 20));
        Assert.Equal("MissingResearchPhase", (await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.ValidateLedgerAsync())).Code);
        await tools.search_facts_batch(["alice"]);
        Assert.Equal("MissingResearchPhase", (await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.ValidateLedgerAsync())).Code);
        if (toolLimit)
        {
            Assert.Equal("ToolCallBudgetExhausted", (await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.fetch_source_entry([fact.EntryId]))).Code);
            await Assert.ThrowsAsync<TimelineFactResearchException>(() => tools.ValidateLedgerAsync());
        }
        else { await tools.fetch_source_entry([fact.EntryId]); Assert.Single((await tools.ValidateLedgerAsync()).Contexts); }
    }

    [Fact]
    public async Task Cancelled_operation_never_releases_evidence()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        using var cts = new CancellationTokenSource();
        using var tools = await Tools(f, ct: cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.search_facts_batch(["alice"]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.ValidateLedgerAsync());
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
}
