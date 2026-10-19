using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.DataAccessLogic;
using ProjectTimeline.UI.WebApi;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class TimelineFactApiTests
{
    [Fact]
    public async Task Review_persists_server_proposal_accepts_id_only_and_preserves_source()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var source = f.Entry.Content;
        var fake = new FactReviewFakeAgent { Result = new() { Additions = [new("Alice owns delivery.", "alice", "Person", "Alice owns delivery.")] } };
        var service = new TimelineFactApplication(f.Db, () => fake);
        var review = await service.PlanAsync(f.Entry.Id, f.Actor.Id, false, new(null, []));
        Assert.NotNull(review.ProposalId);
        Assert.True(review.Ready);
        Assert.NotEmpty(review.Warnings);
        Assert.Equal("Pending", review.State);
        Assert.Single(await f.Db.TimelineFactProposals.ToListAsync());
        var accepted = await service.AcceptAsync(f.Entry.Id, review.ProposalId!.Value, f.Actor.Id, false);
        Assert.Equal("Accepted", accepted.State);
        await service.AcceptAsync(f.Entry.Id, review.ProposalId.Value, f.Actor.Id, false);
        Assert.Single(await f.Db.TimelineFacts.ToListAsync());
        Assert.Equal(source, (await f.Db.TimelineEntries.SingleAsync()).Content);
        Assert.DoesNotContain("\"Embedding\":", JsonSerializer.Serialize(await service.ReadAsync(f.Entry.Id, f.Actor.Id, false)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Authorization_precedes_agent_factory_and_no_changes_does_not_prepare()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var calls = 0;
        var service = new TimelineFactApplication(f.Db, () => { calls++; return new FactReviewFakeAgent(); });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PlanAsync(f.Entry.Id, Guid.NewGuid(), true, new(null, [])));
        Assert.Equal(0, calls);
        var reader = new User { Email = "reader@test.invalid", DisplayName = "Reader", PasswordHash = "test" };
        f.Db.Add(reader); await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PlanAsync(f.Entry.Id, reader.Id, false, new(null, [])));
        Assert.Equal(0, calls);
        var result = await service.PlanAsync(f.Entry.Id, f.Actor.Id, false, new(null, []));
        Assert.Null(result.ProposalId);
        Assert.False(result.Ready);
        Assert.Empty(await f.Db.TimelineFactProposals.ToListAsync());
    }

    [Fact]
    public async Task Clarification_rounds_persist_indexed_answers_and_stale_or_cross_entry_acceptance_fails()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
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
        var fake = new FactReviewFakeAgent { Result = new() {
            Additions = [new("Alice owns delivery.", "alice", "Person", "Alice owns delivery."),
                new("Bob owns testing.", "bob", "Person", "Bob owns testing.")],
            Invalidations = [new(fact.Id, "Responsibility changed", 1)] } };
        var service = new TimelineFactApplication(f.Db, () => fake);
        var result = await service.PlanAsync(f.Entry.Id, f.Actor.Id, false, new(null, []));
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
        Assert.Equal("Bob owns testing.", preview.RootElement.GetProperty("invalidatingClaim").GetString());
        Assert.Equal("Bob owns testing.", result.Additions[causeIndex].Claim);
        Assert.True(fact.Valid);
        Assert.False(preview.RootElement.TryGetProperty("invalidatedByFactId", out _)); // Pending intent is not persisted causality.
        await service.AcceptAsync(f.Entry.Id, result.ProposalId!.Value, f.Actor.Id, false);
        var state = await service.ReadAsync(f.Entry.Id, f.Actor.Id, false);
        using var old = JsonDocument.Parse(JsonSerializer.Serialize(state.Facts.Single(x => x.Id == fact.Id), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.False(old.RootElement.GetProperty("valid").GetBoolean());
        Assert.Equal(additionId.GetGuid(), old.RootElement.GetProperty("invalidatedByFactId").GetGuid());

        // The original link must remain readable when the cause itself is later invalidated.
        fake.Result = new() { Additions = [new("Alice owns delivery.", "alice", "Person", "Alice owns delivery.")],
            Invalidations = [new(additionId.GetGuid(), "Later correction", 0)] };
        var later = await service.PlanAsync(f.Entry.Id, f.Actor.Id, false, new(null, []));
        await service.AcceptAsync(f.Entry.Id, later.ProposalId!.Value, f.Actor.Id, false);
        state = await service.ReadAsync(f.Entry.Id, f.Actor.Id, false);
        using var history = JsonDocument.Parse(JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var historical = history.RootElement.GetProperty("facts").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == fact.Id);
        Assert.Equal(additionId.GetGuid(), historical.GetProperty("invalidatedByFactId").GetGuid());
        Assert.Equal("Bob owns testing.", state.Facts.Single(x => x.Id == additionId.GetGuid()).Claim);
        Assert.False(state.Facts.Single(x => x.Id == additionId.GetGuid()).Valid);
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
        var service = new TimelineFactApplication(f.Db, () => throw new Exception("credential and provider body"));
        var error = await Assert.ThrowsAsync<TimelineFactProviderException>(() => service.PlanAsync(f.Entry.Id, f.Actor.Id, false, new(null, [])));
        Assert.Equal("Fact provider failed.", error.Message);
        Assert.Null(error.InnerException);
    }
}
