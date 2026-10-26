using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents.AgentModels;
using ProjectTimeline.DataAccessLogic;
using ProjectTimeline.UI.WebApi;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class TimelineFactApiTests
{
    private sealed class BrowserCandidates(TimelineFactCandidate candidate) : ITimelineFactProvider
    {
        public string ExtractionModel => "offline-browser-fixture";
        public Task<IReadOnlyList<TimelineFactCandidate>> ExtractAsync(TimelineFactExtraction source, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TimelineFactCandidate>>([candidate]);
        public Task<float[]> EmbedAsync(string claim, CancellationToken ct) => throw new Exception("Pending generation must not embed.");
    }

    [Theory]
    [InlineData("initial")]
    [InlineData("updated")]
    public async Task Browser_fixture_candidates_and_invalidation_replay_real_generation_planner_and_acceptance(string version)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "dotnet", "ProjectTimeline.sln"))) root = root.Parent;
        Assert.NotNull(root);
        using var fixtures = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root.FullName, "dotnet", "src", "ProjectTimeline.UI.WebApi", "ClientApp", "tests", "entryFactsJourney.fixtures.json")));
        var prior = fixtures.RootElement.GetProperty("prior");
        var current = fixtures.RootElement.GetProperty(version);
        var clarification = fixtures.RootElement.GetProperty("clarification");
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var priorCandidate = prior.GetProperty("candidate").Deserialize<TimelineFactCandidate>(jsonOptions)!;
        var candidate = current.GetProperty("candidate").Deserialize<TimelineFactCandidate>(jsonOptions)!;
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        f.Entry.Title = prior.GetProperty("title").GetString()!; f.Entry.Content = prior.GetProperty("content").GetString()!;
        await f.Db.SaveChangesAsync();
        var service = new TimelineFactService(f.Db);
        var priorReview = await service.PrepareAsync(f.Scope, f.Actor.Id, false, f.Timeline.FactRevision, new([priorCandidate], [], []));
        await service.AcceptAsync(f.Scope, priorReview.Id, f.Actor.Id, false);
        var target = await f.Db.TimelineFacts.SingleAsync();
        Assert.True(target.Valid); Assert.Equal(priorReview.Id, target.CreationReviewId);
        var priorEntryId = f.Entry.Id;
        await f.NewSourceAsync();
        f.Entry.Title = current.GetProperty("title").GetString()!; f.Entry.Content = current.GetProperty("content").GetString()!;
        await f.Db.SaveChangesAsync();
        var options = (DbContextOptions<ProjectTimelineDbContext>)f.Db.GetService<IDbContextOptions>();
        var fake = new FactReviewFakeAgent { Result = new() { Additions = [candidate],
            Questions = [new(clarification.GetProperty("question").GetString()!, null)], Invalidations = [new(target.Id, "Explicit correction", 0)],
            InvalidationAssessments = [TimelineFactContextReconciliationTests.Assessment(target, candidate)] } };
        var api = new TimelineFactApplication(f.Db, () => fake, new(() => new(options), new BrowserCandidates(candidate)));
        var generated = await api.GenerateAsync(f.Entry.Id, f.Actor.Id, false, new());
        Assert.Equal("Pending", generated.LifecycleState); // Rejects the old browser's EntryContent/missing-entity fixtures.
        var persisted = Assert.Single(generated.Proposals.Single(x => x.State == "Generated").Additions);
        Assert.Equal(candidate, persisted);
        await f.Db.Entry(f.Timeline).ReloadAsync();
        var first = await api.PlanAsync(f.Entry.Id, f.Actor.Id, false, new(null, []));
        Assert.False(first.Ready);
        var invalidation = Assert.Single(first.Invalidations);
        Assert.Equal(target.Claim, invalidation.Claim); Assert.Equal(target.SourceQuote, invalidation.SourceQuote);
        Assert.Equal(candidate.Claim, invalidation.InvalidatingClaim);
        Assert.NotNull(invalidation.InvalidatingAdditionId); Assert.Equal(0, invalidation.InvalidatingAdditionIndex);
        fake.Result.Questions = [];
        var ready = await api.PlanAsync(f.Entry.Id, f.Actor.Id, false, new(first.ProposalId,
            [new(0, clarification.GetProperty("answer").GetString()!)]));
        Assert.True(ready.Ready);
        await api.AcceptAsync(f.Entry.Id, ready.ProposalId!.Value, f.Actor.Id, false);
        var replacement = await f.Db.TimelineFacts.SingleAsync(x => x.EntryId == f.Entry.Id);
        await f.Db.Entry(target).ReloadAsync();
        Assert.NotEqual(priorEntryId, replacement.EntryId);
        Assert.False(target.Valid); Assert.Equal(replacement.Id, target.InvalidatedByFactId);
        Assert.Equal(candidate.SourceQuote, replacement.SourceQuote); Assert.Equal("Entry", replacement.SourceKind);
        Assert.Equal(ready.ProposalId, replacement.CreationReviewId);
    }

    [Fact]
    public async Task Bounded_history_pins_current_generation_and_latest_clarification_after_more_than_fifty_rounds()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await PendingGenerationProvider.GenerateAsync(f.Db, f.Entry, f.Actor.Id, f.Project.Id);
        var fake = new FactReviewFakeAgent();
        var api = new TimelineFactApplication(f.Db, () => fake);
        TimelineFactProposalDto? previous = null;
        for (var round = 0; round < 54; round++)
        {
            fake.Result = new() { Questions = [new($"Confirm round {round}?", null)] };
            previous = await api.PlanAsync(f.Entry.Id, f.Actor.Id, false, new(previous?.ProposalId,
                previous is null ? [] : [new(previous.Questions.Count - 1, "Confirmed.")]));
        }
        fake.Result = new() { Additions = [new("Alice owns delivery.", "alice", "Person", "Alice owns delivery.")] };
        var ready = await api.PlanAsync(f.Entry.Id, f.Actor.Id, false,
            new(previous!.ProposalId, [new(previous.Questions.Count - 1, "Confirmed.")]));
        Assert.True(ready.Ready);
        Assert.Equal(54, ready.Questions.Count);
        Assert.Equal(54, await f.Db.TimelineFactProposals.CountAsync(x => x.Status == "Superseded"));
        var state = await api.ReadAsync(f.Entry.Id, f.Actor.Id, false);
        Assert.True(state.Truncated);
        Assert.Equal(50, state.Proposals.Count);
        Assert.Contains(state.Proposals, p => p.ProposalId == ready.ProposalId && p.Ready);
        var generated = Assert.Single(state.Proposals, p => p.State == "Generated");
        Assert.Equal(state.GenerationId, generated.ProposalId);
        Assert.Equal("Alice owns delivery.", Assert.Single(generated.Additions).Claim);
        await api.AcceptAsync(f.Entry.Id, ready.ProposalId!.Value, f.Actor.Id, false);
        Assert.Single(await f.Db.TimelineFacts.ToListAsync());
    }

    [Fact]
    public async Task Review_persists_server_proposal_accepts_id_only_and_preserves_source()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await PendingGenerationProvider.GenerateAsync(f.Db, f.Entry, f.Actor.Id, f.Project.Id);
        var source = f.Entry.Content;
        var fake = new FactReviewFakeAgent { Result = new() { Additions = [new("Alice owns delivery.", "alice", "Person", "Alice owns delivery.")] } };
        var service = new TimelineFactApplication(f.Db, () => fake);
        var review = await service.PlanAsync(f.Entry.Id, f.Actor.Id, false, new(null, []));
        Assert.NotNull(review.ProposalId);
        Assert.True(review.Ready);
        Assert.NotEmpty(review.Warnings);
        Assert.Equal("Pending", review.State);
        Assert.Equal(2, await f.Db.TimelineFactProposals.CountAsync());
        var accepted = await service.AcceptAsync(f.Entry.Id, review.ProposalId!.Value, f.Actor.Id, false);
        Assert.Equal("Accepted", accepted.State);
        await service.AcceptAsync(f.Entry.Id, review.ProposalId.Value, f.Actor.Id, false);
        Assert.Single(await f.Db.TimelineFacts.ToListAsync());
        Assert.Equal(source, (await f.Db.TimelineEntries.SingleAsync()).Content);
        Assert.DoesNotContain("\"Embedding\":", JsonSerializer.Serialize(await service.ReadAsync(f.Entry.Id, f.Actor.Id, false)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Authorization_precedes_agent_factory_and_no_changes_requires_explicit_acceptance()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await PendingGenerationProvider.GenerateAsync(f.Db, f.Entry, f.Actor.Id, f.Project.Id);
        var calls = 0;
        var service = new TimelineFactApplication(f.Db, () => { calls++; return new FactReviewFakeAgent(); });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PlanAsync(f.Entry.Id, Guid.NewGuid(), true, new(null, [])));
        Assert.Equal(0, calls);
        var reader = new User { Email = "reader@test.invalid", DisplayName = "Reader", PasswordHash = "test" };
        f.Db.Add(reader); await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PlanAsync(f.Entry.Id, reader.Id, false, new(null, [])));
        Assert.Equal(0, calls);
        var result = await service.PlanAsync(f.Entry.Id, f.Actor.Id, false, new(null, []));
        Assert.NotNull(result.ProposalId);
        Assert.True(result.Ready);
        Assert.Equal(2, await f.Db.TimelineFactProposals.CountAsync());
        await service.AcceptAsync(f.Entry.Id, result.ProposalId!.Value, f.Actor.Id, false);
        Assert.Empty(await f.Db.TimelineFacts.ToListAsync());
        Assert.False((await service.ReadAsync(f.Entry.Id, f.Actor.Id, false)).CanEdit);
    }

    [Fact]
    public async Task Clarification_rounds_persist_indexed_answers_and_stale_or_cross_entry_acceptance_fails()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await PendingGenerationProvider.GenerateAsync(f.Db, f.Entry, f.Actor.Id, f.Project.Id);
        var fake = new FactReviewFakeAgent { Result = new() { Questions = [new("Who owns release?", null)] } };
        var service = new TimelineFactApplication(f.Db, () => fake);
        var first = await service.PlanAsync(f.Entry.Id, f.Actor.Id, false, new(null, []));
        Assert.False(first.Ready);
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => service.AcceptAsync(f.Entry.Id, first.ProposalId!.Value, f.Actor.Id, false));
        fake.Result = new() { Additions = [new("Carol owns release.", "carol", "Person", "Carol owns release.", "Clarification")] };
        var next = await service.PlanAsync(f.Entry.Id, f.Actor.Id, false, new(first.ProposalId, [new(0, "Carol owns release.")]));
        Assert.NotEqual(first.ProposalId, next.ProposalId);
        Assert.True(next.Ready);
        Assert.Equal("Carol owns release.", Assert.Single(next.Questions).Answer);
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => service.AcceptAsync(f.Entry.Id, first.ProposalId!.Value, f.Actor.Id, false));
        var other = new TimelineEntry { TimelineId = f.Timeline.Id, AuthorId = f.Actor.Id, EntryNumber = 3, Title = "Other", Content = "Other" };
        f.Db.Add(other); await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.AcceptAsync(other.Id, next.ProposalId!.Value, f.Actor.Id, false));
        Assert.Empty(await f.Db.TimelineFacts.ToListAsync());
    }

    [Fact]
    public async Task Invalidations_return_existing_claim_quote_and_reason()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        const string correctionClaim = "Bob now owns delivery instead of Alice.";
        var correction = new TimelineEntry { TimelineId = f.Timeline.Id, AuthorId = f.Actor.Id, EntryNumber = 3,
            Title = f.Entry.Title, Content = f.Entry.Content + "\n" + correctionClaim };
        f.Db.Add(correction); await f.Db.SaveChangesAsync();
        await PendingGenerationProvider.GenerateAsync(f.Db, correction, f.Actor.Id, f.Project.Id);
        var fake = new FactReviewFakeAgent { Result = new() {
            Additions = [new("Alice owns delivery.", "alice", "Person", "Alice owns delivery."),
                new(correctionClaim, "bob", "Person", correctionClaim)],
            Invalidations = [new(fact.Id, "Responsibility changed", 1)],
            InvalidationAssessments = [TimelineFactContextReconciliationTests.Assessment(fact,
                new(correctionClaim, "bob", "Person", correctionClaim), "ExplicitSupersession", 1)] } };
        var service = new TimelineFactApplication(f.Db, () => fake);
        var result = await service.PlanAsync(correction.Id, f.Actor.Id, false, new(null, []));
        var invalidation = Assert.Single(result.Invalidations);
        Assert.Equal(fact.Claim, invalidation.Claim);
        Assert.Equal(fact.SourceQuote, invalidation.SourceQuote);
        Assert.Equal("Responsibility changed", invalidation.Reason);
        var storedCause = await f.Db.TimelineFactInvalidations.AsNoTracking().SingleAsync(x => x.ProposalId == result.ProposalId);
        var orderedAdditions = await f.Db.TimelineFactAdditions.AsNoTracking().Where(x => x.ProposalId == result.ProposalId).OrderBy(x => x.Id).ToListAsync();
        var causeIndex = orderedAdditions.FindIndex(x => x.Id == storedCause.InvalidatingAdditionId);
        using var preview = JsonDocument.Parse(JsonSerializer.Serialize(invalidation, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.True(preview.RootElement.TryGetProperty("invalidatingAdditionId", out var additionId), "Preview must expose the stored cause addition ID.");
        Assert.Equal(storedCause.InvalidatingAdditionId, additionId.GetGuid());
        Assert.Equal(causeIndex, preview.RootElement.GetProperty("invalidatingAdditionIndex").GetInt32());
        Assert.Equal(correctionClaim, preview.RootElement.GetProperty("invalidatingClaim").GetString());
        Assert.Equal(correctionClaim, result.Additions[causeIndex].Claim);
        Assert.True(fact.Valid);
        Assert.False(preview.RootElement.TryGetProperty("invalidatedByFactId", out _)); // Pending intent is not persisted causality.
        await service.AcceptAsync(correction.Id, result.ProposalId!.Value, f.Actor.Id, false);
        var state = await service.ReadAsync(f.Entry.Id, f.Actor.Id, false);
        using var old = JsonDocument.Parse(JsonSerializer.Serialize(state.Facts.Single(x => x.Id == fact.Id), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.False(old.RootElement.GetProperty("valid").GetBoolean());
        Assert.Equal(additionId.GetGuid(), old.RootElement.GetProperty("invalidatedByFactId").GetGuid());

        // The original link must remain readable when the cause itself is later invalidated.
        var cause = await f.Db.TimelineFacts.SingleAsync(x => x.Id == additionId.GetGuid());
        var laterReplacement = new TimelineFactCandidate("Alice owns delivery again instead of Bob.", "alice", "Person",
            "Alice owns delivery again instead of Bob.");
        fake.Result = new() { Additions = [laterReplacement],
            Invalidations = [new(additionId.GetGuid(), "Later correction", 0)],
            InvalidationAssessments = [TimelineFactContextReconciliationTests.Assessment(cause, laterReplacement, "ExplicitSupersession")] };
        var laterEntry = new TimelineEntry { TimelineId = f.Timeline.Id, AuthorId = f.Actor.Id, EntryNumber = 4,
            Title = f.Entry.Title, Content = f.Entry.Content + "\n" + laterReplacement.Claim };
        f.Db.Add(laterEntry); await f.Db.SaveChangesAsync();
        await PendingGenerationProvider.GenerateAsync(f.Db, laterEntry, f.Actor.Id, f.Project.Id);
        var later = await service.PlanAsync(laterEntry.Id, f.Actor.Id, false, new(null, []));
        await service.AcceptAsync(laterEntry.Id, later.ProposalId!.Value, f.Actor.Id, false);
        var laterState = await service.ReadAsync(correction.Id, f.Actor.Id, false);
        state = await service.ReadAsync(f.Entry.Id, f.Actor.Id, false);
        using var history = JsonDocument.Parse(JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var historical = history.RootElement.GetProperty("facts").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == fact.Id);
        Assert.Equal(additionId.GetGuid(), historical.GetProperty("invalidatedByFactId").GetGuid());
        Assert.Equal(correctionClaim, laterState.Facts.Single(x => x.Id == additionId.GetGuid()).Claim);
        Assert.False(laterState.Facts.Single(x => x.Id == additionId.GetGuid()).Valid);
    }

    [Fact]
    public async Task Historical_null_cause_is_returned_without_backfill_or_agent_calls()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        await using (var raw = f.Unguarded())
        {
            var legacy = new TimelineFactProposal { EntryId = f.Entry.Id, Status = "Accepted",
                SourceFingerprint = TimelineFactEvidence.Fingerprint(f.Entry), AcceptedAtUtc = DateTime.UtcNow, AcceptedById = f.Actor.Id };
            raw.Add(legacy);
            raw.Add(new TimelineFactInvalidation { ProposalId = legacy.Id, FactId = fact.Id, Reason = "Historical correction" });
            var old = await raw.Set<TimelineFact>().SingleAsync();
            old.Valid = false;
            old.InvalidationReviewId = legacy.Id;
            old.InvalidatedAtUtc = legacy.AcceptedAtUtc;
            await raw.SaveChangesAsync();
        }
        var service = new TimelineFactApplication(f.Db, () => throw new Exception("Read must not construct an agent"));
        using var state = JsonDocument.Parse(JsonSerializer.Serialize(await service.ReadAsync(f.Entry.Id, f.Actor.Id, false), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var historical = Assert.Single(state.RootElement.GetProperty("facts").EnumerateArray());
        Assert.True(historical.TryGetProperty("invalidatedByFactId", out var cause), "Historical DTO must expose nullable causality.");
        Assert.Equal(JsonValueKind.Null, cause.ValueKind);
        var invalidation = state.RootElement.GetProperty("proposals").EnumerateArray()
            .SelectMany(x => x.GetProperty("invalidations").EnumerateArray()).Single();
        foreach (var name in new[] { "invalidatingAdditionId", "invalidatingAdditionIndex", "invalidatingClaim" })
            Assert.Equal(JsonValueKind.Null, invalidation.GetProperty(name).ValueKind);
    }

    [Fact]
    public async Task Endpoint_mapping_returns_safe_statuses_without_exception_text()
    {
        var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([
            new(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test"));
        async Task<int?> Status(Exception error)
        {
            var result = await TimelineFactEndpoints.RespondAsync<int>(principal, (_, _) => Task.FromException<int>(error));
            return result is Microsoft.AspNetCore.Http.HttpResults.ForbidHttpResult ? 403 : ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)result).StatusCode;
        }
        Assert.Equal(403, await Status(new UnauthorizedAccessException("secret")));
        Assert.Equal(404, await Status(new KeyNotFoundException("secret")));
        Assert.Equal(409, await Status(new TimelineFactConflictException("secret")));
        Assert.Equal(502, await Status(new TimelineFactProviderException("Unavailable")));
        var unauth = await TimelineFactEndpoints.RespondAsync<int>(new(), (_, _) => throw new Exception("must not execute"));
        Assert.Equal(401, ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)unauth).StatusCode);
    }

    [Theory]
    [InlineData("{\"ready\":true,\"additions\":[]}")]
    [InlineData("{}")]
    [InlineData(" ")]
    public async Task Acceptance_rejects_any_body_without_trusting_replacement_or_readiness(string body)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));
        var result = await TimelineFactEndpoints.AcceptIdOnlyAsync(context.Request, () => throw new Exception("must not accept"));
        Assert.Equal(400, ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)result).StatusCode);
        context.Request.Body = Stream.Null;
        var calls = 0;
        await TimelineFactEndpoints.AcceptIdOnlyAsync(context.Request, () => { calls++; return Task.FromResult<Microsoft.AspNetCore.Http.IResult>(Microsoft.AspNetCore.Http.Results.Ok()); });
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Plan_request_rejects_unknown_client_draft_fields()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TimelineFactPlanRequest>("{\"draft\":{\"ready\":true}}", new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public async Task Provider_failures_are_redacted()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        await PendingGenerationProvider.GenerateAsync(f.Db, f.Entry, f.Actor.Id, f.Project.Id);
        var service = new TimelineFactApplication(f.Db, () => throw new Exception("credential and provider body"));
        var error = await Assert.ThrowsAsync<TimelineFactProviderException>(() => service.PlanAsync(f.Entry.Id, f.Actor.Id, false, new(null, [])));
        Assert.Equal("Fact provider failed.", error.Message);
        Assert.Null(error.InnerException);
    }
}
