using System.Net;
using System.Text;
using System.Text.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.DataAccessLogic;
using ProjectTimeline.UI.WebApi;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class TimelineEntryClarificationApiTests
{
    private sealed class ClarificationAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Actor", out var actor)) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, actor.ToString()) };
            if (Request.Headers.ContainsKey("X-Test-Admin")) claims.Add(new(ClaimTypes.Role, "Admin"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }

    private sealed class UnknownLengthJson(string text) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();
    }

    private sealed class Host : IAsyncDisposable, ITimelineFactProvider
    {
        private readonly string database = Guid.NewGuid().ToString();
        public DbContextOptions<ProjectTimelineDbContext> Options => new DbContextOptionsBuilder<ProjectTimelineDbContext>()
            .UseInMemoryDatabase(database).Options;
        public ProjectTimelineDbContext Db() => new(Options);
        public User Actor = new() { Email = "clarification-api@example.test", DisplayName = "Writer", PasswordHash = "test" };
        public Project Project = new() { Code = "P", Name = "Project" };
        public Timeline Timeline = new() { Code = "T", Name = "Timeline" };
        public TimelineEntry Entry = null!;
        public WebApplication App = null!;
        public HttpClient Client = null!;
        public int Calls;
        public TimelineFactExtraction? Seen;
        public Func<TimelineFactExtraction, TimelineFactExtractionOutcome>? OnExtract;
        public FactReviewFakeAgent Agent = new();
        public TimelineFactExtractionOutcome Outcome = new([], [new("owner", "Who owns delivery?"), new("date", "When is delivery?")], []);
        public string Path => $"/api/entries/{Entry.Id}/fact-clarifications";
        public string ExtractionModel => "offline-clarification";
        public Task<IReadOnlyList<TimelineFactCandidate>> ExtractAsync(TimelineFactExtraction source, CancellationToken ct) =>
            throw new InvalidOperationException("Structured extraction must be forwarded.");
        public Task<TimelineFactExtractionOutcome> ExtractOutcomeAsync(TimelineFactExtraction source, CancellationToken ct)
        { Calls++; Seen = source.Freeze(); return Task.FromResult(OnExtract?.Invoke(source) ?? Outcome); }
        public Task<float[]> EmbedAsync(string claim, CancellationToken ct) => throw new InvalidOperationException("No embeddings.");
        public static async Task<Host> Create()
        {
            var h = new Host();
            await using (var db = h.Db())
            {
                h.Project.OwnerId = h.Actor.Id; h.Timeline.ProjectId = h.Project.Id;
                db.AddRange(h.Actor, h.Project, h.Timeline); await db.SaveChangesAsync();
                h.Entry = await new TimelineService(db).AddEntryAsync(h.Timeline.Id, h.Actor.Id, "Delivery", "Delivery is planned.", default);
            }
            var b = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            b.WebHost.UseTestServer();
            b.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, ClarificationAuthentication>("Test", _ => { });
            b.Services.AddAuthorization(); b.Services.AddRouting();
            b.Services.AddSingleton(h.Options); b.Services.AddScoped(_ => h.Db());
            b.Services.AddTimelineFacts(new ConfigurationBuilder().Build());
            b.Services.AddScoped<ITimelineFactProvider>(_ => new Provider(h));
            b.Services.AddScoped(sp => new TimelineFactApplication(sp.GetRequiredService<ProjectTimelineDbContext>(), () => h.Agent,
                sp.GetRequiredService<TimelineFactProcessing>()));
            h.App = b.Build();
            h.App.UseRouting(); h.App.UseAuthentication(); h.App.UseAuthorization();
            h.App.UseTimelineFactBodyBounds(); h.App.MapTimelineFacts(); await h.App.StartAsync();
            h.Client = h.App.GetTestClient(); h.Client.DefaultRequestHeaders.Add("X-Test-Actor", h.Actor.Id.ToString());
            return h;
        }
        public DbContext Raw()
        {
            using var db = Db();
            return new(new DbContextOptionsBuilder().UseInMemoryDatabase(database).UseModel(db.Model).Options);
        }
        public Task<HttpResponseMessage> Post(string path, object body) => Client.PostAsync(path,
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        public async Task<JsonElement> Read()
        { using var response = await Client.GetAsync(Path); return await Json(response); }
        public async Task<(Guid Generation, Guid[] Questions)> Pause()
        {
            using var response = await Post($"/api/entries/{Entry.Id}/fact-generation", new { });
            var state = await Json(response);
            Assert.Equal("AwaitingClarification", state.GetProperty("lifecycleState").GetString());
            Assert.False(state.GetProperty("canGenerate").GetBoolean());
            Assert.False(state.GetProperty("canRegenerate").GetBoolean());
            var read = await Read();
            Assert.False(read.GetProperty("canResume").GetBoolean());
            return (read.GetProperty("generationId").GetGuid(), read.GetProperty("current").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToArray());
        }
        public Task<HttpResponseMessage> Answer(Guid generation, Guid question, string answer) =>
            Post(Path + $"/{question}/answer", new { expectedGenerationId = generation, answer });
        public async ValueTask DisposeAsync() { Client.Dispose(); await App.DisposeAsync(); }
    }
    // Do not register the disposable host itself as a scoped provider: scope disposal would abort its HTTP client.
    private sealed class Provider(Host host) : ITimelineFactProvider
    {
        public string ExtractionModel => host.ExtractionModel;
        public Task<IReadOnlyList<TimelineFactCandidate>> ExtractAsync(TimelineFactExtraction source, CancellationToken ct) => host.ExtractAsync(source, ct);
        public Task<TimelineFactExtractionOutcome> ExtractOutcomeAsync(TimelineFactExtraction source, CancellationToken ct) => host.ExtractOutcomeAsync(source, ct);
        public Task<float[]> EmbedAsync(string claim, CancellationToken ct) => host.EmbedAsync(claim, ct);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Current_questions_answers_and_explicit_resume_use_server_identity_and_frozen_carry()
    {
        await using var h = await Host.Create();
        // This read is the first RED boundary: no route existed before C3b.
        var initial = await h.Read(); Assert.Empty(initial.GetProperty("current").EnumerateArray());
        var (generation, questions) = await h.Pause(); Assert.Equal(1, h.Calls);
        using (var unanswered = await h.Post(h.Path + "/resume", new { expectedGenerationId = generation }))
            Assert.Equal(HttpStatusCode.Conflict, unanswered.StatusCode);
        using (var answer = await h.Answer(generation, questions[0], "Friday")) await Json(answer);
        Assert.Equal(1, h.Calls);
        using (var partial = await h.Post(h.Path + "/resume", new { expectedGenerationId = generation }))
            Assert.Equal(HttpStatusCode.Conflict, partial.StatusCode);
        using (var plan = await h.Post($"/api/entries/{h.Entry.Id}/fact-proposals", new { }))
            Assert.Equal(HttpStatusCode.Conflict, plan.StatusCode);
        using (var generate = await h.Post($"/api/entries/{h.Entry.Id}/fact-generation", new { regenerate = true, expectedGenerationId = generation }))
            Assert.Equal(HttpStatusCode.Conflict, generate.StatusCode);
        using (var answer = await h.Answer(generation, questions[1], "Alice")) await Json(answer);
        var ready = await h.Read(); Assert.True(ready.GetProperty("canResume").GetBoolean());
        Assert.All(ready.GetProperty("current").EnumerateArray(), x =>
        { Assert.False(x.GetProperty("canAnswer").GetBoolean()); Assert.Equal(h.Actor.Id, x.GetProperty("answeredById").GetGuid()); });
        Assert.Equal(1, h.Calls);
        h.Outcome = new([], [], []);
        using (var resume = await h.Post(h.Path + "/resume", new { expectedGenerationId = generation }))
            Assert.Equal("Empty", (await Json(resume)).GetProperty("lifecycleState").GetString());
        Assert.Equal(2, h.Calls); Assert.Equal(2, h.Seen!.Input!.Answers.Count);
        var carried = await h.Read();
        Assert.NotEqual(generation, carried.GetProperty("generationId").GetGuid());
        Assert.Equal(2, carried.GetProperty("current").GetArrayLength());
        Assert.All(carried.GetProperty("current").EnumerateArray(), x => Assert.Equal("CarriedAnswer", x.GetProperty("binding").GetString()));
        using (var stale = await h.Post(h.Path + "/resume", new { expectedGenerationId = generation }))
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        await using var db = h.Db(); Assert.Equal(h.Entry.Content, (await db.TimelineEntries.SingleAsync()).Content);
        Assert.Empty(await db.TimelineFacts.ToListAsync());
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("inactive", HttpStatusCode.Forbidden)]
    [InlineData("foreign", HttpStatusCode.Forbidden)]
    [InlineData("reviewer", HttpStatusCode.OK)]
    public async Task Scope_and_active_account_are_authoritative(string identity, HttpStatusCode readCode)
    {
        await using var h = await Host.Create(); var (generation, questions) = await h.Pause();
        h.Client.DefaultRequestHeaders.Remove("X-Test-Actor");
        await using (var db = h.Db())
        {
            if (identity == "inactive")
            { var actor = await db.Users.SingleAsync(); actor.IsActive = false; await db.SaveChangesAsync(); h.Client.DefaultRequestHeaders.Add("X-Test-Actor", actor.Id.ToString()); }
            if (identity is "foreign" or "reviewer")
            {
                var user = new User { Email = "other@example.test", DisplayName = "Other", PasswordHash = "test" };
                db.Add(user);
                if (identity == "reviewer") db.ProjectParticipants.Add(new() { ProjectId = h.Project.Id, UserId = user.Id, ProjectRoleName = "Reviewer" });
                await db.SaveChangesAsync(); h.Client.DefaultRequestHeaders.Add("X-Test-Actor", user.Id.ToString());
            }
        }
        using var read = await h.Client.GetAsync(h.Path); Assert.Equal(readCode, read.StatusCode);
        if (identity == "reviewer")
        { var state = await Json(read); Assert.False(state.GetProperty("canAnswer").GetBoolean()); Assert.False(state.GetProperty("canResume").GetBoolean()); }
        using var answer = await h.Answer(generation, questions[0], "Actual answer");
        using var resume = await h.Post(h.Path + "/resume", new { expectedGenerationId = generation });
        var denied = identity == "anonymous" ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
        Assert.Equal(denied, answer.StatusCode); Assert.Equal(denied, resume.StatusCode); Assert.Equal(1, h.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Administrator_claim_still_requires_active_persisted_account_and_existing_scope(bool inactive)
    {
        await using var h = await Host.Create(); var (generation, questions) = await h.Pause();
        var role = new UserRole { RoleName = "Admin" };
        var admin = new User { Email = "admin@example.test", DisplayName = "Admin", PasswordHash = "test", IDUserRole = role.Id, IsActive = !inactive };
        await using (var db = h.Db()) { db.AddRange(role, admin); await db.SaveChangesAsync(); }
        h.Client.DefaultRequestHeaders.Remove("X-Test-Actor");
        h.Client.DefaultRequestHeaders.Add("X-Test-Actor", admin.Id.ToString()); h.Client.DefaultRequestHeaders.Add("X-Test-Admin", "true");
        using var read = await h.Client.GetAsync(h.Path); Assert.Equal(inactive ? HttpStatusCode.Forbidden : HttpStatusCode.OK, read.StatusCode);
        using var answer = await h.Answer(generation, questions[0], "Actual admin answer");
        Assert.Equal(inactive ? HttpStatusCode.Forbidden : HttpStatusCode.OK, answer.StatusCode);
        using var foreign = await h.Client.GetAsync($"/api/entries/{Guid.NewGuid()}/fact-clarifications");
        Assert.Equal(inactive ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(1, h.Calls);
    }

    [Theory]
    [InlineData("empty", HttpStatusCode.BadRequest)]
    [InlineData("long", HttpStatusCode.BadRequest)]
    [InlineData("foreign", HttpStatusCode.NotFound)]
    [InlineData("generation", HttpStatusCode.Conflict)]
    public async Task Invalid_answer_requests_fail_closed(string kind, HttpStatusCode expected)
    {
        await using var h = await Host.Create(); var (generation, questions) = await h.Pause();
        using var response = await h.Answer(kind == "generation" ? Guid.NewGuid() : generation,
            kind == "foreign" ? Guid.NewGuid() : questions[0], kind == "empty" ? " " : kind == "long" ? new string('x', 4001) : "Alice");
        Assert.Equal(expected, response.StatusCode); Assert.Equal(1, h.Calls);
        await using var db = h.Db(); Assert.All(await db.TimelineEntryClarifications.ToListAsync(), x => Assert.Null(x.Answer));
    }

    [Theory]
    [InlineData("answer", "author")]
    [InlineData("answer", "sourceFingerprint")]
    [InlineData("answer", "ready")]
    [InlineData("resume", "snapshot")]
    public async Task Strict_bodies_reject_forged_fields(string action, string field)
    {
        await using var h = await Host.Create(); var (generation, questions) = await h.Pause();
        using var response = await h.Post(h.Path + (action == "answer" ? $"/{questions[0]}/answer" : "/resume"),
            new Dictionary<string, object> { ["expectedGenerationId"] = generation, [field] = "forged", [action == "answer" ? "answer" : "support"] = "Alice" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(1, h.Calls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    public async Task Malformed_clarification_requests_are_bad_requests(string json)
    {
        await using var h = await Host.Create();
        using var response = await h.Client.PostAsync(h.Path + "/resume", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(0, h.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Clarification_routes_retain_body_bounds(bool chunked)
    {
        await using var h = await Host.Create();
        using HttpContent body = chunked ? new UnknownLengthJson(new string('x', 65537)) : new StringContent(new string('x', 65537));
        body.Headers.ContentType = new("application/json");
        if (chunked) h.Client.DefaultRequestHeaders.TransferEncodingChunked = true;
        using var response = await h.Client.PostAsync(h.Path + "/resume", body);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode); Assert.Equal(0, h.Calls);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("source")]
    [InlineData("answer")]
    public async Task Corrupt_current_proof_returns_conflict_not_empty_success(string kind)
    {
        await using var h = await Host.Create(); var (generation, questions) = await h.Pause();
        using (var answer = await h.Answer(generation, questions[0], "Friday")) await Json(answer);
        await using (var raw = h.Raw())
        {
            var job = await raw.Set<TimelineFactJob>().SingleAsync(x => x.Id == generation);
            if (kind == "input") job.ExtractionInputJson = "{}";
            if (kind == "source") job.SourceFingerprint = new string('A', 64);
            if (kind == "answer") (await raw.Set<TimelineEntryClarification>().SingleAsync(x => x.Id == questions[0])).AnsweredById = null;
            await raw.SaveChangesAsync();
        }
        using var read = await h.Client.GetAsync(h.Path); Assert.Equal(HttpStatusCode.Conflict, read.StatusCode);
        using var write = await h.Answer(generation, questions[1], "Alice"); Assert.Equal(HttpStatusCode.Conflict, write.StatusCode);
        using var resume = await h.Post(h.Path + "/resume", new { expectedGenerationId = generation });
        Assert.Equal(HttpStatusCode.Conflict, resume.StatusCode); Assert.Equal(1, h.Calls);
        using var facts = await h.Client.GetAsync($"/api/entries/{h.Entry.Id}/facts");
        var state = await Json(facts); Assert.True(state.GetProperty("refreshRequired").GetBoolean());
    }

    [Fact]
    public async Task Concurrent_human_requests_never_replace_the_winner_and_fresh_read_recovers()
    {
        await using var h = await Host.Create(); var (generation, questions) = await h.Pause();
        var responses = await Task.WhenAll(h.Answer(generation, questions[0], "Friday"), h.Answer(generation, questions[0], "Monday"));
        try
        {
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
            var read = await h.Read();
            Assert.Single(read.GetProperty("current").EnumerateArray(), x => x.GetProperty("answer").ValueKind == JsonValueKind.String);
            Assert.Equal(1, h.Calls);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task Bounded_history_cannot_hide_current_questions()
    {
        await using var h = await Host.Create(); var (generation, questions) = await h.Pause();
        // Raw historical padding is not trusted current evidence and never participates in the job snapshot.
        await using (var raw = h.Raw())
        {
            for (var i = 0; i < 101; i++) raw.Add(new TimelineEntryClarification { EntryId = h.Entry.Id,
                GenerationId = Guid.NewGuid(), QuestionKey = "old-" + i, Question = "Historical question?",
                SourceFingerprint = TimelineFactEvidence.Fingerprint(h.Entry) });
            await raw.SaveChangesAsync();
        }
        var read = await h.Read(); Assert.True(read.GetProperty("truncated").GetBoolean());
        Assert.Equal(100, read.GetProperty("history").GetArrayLength());
        Assert.Equal(2, read.GetProperty("current").GetArrayLength());
        Assert.All(read.GetProperty("current").EnumerateArray(), x => Assert.Equal(generation, x.GetProperty("generationId").GetGuid()));
        using var answer = await h.Answer(generation, questions[0], "Friday"); await Json(answer);
    }

    [Fact]
    public async Task Actual_answers_remain_visible_after_normal_review_acceptance_and_lock_further_writes()
    {
        await using var h = await Host.Create(); var (generation, questions) = await h.Pause();
        foreach (var id in questions) { using var answer = await h.Answer(generation, id, "Alice owns delivery."); await Json(answer); }
        h.OnExtract = source =>
        {
            var span = TimelineFactSourceEvidence.Create(source).First(x => x.SourceKind == "Clarification");
            return new([new("Alice owns delivery.", "alice", "Person", span.Text, "Clarification")], [], [new(0, span.Id, [span])]);
        };
        using (var resume = await h.Post(h.Path + "/resume", new { expectedGenerationId = generation }))
            Assert.Equal("Pending", (await Json(resume)).GetProperty("lifecycleState").GetString());
        h.Agent.Result = new() { Additions = [new("Alice owns delivery.", "alice", "Person", "Alice owns delivery.", "Clarification")] };
        using var plan = await h.Post($"/api/entries/{h.Entry.Id}/fact-proposals", new { });
        var proposal = (await Json(plan)).GetProperty("proposalId").GetGuid();
        using var accept = await h.Client.PostAsync($"/api/entries/{h.Entry.Id}/fact-proposals/{proposal}/accept", null); await Json(accept);
        var read = await h.Read(); Assert.Equal("Reconciled", read.GetProperty("lifecycleState").GetString());
        Assert.Equal(2, read.GetProperty("current").GetArrayLength());
        Assert.All(read.GetProperty("current").EnumerateArray(), x =>
        { Assert.Equal("Alice owns delivery.", x.GetProperty("answer").GetString()); Assert.False(x.GetProperty("canAnswer").GetBoolean()); });
        Assert.False(read.GetProperty("canResume").GetBoolean()); Assert.Equal(2, h.Calls);
        using var locked = await h.Answer(generation, questions[0], "Changed"); Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode);
        await using var db = h.Db(); var fact = await db.TimelineFacts.SingleAsync();
        Assert.True(fact.Valid); Assert.Equal("Clarification", fact.SourceKind);
        Assert.Equal(h.Entry.Content, (await db.TimelineEntries.SingleAsync()).Content);
    }

    [Fact]
    public async Task Append_once_answers_and_source_edit_restore_preserve_read_only_history()
    {
        await using var h = await Host.Create(); var (generation, questions) = await h.Pause();
        using (var answer = await h.Answer(generation, questions[0], "Friday")) await Json(answer);
        using (var overwrite = await h.Answer(generation, questions[0], "Monday")) Assert.Equal(HttpStatusCode.Conflict, overwrite.StatusCode);
        await using (var db = h.Db())
        {
            var source = await db.TimelineEntries.SingleAsync(); source.Content = "Edited"; await db.SaveChangesAsync();
            source.Content = h.Entry.Content; await db.SaveChangesAsync();
        }
        var history = await h.Read(); Assert.Empty(history.GetProperty("current").EnumerateArray());
        Assert.Equal(2, history.GetProperty("history").GetArrayLength());
        Assert.Contains(history.GetProperty("history").EnumerateArray(), x => x.GetProperty("answer").GetString() == "Friday");
        Assert.All(history.GetProperty("history").EnumerateArray(), x => Assert.False(x.GetProperty("canAnswer").GetBoolean()));
        using var stale = await h.Answer(generation, questions[1], "Alice"); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }
}
