using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.AgentModels;
using ProjectTimeline.BusinessLogic.Agents.Framework;
using ProjectTimeline.BusinessLogic.Agents.Models;
using ProjectTimeline.BusinessLogic.Agents.Tracking;
using ProjectTimeline.BusinessLogic.Agents.Tools;
using ProjectTimeline.DataAccessLogic;
using ProjectTimeline.UI.WebApi;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class TimelineFactInformationTests
{
    private static TimelineFactInformationSearch Search(TimelineFactReviewFixture f, Guid? actor = null, bool admin = false) =>
        new(f.Db, new(f.Project.Id, f.Timeline.Id), actor ?? f.Actor.Id, admin);

    [Fact]
    public async Task Latest_valid_search_is_timeline_wide_while_review_anchor_remains_chronological()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var first = await f.AcceptAsync("Alice owns delivery.", "alice");
        var later = new TimelineEntry { TimelineId = f.Timeline.Id, AuthorId = f.Actor.Id,
            EntryNumber = 10, Title = "Later", Content = "Alice owns release." };
        f.Db.Add(later); await f.Db.SaveChangesAsync();
        var scope = new TimelineFactScope(f.Project.Id, f.Timeline.Id, later.Id);
        var revision = await f.Db.Timelines.Select(x => x.FactRevision).SingleAsync();
        var service = new TimelineFactService(f.Db);
        var proposal = await service.PrepareAsync(scope, f.Actor.Id, false, revision,
            new([new("Alice owns release.", "alice-release", "Person", "Alice owns release.")], [], []));
        await service.AcceptAsync(scope, proposal.Id, f.Actor.Id, false);
        var found = await Search(f).SearchAsync("ALIC");
        Assert.Equal(2, found.Facts.Count);
        Assert.Equal(later.Id, found.Facts[0].EntryId);
        Assert.Equal(proposal.Id, found.Facts[0].CreationReviewId);
        Assert.Equal(first.Id, Assert.Single(await f.Search.SearchAsync("alice")).Id);
        Assert.Equal(2, found.Coverage.Entries);
        Assert.Equal(2, found.Coverage.EntriesWithoutCompletedExtraction);
        Assert.Contains(found.Coverage.Warnings, x => x.Contains("not exhaustive"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pending_invalid_and_foreign_source_or_review_never_become_evidence(bool foreignProject)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var accepted = await f.AcceptAsync("Alice owns delivery.", "alice");
        await f.PrepareAsync("Bob owns testing.", "bob");
        Assert.Empty((await Search(f).SearchAsync("bob")).Facts);
        Assert.Equal(1, (await Search(f).SearchAsync("alice")).Coverage.PendingReviews);
        // Corrupt persisted provenance simulation only; normal fixtures use the actual guarded acceptance service.
        await using var raw = f.Unguarded();
        var fact = await raw.Set<TimelineFact>().SingleAsync();
        var otherProject = new Project { OwnerId = f.Actor.Id, Code = "O", Name = "Other" };
        var otherTimeline = new Timeline { ProjectId = foreignProject ? otherProject.Id : f.Project.Id, Code = "O", Name = "Other" };
        var other = new TimelineEntry { TimelineId = otherTimeline.Id, AuthorId = f.Actor.Id,
            EntryNumber = 1, Title = "Other", Content = "Alice owns delivery." };
        raw.AddRange(otherProject, otherTimeline, other); await raw.SaveChangesAsync();
        var review = await raw.Set<TimelineFactProposal>().SingleAsync(x => x.Id == fact.CreationReviewId);
        foreach (var kind in new[] { "invalid", "pending", "source", "review" })
        {
            fact.Valid = kind != "invalid";
            review.Status = kind == "pending" ? "Pending" : "Accepted";
            fact.EntryId = kind == "source" ? other.Id : f.Entry.Id;
            review.EntryId = kind == "review" ? other.Id : f.Entry.Id;
            await raw.SaveChangesAsync();
            Assert.Empty((await Search(f).SearchAsync("alice")).Facts);
        }
        Assert.NotEqual(Guid.Empty, accepted.Id);
    }

    [Fact]
    public async Task Bounds_coverage_and_SQL_filter_before_limit_without_raw_or_embedding_projection()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await f.AcceptAsync("Alice owns delivery.", "alice");
        await f.AcceptAsync("Bob owns testing.", "bob");
        var result = await Search(f).SearchAsync("Person", 1);
        Assert.Single(result.Facts); Assert.True(result.Coverage.Truncated);
        Assert.Equal(2, result.Coverage.AcceptedValidFacts);
        Assert.Contains(result.Coverage.Warnings, x => x.Contains("truncated"));
        Assert.Single((await Search(f).SearchAsync("deliv", 1)).Facts);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Search(f).SearchAsync("alice", 51));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Search(f).SearchAsync("alice", 0));
        using var db = new ProjectTimelineDbContext(new DbContextOptionsBuilder<ProjectTimelineDbContext>()
            .UseNpgsql("Host=localhost;Database=offline_translation_only", o => o.UseVector()).Options);
        var search = new TimelineFactInformationSearch(db, new(f.Project.Id, f.Timeline.Id), f.Actor.Id, false);
        var method = typeof(TimelineFactInformationSearch).GetMethod("SearchQuery", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var query = (IQueryable<TimelineFactSearchHit>)method.Invoke(search, [new[] { "deliv" }, 2])!;
        var sql = query.ToQueryString();
        Assert.Contains("LIMIT", sql); Assert.Contains("lower(", sql); Assert.Contains("Accepted", sql);
        Assert.DoesNotContain("\"Content\"", sql); Assert.DoesNotContain("Embedding", sql);
        Assert.DoesNotContain("EntryNumber\" <=", sql);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("!?")]
    [InlineData("x")]
    public async Task Invalid_question_never_constructs_agent(string question)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await f.AcceptAsync("Alice owns delivery.", "alice");
        var calls = 0;
        var agent = new SearchEntriesAgent(f.Db, new(f.Project.Id, f.Timeline.Id), f.Actor.Id, false,
            () => { calls++; throw new Exception("must not construct"); });
        await Assert.ThrowsAsync<ArgumentException>(() => agent.RunAsync(question));
        await Assert.ThrowsAsync<ArgumentException>(() => agent.RunAsync(new string('a', 301)));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Empty_failed_processing_and_unauthorized_never_construct_agents_or_fallback_to_raw_content()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        f.Db.TimelineFactJobs.Add(new() { EntryId = f.Entry.Id, Kind = "Extraction", State = "Failed", WorkKey = "test",
            SourceFingerprint = TimelineFactEvidence.Fingerprint(f.Entry) });
        await f.Db.SaveChangesAsync();
        var calls = 0;
        var agent = new SearchEntriesAgent(f.Db, new(f.Project.Id, f.Timeline.Id), f.Actor.Id, false,
            () => { calls++; throw new Exception("must not construct"); });
        var response = await agent.RunAsync("Alice");
        Assert.Empty(response.Facts); Assert.Empty(response.Citations);
        Assert.Contains("No accepted matching facts", response.Content);
        Assert.Equal(1, response.Coverage.EntriesWithoutCompletedExtraction);
        f.Actor.IsActive = false; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => agent.RunAsync("Alice"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new SearchEntriesAgent(f.Db,
            new(f.Project.Id, f.Timeline.Id), Guid.NewGuid(), true, () => { calls++; return new InformationFakeAgent(); }).RunAsync("Alice"));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Synthesis_preserves_question_uses_only_retrieved_facts_and_returns_citations_without_mutation()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        var fake = new InformationFakeAgent { Result = new() { Content = "Alice owns delivery.", CitedFactIds = [fact.Id] } };
        var before = await f.Db.Timelines.Select(x => x.FactRevision).SingleAsync();
        const string question = "  What does ALICE own?  ";
        var result = await new SearchEntriesAgent(f.Db, new(f.Project.Id, f.Timeline.Id), f.Actor.Id, false, () => fake).RunAsync(question);
        Assert.Equal(question, JsonDocument.Parse(fake.Message!).RootElement.GetProperty("question").GetString());
        Assert.DoesNotContain("Bob owns testing", fake.Message);
        Assert.Contains("untrusted DATA", fake.Instructions); Assert.Empty(fake.Tools);
        var citation = Assert.Single(result.Citations);
        Assert.Equal(fact.Id, citation.FactId); Assert.Equal(f.Entry.Id, citation.EntryId);
        Assert.Equal(fact.CreationReviewId, citation.CreationReviewId);
        Assert.Equal(before, await f.Db.Timelines.Select(x => x.FactRevision).SingleAsync());
        Assert.Single(await f.Db.TimelineFacts.ToListAsync());
        Assert.Equal("Alice owns delivery.\nBob owns testing.  ", (await f.Db.TimelineEntries.SingleAsync()).Content);
    }

    [Theory]
    [InlineData("unsupported")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("null")]
    [InlineData("malformed")]
    [InlineData("blank")]
    [InlineData("oversized")]
    public async Task Malformed_or_unsupported_citations_are_rejected(string kind)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        var fake = new InformationFakeAgent { Result = new() { Content = "Answer", CitedFactIds = [fact.Id] } };
        if (kind == "unsupported") fake.Result.CitedFactIds = [Guid.NewGuid()];
        if (kind == "empty") fake.Result.CitedFactIds = [];
        if (kind == "duplicate") fake.Result.CitedFactIds = [fact.Id, fact.Id];
        if (kind == "blank") fake.Result.Content = " ";
        if (kind == "oversized") fake.Result.Content = new string('a', 12001);
        if (kind == "null") fake.Json = "null";
        if (kind == "malformed") fake.Json = "{\"CitedFactIds\":[\"not-an-id\"]}";
        await Assert.ThrowsAsync<TimelineFactInformationSynthesisException>(() => new SearchEntriesAgent(f.Db,
            new(f.Project.Id, f.Timeline.Id), f.Actor.Id, false, () => fake).RunAsync("Alice"));
    }

    [Fact]
    public async Task Reauthorization_and_revision_change_during_synthesis_prevent_stale_answer()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        var fake = new InformationFakeAgent { Result = new() { Content = "Answer", CitedFactIds = [fact.Id] } };
        fake.Script = async () => { await f.AcceptAsync("Bob owns testing.", "bob"); };
        var agent = new SearchEntriesAgent(f.Db, new(f.Project.Id, f.Timeline.Id), f.Actor.Id, false, () => fake);
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => agent.RunAsync("Alice"));
        fake.Script = async () => { f.Actor.IsActive = false; await f.Db.SaveChangesAsync(); };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => agent.RunAsync("Alice"));
    }

    [Fact]
    public async Task Cancellation_after_tokenless_call_discards_result_and_detaches_tracker()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        using var cts = new CancellationTokenSource();
        var fake = new InformationFakeAgent { Result = new() { Content = "Answer", CitedFactIds = [fact.Id] }, EmitEvent = true };
        fake.Script = () => { cts.Cancel(); return Task.CompletedTask; };
        var tracker = new FunctionCallTracker();
        var agent = new SearchEntriesAgent(f.Db, new(f.Project.Id, f.Timeline.Id), f.Actor.Id, false, () => fake, tracker);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.RunAsync("Alice", cts.Token));
        Assert.NotNull(fake.Message); // Cancellation did not magically stop the tokenless SDK invocation.
        Assert.Single(tracker.Tools);
        Assert.Equal(0, fake.HandlerCount);
    }

    [Fact]
    public async Task Latest_extraction_state_not_historical_completion_controls_coverage()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var job = new TimelineFactJob { EntryId = f.Entry.Id, WorkKey = "test", Kind = "Extraction", State = "Completed",
            SourceFingerprint = TimelineFactEvidence.Fingerprint(f.Entry) };
        // Historical completed jobs predate direct provenance; import fixture state through the raw context.
        await using (var legacy = f.Unguarded()) { legacy.Add(job); await legacy.SaveChangesAsync(); }
        Assert.Equal(0, (await Search(f).SearchAsync("alice")).Coverage.EntriesWithoutCompletedExtraction);
        f.Db.Add(new TimelineFactJob { EntryId = f.Entry.Id, WorkKey = "test-newer", Kind = "Extraction", State = "Failed",
            SourceFingerprint = TimelineFactEvidence.Fingerprint(f.Entry) });
        await f.Db.SaveChangesAsync();
        Assert.Equal(1, (await Search(f).SearchAsync("alice")).Coverage.EntriesWithoutCompletedExtraction);
        Assert.Empty((await Search(f).SearchAsync("alice")).Facts);
    }

    [Fact]
    public async Task Trusted_scope_rejects_project_timeline_mismatch_and_current_database_admin_is_authoritative()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => new TimelineFactInformationSearch(f.Db,
            new(f.Project.Id, Guid.NewGuid()), f.Actor.Id, false).SearchAsync("alice"));
        var other = new Project { Code = "O", Name = "Other", OwnerId = f.Actor.Id };
        f.Db.Add(other); await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => new TimelineFactInformationSearch(f.Db,
            new(other.Id, f.Timeline.Id), f.Actor.Id, false).SearchAsync("alice"));
        var adminRole = new UserRole { RoleName = "Admin" };
        var admin = new User { Email = "admin@example.test", PasswordHash = "test", IDUserRole = adminRole.Id, UserRole = adminRole };
        f.Db.AddRange(adminRole, admin); await f.Db.SaveChangesAsync();
        // No caller-controlled admin flag exists on the application API.
        var application = new TimelineFactInformationApplication(f.Db, () => throw new Exception("empty facts must not construct"));
        Assert.Empty((await application.SearchAsync(f.Timeline.Id, admin.Id, "alice")).Facts);
        adminRole.RoleName = "User"; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => application.SearchAsync(f.Timeline.Id, admin.Id, "alice"));
    }

    private static ProjectTimelineDbContext SeparateContext(TimelineFactReviewFixture f) =>
        new((DbContextOptions<ProjectTimelineDbContext>)f.Db.GetService<IDbContextOptions>());

    [Theory]
    [InlineData("demoted", false)]
    [InlineData("deactivated", false)]
    [InlineData("removed", false)]
    [InlineData("memberRemoved", false)]
    [InlineData("owner", true)]
    [InlineData("member", true)]
    public async Task In_flight_persisted_access_change_controls_HTTP_evidence_release(string change, bool allowed)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        var adminRole = new UserRole { RoleName = "Admin" };
        var userRole = new UserRole { RoleName = "User" };
        var admin = new User { Email = "flight@example.test", PasswordHash = "test", UserRole = adminRole, IDUserRole = adminRole.Id };
        f.Db.AddRange(adminRole, userRole, admin);
        if (change == "owner") f.Project.OwnerId = admin.Id;
        if (change is "member" or "memberRemoved") f.Db.Add(new ProjectParticipant { ProjectId = f.Project.Id, UserId = admin.Id });
        await f.Db.SaveChangesAsync();
        var runs = 0;
        var fake = new InformationFakeAgent { Result = new() { Content = "PRIVATE_ANSWER", CitedFactIds = [fact.Id] } };
        fake.Script = async () =>
        {
            runs++;
            // This is a real guarded context sharing only the persisted store, not tracked identities.
            await using var separate = SeparateContext(f);
            var current = await separate.Users.SingleAsync(x => x.Id == admin.Id);
            if (change == "removed") separate.Users.Remove(current);
            else if (change == "deactivated") current.IsActive = false;
            else current.IDUserRole = userRole.Id;
            if (change == "memberRemoved") separate.ProjectParticipants.Remove(await separate.ProjectParticipants.SingleAsync(x => x.UserId == admin.Id));
            await separate.SaveChangesAsync();
        };
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting(); builder.Services.AddAuthorization();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthentication>("Test", _ => { });
        builder.Services.AddSingleton(f.Db);
        builder.Services.AddScoped(sp => new TimelineFactInformationApplication(sp.GetRequiredService<ProjectTimelineDbContext>(), () => fake));
        await using var app = builder.Build();
        app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.MapTimelineFactInformation();
        await app.StartAsync(); using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Actor", admin.Id.ToString());
        var response = await client.GetAsync($"/api/timelines/{f.Timeline.Id}/entry-search?text=Alice");
        Assert.Equal(1, runs);
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        if (allowed)
        {
            using var body = JsonDocument.Parse(json);
            Assert.Equal("PRIVATE_ANSWER", body.RootElement.GetProperty("content").GetString());
            Assert.Equal(fact.Id, body.RootElement.GetProperty("citations")[0].GetProperty("factId").GetGuid());
        }
        else
        {
            Assert.DoesNotContain("PRIVATE_ANSWER", json);
            Assert.DoesNotContain("Alice owns delivery", json);
            Assert.DoesNotContain(fact.Id.ToString(), json);
            Assert.DoesNotContain("coverage", json);
        }
        // Original request context intentionally retains the stale admin identity.
        Assert.True(admin.IsActive);
        Assert.Equal(adminRole.Id, admin.IDUserRole);
    }

    [Fact]
    public async Task Direct_search_and_tool_true_flag_cannot_grant_unrelated_nonadmin_access()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await f.AcceptAsync("Alice owns delivery.", "alice");
        var role = new UserRole { RoleName = "User" };
        var outsider = new User { Email = "flag@example.test", PasswordHash = "test", UserRole = role, IDUserRole = role.Id };
        f.Db.AddRange(role, outsider); await f.Db.SaveChangesAsync();
        var scope = new TimelineFactInformationScope(f.Project.Id, f.Timeline.Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new TimelineFactInformationSearch(f.Db, scope, outsider.Id, true).SearchAsync("Alice"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new SearchEntries(f.Db, scope, outsider.Id, true).search_entries("Alice"));
        var calls = 0;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new SearchEntriesAgent(f.Db, scope, outsider.Id, true,
            () => { calls++; return new InformationFakeAgent(); }).RunAsync("Alice"));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Access_revoked_during_lazy_construction_is_rechecked_before_model_receives_evidence()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await f.AcceptAsync("Alice owns delivery.", "alice");
        var adminRole = new UserRole { RoleName = "Admin" };
        var userRole = new UserRole { RoleName = "User" };
        var admin = new User { Email = "before@example.test", PasswordHash = "test", IDUserRole = adminRole.Id, UserRole = adminRole };
        f.Db.AddRange(adminRole, userRole, admin); await f.Db.SaveChangesAsync();
        var fake = new InformationFakeAgent();
        var tracker = new FunctionCallTracker();
        var agent = new SearchEntriesAgent(f.Db, new(f.Project.Id, f.Timeline.Id), admin.Id, true, () =>
        {
            using var separate = SeparateContext(f);
            var current = separate.Users.Single(x => x.Id == admin.Id);
            current.IDUserRole = userRole.Id;
            separate.SaveChanges();
            return fake;
        }, tracker);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => agent.RunAsync("Alice"));
        Assert.Null(fake.Message);
        Assert.Empty(fake.Instructions);
        Assert.Empty(tracker.Tools);
        Assert.Equal(0, fake.HandlerCount);
    }

    [Fact]
    public async Task Actual_HTTP_contract_authorizes_before_model_uses_server_scope_and_safe_errors()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var role = new UserRole { RoleName = "User" };
        f.Actor.UserRole = role; f.Actor.IDUserRole = role.Id; f.Db.Add(role); await f.Db.SaveChangesAsync();
        var outsider = new User { Email = "outsider@example.test", PasswordHash = "test", IDUserRole = role.Id, UserRole = role };
        f.Db.Add(outsider); await f.Db.SaveChangesAsync();
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting(); builder.Services.AddAuthorization();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthentication>("Test", _ => { });
        builder.Services.AddSingleton(f.Db);
        var calls = 0;
        var fake = new InformationFakeAgent();
        var failFactory = false;
        builder.Services.AddScoped(sp => new TimelineFactInformationApplication(sp.GetRequiredService<ProjectTimelineDbContext>(), () =>
        { calls++; if (failFactory) throw new InvalidOperationException("SECRET_SENTINEL provider"); return fake; }));
        await using var app = builder.Build();
        app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.MapTimelineFactInformation();
        await app.StartAsync(); using var client = app.GetTestClient();
        var url = $"/api/timelines/{f.Timeline.Id}/entry-search?text=Alice";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(url)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-Actor", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(url)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Test-Actor"); client.DefaultRequestHeaders.Add("X-Test-Actor", outsider.Id.ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(url + "&administrator=true&actorId=" + f.Actor.Id)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Test-Actor"); client.DefaultRequestHeaders.Add("X-Test-Actor", f.Actor.Id.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/timelines/{Guid.NewGuid()}/entry-search?text=Alice")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(url.Replace("Alice", new string('a', 301)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(url.Replace("?text=Alice", ""))).StatusCode);
        var empty = await client.GetAsync(url); Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        using (var body = JsonDocument.Parse(await empty.Content.ReadAsStringAsync()))
        { Assert.Equal(0, body.RootElement.GetProperty("facts").GetArrayLength()); Assert.True(body.RootElement.TryGetProperty("coverage", out _)); }
        Assert.Equal(0, calls);
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        fake.Result = new() { Content = "Alice owns delivery.", CitedFactIds = [fact.Id] };
        // Spoofed scope/admin query fields are ignored: the authenticated server state is authoritative.
        var ok = await client.GetAsync(url + "&projectId=" + Guid.NewGuid() + "&administrator=true&actorId=" + Guid.NewGuid());
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var json = await ok.Content.ReadAsStringAsync();
        using (var body = JsonDocument.Parse(json))
        {
            Assert.Equal("Alice owns delivery.", body.RootElement.GetProperty("content").GetString());
            Assert.Equal(fact.Id, body.RootElement.GetProperty("citations")[0].GetProperty("factId").GetGuid());
            Assert.Equal(f.Entry.Id, body.RootElement.GetProperty("facts")[0].GetProperty("entryId").GetGuid());
        }
        Assert.DoesNotContain("Embedding", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", json, StringComparison.OrdinalIgnoreCase);
        fake.Result.CitedFactIds = [Guid.NewGuid()];
        Assert.Equal(HttpStatusCode.BadGateway, (await client.GetAsync(url)).StatusCode);
        failFactory = true;
        var failure = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadGateway, failure.StatusCode);
        Assert.DoesNotContain("SECRET_SENTINEL", await failure.Content.ReadAsStringAsync());
        failFactory = false; fake.Result.CitedFactIds = [fact.Id];
        fake.Script = async () => { await f.AcceptAsync("Bob owns testing.", "bob"); };
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync(url)).StatusCode);
        var before = calls;
        f.Actor.IsActive = false; await f.Db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(url)).StatusCode);
        Assert.Equal(before, calls);
    }
}

internal sealed class InformationFakeAgent : ICustomAgent
{
    public TimelineFactInformationResult Result { get; set; } = new();
    public string? Json { get; set; }
    public string? Message { get; private set; }
    public string Instructions { get; private set; } = "";
    public List<AITool> Tools { get; private set; } = [];
    public Func<Task>? Script { get; set; }
    private EventHandler<FunctionCallEventArgs>? handlers;
    public int HandlerCount { get; private set; }
    public bool EmitEvent { get; set; }
    public event EventHandler<FunctionCallEventArgs>? FunctionCalled
    {
        add { handlers += value; HandlerCount++; }
        remove { handlers -= value; HandlerCount--; }
    }
    public void Init(string agentName, string model, string instructions, List<AITool> tools)
    { Instructions = instructions; Tools = tools; }
    public Task<AgentResponse> RunAsync(string message) => throw new NotSupportedException();
    public async Task<AgentResponse<T>> RunAsync<T>(string message) where T : class
    {
        Message = message;
        if (EmitEvent) handlers?.Invoke(this, new FunctionCallEventArgs { Function = new FunctionCall { ID = "fake", Name = "fake", Executed = true } });
        if (Script is not null) await Script();
        return new AgentResponse<T>(new AgentResponse(new ChatMessage(ChatRole.Assistant, Json ?? JsonSerializer.Serialize(Result))), JsonSerializerOptions.Default);
    }
}
