using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.UI.WebApi;
using ProjectTimeline.DataAccessLogic;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class TimelineFactHttpTests
{
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Explicit_generation_http_contract_displays_pending_then_reconciles_and_locks()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var options = (DbContextOptions<ProjectTimelineDbContext>)f.Db.GetService<IDbContextOptions>();
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthentication>("Test", _ => { });
        builder.Services.AddAuthorization(); builder.Services.AddRouting();
        builder.Services.AddSingleton(options);
        builder.Services.AddScoped(_ => new ProjectTimelineDbContext(options));
        builder.Services.AddTimelineFacts(new ConfigurationBuilder().Build());
        builder.Services.AddScoped<ITimelineFactProvider>(_ => new PendingGenerationProvider());
        var agent = new FactReviewFakeAgent { Result = new() { Additions = [new("Alice owns delivery.", "alice", "Person", "Alice owns delivery.")] } };
        builder.Services.AddScoped(sp => new TimelineFactApplication(sp.GetRequiredService<ProjectTimelineDbContext>(), () => agent,
            sp.GetRequiredService<TimelineFactProcessing>()));
        await using var app = builder.Build();
        app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseTimelineFactBodyBounds(); app.MapTimelineFacts();
        await app.StartAsync();
        using var client = app.GetTestClient(); client.DefaultRequestHeaders.Add("X-Test-Actor", f.Actor.Id.ToString());
        var path = $"/api/entries/{f.Entry.Id}";
        using var generated = await client.PostAsync(path + "/fact-generation", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        var state = await JsonAsync(generated);
        Assert.Equal("Pending", state.GetProperty("lifecycleState").GetString());
        Assert.Empty(state.GetProperty("facts").EnumerateArray());
        Assert.False(Assert.Single(state.GetProperty("proposals").EnumerateArray()).GetProperty("ready").GetBoolean());
        var sourceSnapshot = state.GetProperty("source");
        Assert.Equal(f.Entry.Id, sourceSnapshot.GetProperty("entryId").GetGuid());
        Assert.Equal(f.Entry.Title, sourceSnapshot.GetProperty("title").GetString());
        Assert.Equal(f.Entry.Content, sourceSnapshot.GetProperty("content").GetString());
        Assert.Equal(TimelineFactEvidence.Fingerprint(f.Entry), sourceSnapshot.GetProperty("fingerprint").GetString());
        Assert.Equal(sourceSnapshot.GetProperty("fingerprint").GetString(), state.GetProperty("proposals")[0].GetProperty("sourceFingerprint").GetString());
        Assert.False(state.GetProperty("refreshRequired").GetBoolean());
        var rawId = state.GetProperty("generationId").GetGuid();
        using var rawAccept = await client.PostAsync($"{path}/fact-proposals/{rawId}/accept", null);
        Assert.Equal(HttpStatusCode.Conflict, rawAccept.StatusCode);
        using var planned = await client.PostAsync(path + "/fact-proposals", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        var reviewId = (await JsonAsync(planned)).GetProperty("proposalId").GetGuid();
        using var accepted = await client.PostAsync($"{path}/fact-proposals/{reviewId}/accept", null);
        await JsonAsync(accepted);
        using var locked = await client.PostAsync(path + "/fact-generation", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode);
        using var read = await client.GetAsync(path + "/facts");
        state = await JsonAsync(read);
        Assert.Equal("Reconciled", state.GetProperty("lifecycleState").GetString());
        Assert.False(state.GetProperty("canEdit").GetBoolean());
        Assert.Equal(sourceSnapshot.GetProperty("fingerprint").GetString(), state.GetProperty("source").GetProperty("fingerprint").GetString());
        Assert.False(state.GetProperty("refreshRequired").GetBoolean());
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("outsider", HttpStatusCode.Forbidden)]
    [InlineData("inactive", HttpStatusCode.Forbidden)]
    public async Task Access_denied_before_agent_construction(string identity, HttpStatusCode expected)
    {
        await using var h = await TimelineFactHttpHost.CreateAsync();
        h.Client.DefaultRequestHeaders.Remove("X-Test-Actor");
        if (identity == "inactive")
        {
            h.Actor.IsActive = false;
            await h.Db.SaveChangesAsync();
            h.Client.DefaultRequestHeaders.Add("X-Test-Actor", h.Actor.Id.ToString());
        }
        if (identity == "outsider")
        {
            var outsider = new User { Email = "outsider@example.test", DisplayName = "Other", PasswordHash = "test" };
            h.Db.Add(outsider); await h.Db.SaveChangesAsync();
            h.Client.DefaultRequestHeaders.Add("X-Test-Actor", outsider.Id.ToString());
        }
        using var plan = await h.PostAsync(h.Plan, "{}");
        using var read = await h.Client.GetAsync(h.Facts);
        using var accept = await h.PostAsync(h.Accept(Guid.NewGuid()));
        using var backfill = await h.PostAsync(h.Backfill, "{\"afterEntryNumber\":0,\"throughEntryNumber\":2}");
        foreach (var response in new[] { plan, read, accept, backfill }) Assert.Equal(expected, response.StatusCode);
        Assert.Equal(0, h.AgentConstructions);
    }

    [Theory]
    [InlineData(false, "malformed", HttpStatusCode.BadRequest)]
    [InlineData(true, "malformed", HttpStatusCode.BadRequest)]
    [InlineData(false, "null", HttpStatusCode.BadRequest)]
    [InlineData(true, "null", HttpStatusCode.BadRequest)]
    [InlineData(false, "oversized", HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(true, "oversized", HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(false, "forged", HttpStatusCode.BadRequest)]
    [InlineData(true, "forged", HttpStatusCode.BadRequest)]
    public async Task Json_and_body_bounds_execute_over_http(bool chunked, string kind, HttpStatusCode expected)
    {
        await using var h = await TimelineFactHttpHost.CreateAsync();
        var body = kind switch
        {
            "malformed" => "{", "null" => "null",
            "oversized" => "{\"answers\":[],\"previousProposalId\":null}" + new string(' ', 65536),
            _ => "{\"ready\":true,\"additions\":[{\"claim\":\"forged\"}]}"
        };
        using var response = await h.PostAsync(h.Plan, body, chunked);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(0, h.AgentConstructions);
        Assert.Empty(await h.Db.TimelineFactProposals.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_64KiB_valid_json_is_allowed(bool chunked)
    {
        await using var h = await TimelineFactHttpHost.CreateAsync();
        await PendingGenerationProvider.GenerateAsync(h.Db, h.Entry, h.Actor.Id, h.Project.Id);
        using var response = await h.PostAsync(h.Plan, "{}" + new string(' ', 65534), chunked);
        await JsonAsync(response);
        Assert.Equal(1, h.AgentConstructions);
    }

    [Theory]
    [InlineData(false, "{}")]
    [InlineData(true, "{}")]
    [InlineData(false, " ")]
    [InlineData(true, " ")]
    [InlineData(true, "{\"ready\":true,\"additions\":[]}")]
    public async Task Acceptance_rejects_every_supplied_body(bool chunked, string body)
    {
        await using var h = await TimelineFactHttpHost.CreateAsync();
        await PendingGenerationProvider.GenerateAsync(h.Db, h.Entry, h.Actor.Id, h.Project.Id);
        h.Agent.Result = new() { Additions = [new("Alice owns delivery.", "alice", "Person", "Alice owns delivery.")] };
        using var plan = await h.PostAsync(h.Plan, "{}");
        var id = (await JsonAsync(plan)).GetProperty("proposalId").GetGuid();
        using var response = await h.PostAsync(h.Accept(id), body, chunked);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await h.Db.TimelineFacts.ToListAsync());
    }

    [Fact]
    public async Task Activation_is_bodyless_idempotent_source_preserving_and_get_is_safe()
    {
        await using var h = await TimelineFactHttpHost.CreateAsync();
        await PendingGenerationProvider.GenerateAsync(h.Db, h.Entry, h.Actor.Id, h.Project.Id);
        var source = h.Entry.Content;
        h.Agent.Result = new() { Additions = [new(source, "alice", "Person", source)] };
        using var plan = await h.PostAsync(h.Plan, "{}", true);
        var proposal = await JsonAsync(plan);
        Assert.True(proposal.GetProperty("ready").GetBoolean());
        var id = proposal.GetProperty("proposalId").GetGuid();
        for (var i = 0; i < 2; i++)
        {
            using var accepted = await h.PostAsync(h.Accept(id));
            Assert.Equal("Accepted", (await JsonAsync(accepted)).GetProperty("state").GetString());
        }
        var fact = Assert.Single(await h.Db.TimelineFacts.ToListAsync());
        fact.Embedding = new Pgvector.Vector(Enumerable.Repeat(1f, 1536).ToArray());
        fact.EmbeddingModel = "text-embedding-3-small";
        fact.EmbeddedAtUtc = DateTime.UtcNow;
        var job = await h.Db.TimelineFactJobs.FirstAsync(x => x.Kind == "Embedding");
        job.LastErrorCode = "HTTP_SECRET_SENTINEL";
        await h.Db.SaveChangesAsync();
        using var read = await h.Client.GetAsync(h.Facts);
        var dto = await JsonAsync(read);
        Assert.Single(dto.GetProperty("facts").EnumerateArray());
        var text = dto.GetRawText();
        Assert.DoesNotContain("\"embedding\":", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"embeddingModel\":", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HTTP_SECRET_SENTINEL", text);
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(source, (await h.Db.TimelineEntries.AsNoTracking().SingleAsync()).Content);
    }

    [Fact]
    public async Task Clarification_binds_previous_id_and_question_index_to_stored_proposal()
    {
        await using var h = await TimelineFactHttpHost.CreateAsync();
        await PendingGenerationProvider.GenerateAsync(h.Db, h.Entry, h.Actor.Id, h.Project.Id);
        h.Agent.Result = new() { Questions = [new("Who owns release?", null)] };
        using var initial = await h.PostAsync(h.Plan, "{}");
        var first = (await JsonAsync(initial)).GetProperty("proposalId").GetGuid();
        using var incomplete = await h.PostAsync(h.Accept(first));
        Assert.Equal(HttpStatusCode.Conflict, incomplete.StatusCode);
        h.Agent.Result = new() { Additions = [new("Carol owns release.", "carol", "Person", "Carol owns release.", "Clarification")] };
        using var invalidIndex = await h.PostAsync(h.Plan, $"{{\"previousProposalId\":\"{first}\",\"answers\":[{{\"questionIndex\":9,\"answer\":\"Carol owns release.\"}}]}}");
        Assert.Equal(HttpStatusCode.Conflict, invalidIndex.StatusCode);
        using var next = await h.PostAsync(h.Plan, $"{{\"previousProposalId\":\"{first}\",\"answers\":[{{\"questionIndex\":0,\"answer\":\"Carol owns release.\"}}]}}", true);
        var dto = await JsonAsync(next);
        Assert.True(dto.GetProperty("ready").GetBoolean());
        Assert.Equal("Carol owns release.", dto.GetProperty("questions")[0].GetProperty("answer").GetString());
        using var accept = await h.PostAsync(h.Accept(dto.GetProperty("proposalId").GetGuid()));
        await JsonAsync(accept);
        Assert.Equal("Clarification", Assert.Single(await h.Db.TimelineFacts.ToListAsync()).SourceKind);
    }

    [Fact]
    public async Task Stale_missing_and_cross_entry_ids_do_not_activate_facts()
    {
        await using var h = await TimelineFactHttpHost.CreateAsync();
        h.Agent.Result = new() { Additions = [new(h.Entry.Content, "alice", "Person", h.Entry.Content)] };
        var other = new TimelineEntry { TimelineId = h.Timeline.Id, AuthorId = h.Actor.Id, EntryNumber = 3, Title = "Other", Content = "Other" };
        h.Db.Add(other); await h.Db.SaveChangesAsync();
        await PendingGenerationProvider.GenerateAsync(h.Db, h.Entry, h.Actor.Id, h.Project.Id);
        using var firstResponse = await h.PostAsync(h.Plan, "{}");
        using var secondResponse = await h.PostAsync(h.Plan, "{}");
        var first = (await JsonAsync(firstResponse)).GetProperty("proposalId").GetGuid();
        var second = (await JsonAsync(secondResponse)).GetProperty("proposalId").GetGuid();
        using var cross = await h.PostAsync($"/api/entries/{other.Id}/fact-proposals/{first}/accept");
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
        using var accepted = await h.PostAsync(h.Accept(second));
        await JsonAsync(accepted);
        using var stale = await h.PostAsync(h.Accept(first));
        using var missing = await h.PostAsync(h.Accept(Guid.NewGuid()));
        using var missingEntry = await h.PostAsync($"/api/entries/{Guid.NewGuid()}/fact-proposals", "{}");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingEntry.StatusCode);
        Assert.Single(await h.Db.TimelineFacts.ToListAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Provider_factory_and_execution_errors_return_safe_502_without_secret_traces(bool factory)
    {
        await using var h = await TimelineFactHttpHost.CreateAsync();
        await PendingGenerationProvider.GenerateAsync(h.Db, h.Entry, h.Actor.Id, h.Project.Id);
        h.FailFactory = factory;
        h.Agent.Script = () => throw new InvalidOperationException("HTTP_SECRET_SENTINEL provider response");
        using var response = await h.PostAsync(h.Plan, "{}");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Fact provider unavailable", body);
        Assert.DoesNotContain("HTTP_SECRET_SENTINEL", body);
        Assert.DoesNotContain(h.Logs, x => x.Contains("HTTP_SECRET_SENTINEL", StringComparison.Ordinal));
        Assert.Equal("Generated", (await h.Db.TimelineFactProposals.SingleAsync()).Status);
    }

    [Theory]
    [InlineData(-1, 2, 50)]
    [InlineData(3, 2, 50)]
    [InlineData(0, 2, 0)]
    [InlineData(0, 2, 101)]
    public async Task Backfill_rejects_invalid_bounds_without_provider(int after, int through, int size)
    {
        await using var h = await TimelineFactHttpHost.CreateAsync();
        using var response = await h.PostAsync(h.Backfill, $"{{\"afterEntryNumber\":{after},\"throughEntryNumber\":{through},\"pageSize\":{size}}}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await h.Db.TimelineFactJobs.ToListAsync());
        Assert.Equal(0, h.AgentConstructions);
    }

    [Fact]
    public async Task Backfill_is_bounded_resumable_idempotent_enqueue_only()
    {
        await using var h = await TimelineFactHttpHost.CreateAsync();
        h.FailFactory = true;
        h.Db.Add(new TimelineEntry { TimelineId = h.Timeline.Id, AuthorId = h.Actor.Id, EntryNumber = 3, Title = "Later", Content = "Later" });
        await h.Db.SaveChangesAsync();
        for (var i = 0; i < 2; i++)
        {
            using var response = await h.PostAsync(h.Backfill, "{\"afterEntryNumber\":0,\"throughEntryNumber\":2,\"pageSize\":1}", true);
            var dto = await JsonAsync(response);
            Assert.Equal(2, dto.GetProperty("lastEntryNumber").GetInt32());
            Assert.Equal(i == 0 ? 1 : 0, dto.GetProperty("enqueued").GetInt32());
        }
        Assert.Equal(h.Entry.Id, Assert.Single(await h.Db.TimelineFactJobs.ToListAsync()).EntryId);
        Assert.Equal(0, h.AgentConstructions);
    }
}
