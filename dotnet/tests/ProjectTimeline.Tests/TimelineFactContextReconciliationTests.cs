using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.BusinessLogic.Agents.AgentModels;
using ProjectTimeline.DataAccessLogic;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class TimelineFactContextReconciliationTests
{
    [Theory]
    [InlineData("Login requires password recovery.", "The mobile toolbar is blue.")]
    [InlineData("Start the server with dotnet run.", "The toolbar is blue.")]
    public async Task Replacement_link_alone_never_authorizes_invalidation(string oldClaim, string newClaim)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        f.Entry.Content = oldClaim;
        await f.Db.SaveChangesAsync();
        var fact = await f.AcceptAsync(oldClaim, "requirement");
        await f.NewSourceAsync();
        await using (var raw = f.Unguarded())
        {
            (await raw.Set<TimelineEntry>().SingleAsync(x => x.Id == f.Entry.Id)).Content = newClaim;
            await raw.SaveChangesAsync();
        }
        var fake = new FactReviewFakeAgent { Result = new() {
            Additions = [new(newClaim, "requirement", "Requirement", newClaim)],
            Invalidations = [new(fact.Id, "Newer source", 0)] } };
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => Planner(f, fake).PlanAsync());
        Assert.True(fact.Valid);
    }

    [Fact]
    public async Task Seed_and_followup_contexts_include_original_entry_not_just_quote()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        var title = f.Entry.Title;
        var content = f.Entry.Content;
        await f.NewSourceAsync();
        var fake = new FactReviewFakeAgent();
        fake.Script = async () =>
        {
            var envelope = JsonDocument.Parse(fake.Message!).RootElement;
            var seed = Assert.Single(envelope.GetProperty("seed_contexts").EnumerateArray());
            Assert.Equal(content, seed.GetProperty("Content").GetString());
            var tool = Assert.IsAssignableFrom<AIFunction>(fake.Tools.Single(x => x.Name == "fetch_fact_contexts"));
            var contexts = JsonSerializer.SerializeToElement(await tool.InvokeAsync(
                new AIFunctionArguments { ["ids"] = new[] { fact.Id } }));
            Assert.Equal(title, contexts[0].GetProperty("title").GetString());
            Assert.Equal(content, contexts[0].GetProperty("content").GetString());
        };
        await Planner(f, fake).PlanAsync();
    }

    [Theory]
    [InlineData("Contradiction", "Delivery has exactly one owner: Alice.", "Delivery has exactly one owner: Bob.")]
    [InlineData("ExplicitSupersession", "Use dotnet run to start this server.", "Replace the startup command dotnet run with dotnet watch for this server.")]
    public async Task Bound_same_scope_assessment_allows_supported_replacement(string relationship, string oldClaim, string newClaim)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        f.Entry.Content = oldClaim;
        await f.Db.SaveChangesAsync();
        var fact = await f.AcceptAsync(oldClaim, "old-key");
        await f.NewSourceAsync();
        f.Entry.Content = newClaim;
        await f.Db.SaveChangesAsync();
        var candidate = new TimelineFactCandidate(newClaim, "different-key", "Requirement", newClaim);
        var fake = new FactReviewFakeAgent { Result = new() {
            Additions = [candidate], Invalidations = [new(fact.Id, "Scoped update", 0)],
            InvalidationAssessments = [Assessment(fact, candidate, relationship)] } };
        // The historical fact may not be a lexical seed; followup must deliver context before assessment is accepted.
        fake.Script = async () =>
        {
            var tool = Assert.IsAssignableFrom<AIFunction>(fake.Tools.Single(x => x.Name == "fetch_fact_contexts"));
            await tool.InvokeAsync(new AIFunctionArguments { ["ids"] = new[] { fact.Id } });
        };
        var plan = await Planner(f, fake).PlanAsync();
        Assert.Single(plan.Draft.Invalidations);
        Assert.True(fact.Valid);
        Assert.False(f.Db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("fact")]
    [InlineData("index")]
    [InlineData("historicalClaim")]
    [InlineData("fingerprint")]
    [InlineData("historicalQuote")]
    [InlineData("replacementClaim")]
    [InlineData("replacementQuote")]
    [InlineData("scope")]
    [InlineData("independent")]
    [InlineData("ambiguous")]
    [InlineData("silence")]
    [InlineData("explanation")]
    [InlineData("sharedScope")]
    [InlineData("missingContext")]
    public async Task Malformed_or_noninvalidating_assessments_fail_closed(string change)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        await f.NewSourceAsync();
        var candidate = new TimelineFactCandidate("Bob owns testing.", "bob", "Person", "Bob owns testing.");
        var assessment = Assessment(fact, candidate);
        assessment = change switch
        {
            "fact" => assessment with { FactId = Guid.NewGuid() },
            "index" => assessment with { InvalidatingAdditionIndex = 1 },
            "historicalClaim" => assessment with { HistoricalClaim = "Forged" },
            "fingerprint" => assessment with { HistoricalSourceFingerprint = "Forged" },
            "historicalQuote" => assessment with { HistoricalQuote = "Invented history" },
            "replacementClaim" => assessment with { ReplacementClaim = "Forged" },
            "replacementQuote" => assessment with { ReplacementQuote = "Forged" },
            "scope" => assessment with { SameScope = false },
            "independent" => assessment with { Relationship = "Independent" },
            "ambiguous" => assessment with { Relationship = "Ambiguous" },
            "silence" => assessment with { Relationship = "AbsenceOfEvidence" },
            "explanation" => assessment with { Explanation = " " },
            "sharedScope" => assessment with { SharedScope = " " },
            _ => assessment
        };
        var fake = new FactReviewFakeAgent { Result = new() {
            Additions = [candidate], Invalidations = [new(fact.Id, "Update", 0)],
            InvalidationAssessments = change == "missing" ? [] : [assessment] } };
        if (change == "extra") fake.Result.Invalidations.Clear();
        if (change == "duplicate") fake.Result.InvalidationAssessments.Add(assessment);
        if (change == "missingContext")
        {
            await using var raw = f.Unguarded();
            (await raw.Set<TimelineEntry>().SingleAsync(x => x.Id == fact.EntryId)).Content += " changed";
            await raw.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => Planner(f, fake).PlanAsync());
        Assert.True(fact.Valid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Independent_claims_are_preserved_and_genuine_ambiguity_becomes_question(bool ambiguous)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var fact = await f.AcceptAsync("Alice owns delivery.", "alice");
        await f.NewSourceAsync();
        var fake = new FactReviewFakeAgent { Result = new() {
            Additions = [new("Bob owns testing.", "bob", "Person", "Bob owns testing.")],
            Questions = ambiguous ? [new("Does this ownership change also apply to delivery?", null)] : [] } };
        var plan = await Planner(f, fake).PlanAsync();
        Assert.Empty(plan.Draft.Invalidations);
        Assert.Equal(ambiguous, plan.Status.EndsWith("NeedsClarification"));
        Assert.True(fact.Valid);
        Assert.Contains("Absence of evidence is not contradiction", fake.Instructions);
        Assert.Contains("login/password recovery", fake.Instructions);
        Assert.Contains("dotnet run", fake.Instructions);
    }

    [Fact]
    public async Task Valid_looking_assessment_without_delivered_historical_context_is_rejected()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        f.Entry.Content = "Use dotnet run for startup.";
        await f.Db.SaveChangesAsync();
        var fact = await f.AcceptAsync(f.Entry.Content, "startup");
        await f.NewSourceAsync();
        f.Entry.Title = "Command change";
        f.Entry.Content = "Watch replaces the command.";
        await f.Db.SaveChangesAsync();
        var candidate = new TimelineFactCandidate(f.Entry.Content, "command", "Requirement", f.Entry.Content);
        var fake = new FactReviewFakeAgent { Result = new() {
            Additions = [candidate], Invalidations = [new(fact.Id, "Command superseded", 0)],
            InvalidationAssessments = [Assessment(fact, candidate, "ExplicitSupersession")] } };
        var error = await Assert.ThrowsAsync<TimelineFactConflictException>(() => Planner(f, fake).PlanAsync());
        Assert.Contains("delivered context", error.Message);
        Assert.Empty(JsonDocument.Parse(fake.Message!).RootElement.GetProperty("seed_contexts").EnumerateArray());
    }

    [Theory]
    [InlineData("literal")]
    [InlineData("forgedAssessmentQuote")]
    [InlineData("canonicalAssessmentQuote")]
    [InlineData("unsupportedModelQuote")]
    public async Task Canonical_quote_reuse_preserves_valid_original_assessment_binding(string change)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        f.Entry.Content = "Bob owns delivery.";
        await f.Db.SaveChangesAsync();
        var fact = await f.AcceptAsync(f.Entry.Content, "bob");
        await f.NewSourceAsync();
        f.Entry.Content = "Alice owns delivery. Bob no longer owns delivery.";
        await f.Db.SaveChangesAsync();
        await PendingGenerationProvider.GenerateAsync(f.Db, f.Entry, f.Actor.Id, f.Project.Id);
        var selectedQuote = change == "unsupportedModelQuote" ? "Invented source evidence" : f.Entry.Content;
        var addition = new TimelineFactCandidate("Alice owns delivery.", "alice", "Person", selectedQuote);
        var assessment = Assessment(fact, addition, "ExplicitSupersession");
        if (change == "forgedAssessmentQuote") assessment = assessment with { ReplacementQuote = "Forged" };
        if (change == "canonicalAssessmentQuote") assessment = assessment with { ReplacementQuote = "Alice owns delivery." };
        var fake = new FactReviewFakeAgent { Result = new() {
            Additions = [addition], Invalidations = [new(fact.Id, "Delivery owner explicitly replaced", 0)],
            InvalidationAssessments = [assessment] } };
        if (change == "literal")
        {
            var plan = await Planner(f, fake).PlanAsync();
            Assert.Equal("Alice owns delivery.", Assert.Single(plan.Draft.Additions).SourceQuote);
            Assert.Equal(fact.Id, Assert.Single(plan.Draft.Invalidations).FactId);
            Assert.Equal(selectedQuote, Assert.Single(fake.Result.InvalidationAssessments).ReplacementQuote);
            Assert.True(fact.Valid);
            Assert.False(f.Db.ChangeTracker.HasChanges());
        }
        else await Assert.ThrowsAsync<TimelineFactConflictException>(() => Planner(f, fake).PlanAsync());
    }

    internal static TimelineFactInvalidationAssessment Assessment(TimelineFact fact, TimelineFactCandidate addition,
        string relationship = "Contradiction", int index = 0)
        => new(fact.Id, index, fact.Claim, fact.SourceFingerprint, fact.SourceQuote, addition.Claim, addition.SourceQuote,
            relationship, true, "Same requirement, subject, conditions and effective time",
            "The scoped claims cannot coexist; the new source changes that requirement.");

    private static TimelineFactReviewPlanner Planner(TimelineFactReviewFixture f, FactReviewFakeAgent fake)
        => new(f.Db, f.Scope, f.Actor.Id, false, new TimelineFactOptimizationAgent(fake));
}
