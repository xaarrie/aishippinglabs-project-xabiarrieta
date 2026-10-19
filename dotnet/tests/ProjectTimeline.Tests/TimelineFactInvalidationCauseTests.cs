using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Metadata;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.BusinessLogic.Agents;
using ProjectTimeline.DataAccessLogic;
using Xunit;

namespace ProjectTimeline.Tests;

// InMemory proves guard atomicity before SaveChanges, not PostgreSQL transaction rollback/isolation.
public sealed class TimelineFactInvalidationCauseTests
{
    private static TimelineFactCandidate Alice => new("Alice owns delivery.", "alice", "Person", "Alice owns delivery.");
    private static TimelineFactCandidate Bob => new("Bob owns testing.", "bob", "Person", "Bob owns testing.");
    private static async Task<TimelineFactProposal> Prepare(TimelineFactReviewFixture f, TimelineFactDraft draft) =>
        await new TimelineFactService(f.Db).PrepareAsync(f.Scope, f.Actor.Id, false,
            await f.Db.Timelines.AsNoTracking().Where(x => x.Id == f.Timeline.Id).Select(x => x.FactRevision).SingleAsync(), draft);
    private static Task<TimelineFactProposal> Accept(TimelineFactReviewFixture f, TimelineFactProposal proposal) =>
        new TimelineFactService(f.Db).AcceptAsync(f.Scope, proposal.Id, f.Actor.Id, false);

    [Fact]
    public async Task New_invalidation_without_explicit_replacement_is_rejected_even_with_one_addition()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var old = await f.AcceptAsync(Alice.SourceQuote, "alice");
        await f.NewSourceAsync();
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => Prepare(f,
            new([Bob], [new(old.Id, "Replaced")], [])));
        Assert.True(old.Valid);
        Assert.Single(await f.Db.TimelineFactProposals.ToListAsync());
    }

    [Fact]
    public async Task Planner_rejects_missing_cause_instead_of_guessing_first_addition()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var old = await f.AcceptAsync(Alice.SourceQuote, "alice");
        await f.NewSourceAsync();
        var fake = new FactReviewFakeAgent { Result = new() { Additions = [Bob], Invalidations = [new(old.Id, "Replaced")] } };
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => new TimelineFactReviewPlanner(f.Db, f.Scope,
            f.Actor.Id, false, new TimelineFactOptimizationAgent(fake)).PlanAsync());
        Assert.True(old.Valid);
        Assert.False(f.Db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task Explicit_second_addition_is_durable_cause_for_multiple_targets_and_retains_history_on_later_correction()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var first = await f.AcceptAsync(Alice.SourceQuote, "alice");
        await f.NewSourceAsync();
        var second = await f.AcceptAsync(Bob.SourceQuote, "bob");
        await f.NewSourceAsync();
        var proposal = await Prepare(f, new([Alice, Bob], [new(first.Id, "Corrected", 1), new(second.Id, "Corrected", 1)], []));
        var selected = proposal.Additions.ElementAt(1).Id;
        Assert.All(proposal.Invalidations, x => Assert.Equal(selected, x.InvalidatingAdditionId));
        Assert.True(first.Valid); Assert.Null(first.InvalidatedByFactId);
        f.Db.ChangeTracker.Clear(); // Resolve from persisted identities, not draft enumeration or tracked navigations.
        await Accept(f, proposal);
        var facts = await f.Db.TimelineFacts.ToListAsync();
        var cause = facts.Single(x => x.AdditionId == selected);
        Assert.Equal(selected, cause.Id);
        Assert.Equal(proposal.EntryId, cause.EntryId);
        Assert.NotEqual(first.EntryId, cause.EntryId); Assert.NotEqual(second.EntryId, cause.EntryId);
        foreach (var id in new[] { first.Id, second.Id })
        {
            var target = facts.Single(x => x.Id == id);
            Assert.False(target.Valid); Assert.Equal(cause.Id, target.InvalidatedByFactId);
            Assert.Equal(proposal.Id, target.InvalidationReviewId);
            Assert.Equal((await f.Db.TimelineFactProposals.FindAsync(proposal.Id))!.AcceptedAtUtc, target.InvalidatedAtUtc);
        }
        Assert.Equal(4, await f.Db.TimelineFactJobs.CountAsync(x => x.Kind == "Embedding" && x.State == "Pending"));
        var revision = (await f.Db.Timelines.SingleAsync()).FactRevision;
        await Accept(f, proposal);
        Assert.Equal(revision, (await f.Db.Timelines.SingleAsync()).FactRevision);
        Assert.Equal(4, await f.Db.TimelineFacts.CountAsync());
        await f.NewSourceAsync();
        var next = await Prepare(f, new([Alice], [new(cause.Id, "Later replacement", 0)], []));
        await Accept(f, next);
        Assert.False(cause.Valid); Assert.Equal(next.Additions.Single().Id, cause.InvalidatedByFactId);
        Assert.Equal(cause.Id, (await f.Db.TimelineFacts.FindAsync(first.Id))!.InvalidatedByFactId);
        Assert.Equal(first.Claim, (await f.Db.TimelineFacts.FindAsync(first.Id))!.Claim);
        Assert.Equal(first.SourceQuote, (await f.Db.TimelineFacts.FindAsync(first.Id))!.SourceQuote);
        Assert.Equal(5, await f.Db.TimelineFacts.CountAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task Missing_negative_and_out_of_range_indices_are_rejected_by_service_and_planner(int? index)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var old = await f.AcceptAsync(Alice.SourceQuote, "alice");
        await f.NewSourceAsync();
        var draft = new TimelineFactDraft([Alice, Bob], [new(old.Id, "Corrected", index)], []);
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => Prepare(f, draft));
        var fake = new FactReviewFakeAgent { Result = new() { Additions = draft.Additions.ToList(), Invalidations = draft.Invalidations.ToList() } };
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => new TimelineFactReviewPlanner(f.Db, f.Scope,
            f.Actor.Id, false, new TimelineFactOptimizationAgent(fake)).PlanAsync());
        Assert.True(old.Valid); Assert.Single(await f.Db.TimelineFacts.ToListAsync());
        Assert.False(f.Db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task No_supported_replacement_can_be_a_question_but_cannot_be_an_invalidation_only_proposal()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var old = await f.AcceptAsync(Alice.SourceQuote, "alice");
        await f.NewSourceAsync();
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => Prepare(f, new([], [new(old.Id, "Unknown replacement", 0)], [])));
        var fake = new FactReviewFakeAgent { Result = new() { Questions = [new("Which fact replaces the old responsibility?", null)] } };
        var plan = await new TimelineFactReviewPlanner(f.Db, f.Scope, f.Actor.Id, false, new TimelineFactOptimizationAgent(fake)).PlanAsync();
        Assert.Empty(plan.Draft.Additions); Assert.Empty(plan.Draft.Invalidations);
        Assert.EndsWith("NeedsClarification", plan.Status);
        Assert.Contains("instead of invalidating or inventing", fake.Instructions);
        Assert.True(old.Valid);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("foreign-proposal")]
    [InlineData("self")]
    public async Task Direct_pending_creation_cannot_bypass_same_new_proposal_association(string attack)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var old = await f.AcceptAsync(Alice.SourceQuote, "alice");
        await f.NewSourceAsync();
        var proposal = new TimelineFactProposal { EntryId = f.Entry.Id, ExpectedRevision = f.Timeline.FactRevision + 1,
            SourceFingerprint = TimelineFactEvidence.Fingerprint(f.Entry), ClarificationEvidence = "[]" };
        var addition = new TimelineFactAddition { ProposalId = proposal.Id, Claim = Bob.Claim, EntityKey = Bob.EntityKey,
            EntityType = Bob.EntityType, SourceQuote = Bob.SourceQuote };
        proposal.Additions.Add(addition);
        proposal.Invalidations.Add(new() { ProposalId = proposal.Id, FactId = attack == "self" ? addition.Id : old.Id,
            InvalidatingAdditionId = attack switch { "missing" => null, "unknown" => Guid.NewGuid(),
                "foreign-proposal" => old.AdditionId, _ => addition.Id }, Reason = "Corrected" });
        f.Db.Add(proposal);
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => f.Db.SaveChangesAsync());
        await using var reader = f.Unguarded();
        Assert.Single(await reader.Set<TimelineFactProposal>().ToListAsync());
        Assert.True((await reader.Set<TimelineFact>().SingleAsync()).Valid);
    }

    [Theory]
    [InlineData("missing", false)]
    [InlineData("unknown", false)]
    [InlineData("foreign-proposal", false)]
    [InlineData("missing", true)]
    [InlineData("unknown", true)]
    [InlineData("foreign-proposal", true)]
    public async Task Acceptance_rechecks_corrupt_stored_association_without_partial_changes(string attack, bool direct)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var old = await f.AcceptAsync(Alice.SourceQuote, "alice");
        await f.NewSourceAsync();
        var proposal = await Prepare(f, new([Bob], [new(old.Id, "Corrected", 0)], []));
        await using (var raw = f.Unguarded())
        {
            var invalidation = await raw.Set<TimelineFactInvalidation>().SingleAsync(x => x.ProposalId == proposal.Id);
            invalidation.InvalidatingAdditionId = attack switch { "missing" => null, "unknown" => Guid.NewGuid(), _ => old.AdditionId };
            await raw.SaveChangesAsync();
        }
        f.Db.ChangeTracker.Clear();
        if (direct)
        {
            var stored = await f.Db.TimelineFactProposals.Include(x => x.Additions).Include(x => x.Invalidations)
                .SingleAsync(x => x.Id == proposal.Id);
            StageAcceptance(f, stored, await f.Db.TimelineFacts.SingleAsync());
            await Assert.ThrowsAsync<TimelineFactConflictException>(() => f.Db.SaveChangesAsync());
        }
        else await Assert.ThrowsAsync<TimelineFactConflictException>(() => Accept(f, proposal));
        await AssertUnchanged(f, proposal.Id, old.Id);
    }

    private static void StageAcceptance(TimelineFactReviewFixture f, TimelineFactProposal proposal, TimelineFact old)
    {
        proposal.Status = "Accepted"; proposal.AcceptedAtUtc = DateTime.UtcNow; proposal.AcceptedById = f.Actor.Id;
        foreach (var addition in proposal.Additions)
        {
            f.Db.Add(new TimelineFact { Id = addition.Id, EntryId = proposal.EntryId, CreationReviewId = proposal.Id,
                AdditionId = addition.Id, Claim = addition.Claim, EntityKey = addition.EntityKey, EntityType = addition.EntityType,
                SourceQuote = addition.SourceQuote, SourceKind = addition.SourceKind, SourceFingerprint = proposal.SourceFingerprint,
                ClarificationEvidence = proposal.ClarificationEvidence });
            f.Db.Add(new TimelineFactJob { EntryId = proposal.EntryId, FactId = addition.Id, Kind = "Embedding",
                WorkKey = $"embedding:{addition.Id:N}", SourceFingerprint = proposal.SourceFingerprint });
        }
        old.Valid = false; old.InvalidationReviewId = proposal.Id; old.InvalidatedAtUtc = proposal.AcceptedAtUtc;
        old.InvalidatedByFactId = proposal.Invalidations.Single().InvalidatingAdditionId;
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("arbitrary-first")]
    [InlineData("self")]
    [InlineData("foreign-fact")]
    [InlineData("wrong-review")]
    [InlineData("new-fact-cause")]
    [InlineData("missing-embedding")]
    [InlineData("fact-id-mismatch")]
    public async Task Direct_acceptance_rejects_forged_cause_or_incomplete_atomic_payload(string attack)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var old = await f.AcceptAsync(Alice.SourceQuote, "alice");
        await f.NewSourceAsync();
        var proposal = await Prepare(f, new([Alice, Bob], [new(old.Id, "Corrected", 1)], []));
        StageAcceptance(f, proposal, old);
        switch (attack)
        {
            case "missing": old.InvalidatedByFactId = null; break;
            case "arbitrary-first": old.InvalidatedByFactId = proposal.Additions.First().Id; break;
            case "self": old.InvalidatedByFactId = old.Id; break;
            case "foreign-fact": old.InvalidatedByFactId = Guid.NewGuid(); break;
            case "wrong-review": old.InvalidationReviewId = old.CreationReviewId; break;
            case "new-fact-cause":
                f.Db.ChangeTracker.Entries<TimelineFact>().First(x => x.State == EntityState.Added).Entity.InvalidatedByFactId = old.Id; break;
            case "missing-embedding":
                f.Db.ChangeTracker.Entries<TimelineFactJob>().First(x => x.State == EntityState.Added).State = EntityState.Detached; break;
            case "fact-id-mismatch":
                var entry = f.Db.ChangeTracker.Entries<TimelineFact>().First(x => x.State == EntityState.Added);
                entry.Entity.Id = Guid.NewGuid(); break;
        }
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => f.Db.SaveChangesAsync());
        await AssertUnchanged(f, proposal.Id, old.Id);
    }

    private static async Task AssertUnchanged(TimelineFactReviewFixture f, Guid proposalId, Guid oldId)
    {
        await using var reader = f.Unguarded();
        Assert.Equal("Pending", (await reader.Set<TimelineFactProposal>().SingleAsync(x => x.Id == proposalId)).Status);
        var fact = await reader.Set<TimelineFact>().SingleAsync(x => x.Id == oldId);
        Assert.True(fact.Valid); Assert.Null(fact.InvalidatedByFactId); Assert.Null(fact.InvalidatedAtUtc);
        Assert.Single(await reader.Set<TimelineFact>().ToListAsync());
        Assert.Single(await reader.Set<TimelineFactJob>().ToListAsync());
    }

    [Theory]
    [InlineData("clear-link")]
    [InlineData("replace-link")]
    [InlineData("restore-valid")]
    [InlineData("edit-invalidation")]
    public async Task Historical_causality_and_proposal_association_are_immutable(string attack)
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var old = await f.AcceptAsync(Alice.SourceQuote, "alice");
        await f.NewSourceAsync();
        var proposal = await Prepare(f, new([Bob], [new(old.Id, "Corrected", 0)], []));
        await Accept(f, proposal);
        var cause = old.InvalidatedByFactId;
        if (attack == "clear-link") old.InvalidatedByFactId = null;
        if (attack == "replace-link") old.InvalidatedByFactId = Guid.NewGuid();
        if (attack == "restore-valid") old.Valid = true;
        if (attack == "edit-invalidation") proposal.Invalidations.Single().InvalidatingAdditionId = old.AdditionId;
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => f.Db.SaveChangesAsync());
        await using var reader = f.Unguarded();
        var persisted = await reader.Set<TimelineFact>().SingleAsync(x => x.Id == old.Id);
        Assert.False(persisted.Valid); Assert.Equal(cause, persisted.InvalidatedByFactId);
    }

    [Fact]
    public async Task Legacy_accepted_null_causality_is_preserved_on_replay_and_new_reviews()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var old = await f.AcceptAsync(Alice.SourceQuote, "alice");
        var legacyId = Guid.NewGuid();
        await using (var raw = f.Unguarded())
        {
            var legacy = new TimelineFactProposal { Id = legacyId, EntryId = f.Entry.Id, Status = "Accepted",
                SourceFingerprint = TimelineFactEvidence.Fingerprint(f.Entry), AcceptedAtUtc = DateTime.UtcNow, AcceptedById = f.Actor.Id };
            raw.Add(legacy);
            raw.Add(new TimelineFactInvalidation { ProposalId = legacy.Id, FactId = old.Id, Reason = "Historical correction" });
            var target = await raw.Set<TimelineFact>().SingleAsync();
            target.Valid = false; target.InvalidatedAtUtc = legacy.AcceptedAtUtc; target.InvalidationReviewId = legacy.Id;
            await raw.SaveChangesAsync();
        }
        f.Db.ChangeTracker.Clear();
        await new TimelineFactService(f.Db).AcceptAsync(f.Scope, legacyId, f.Actor.Id, false);
        Assert.Null((await f.Db.TimelineFacts.SingleAsync()).InvalidatedByFactId);
        Assert.Null((await f.Db.TimelineFactInvalidations.SingleAsync()).InvalidatingAdditionId);
        await f.NewSourceAsync();
        var correction = await f.AcceptAsync(Bob.SourceQuote, "bob");
        Assert.NotEqual(old.EntryId, correction.EntryId);
        Assert.Null((await f.Db.TimelineFacts.SingleAsync(x => x.Id == old.Id)).InvalidatedByFactId);
        Assert.Equal(2, await f.Db.TimelineFacts.CountAsync());
    }

    [Fact]
    public async Task Previous_proposal_indices_are_rebuilt_from_stable_ids_in_its_display_order()
    {
        await using var f = await TimelineFactReviewFixture.CreateAsync();
        var old = await f.AcceptAsync(Alice.SourceQuote, "alice");
        await f.NewSourceAsync();
        var previous = await Prepare(f, new([Alice, Bob], [new(old.Id, "Corrected", 1)], [new("Who confirmed?", null)]));
        var cause = previous.Invalidations.Single().InvalidatingAdditionId;
        var fake = new FactReviewFakeAgent();
        await new TimelineFactReviewPlanner(f.Db, f.Scope, f.Actor.Id, false, new TimelineFactOptimizationAgent(fake)).PlanAsync(previous.Id);
        using var input = JsonDocument.Parse(fake.Message!);
        var displayed = input.RootElement.GetProperty("previous_proposal");
        var index = displayed.GetProperty("invalidations")[0].GetProperty("InvalidatingAdditionIndex").GetInt32();
        var ordered = previous.Additions.OrderBy(x => x.Id).ToList();
        Assert.Equal(cause, ordered[index].Id);
        Assert.Equal(Bob.Claim, displayed.GetProperty("additions")[index].GetProperty("Claim").GetString());
    }

    [Fact]
    public void Migration_is_nullable_preserving_restricted_indexed_and_refuses_lossy_down_offline()
    {
        using var db = new ProjectTimelineDbContext(new DbContextOptionsBuilder<ProjectTimelineDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused", x => x.MigrationsAssembly("ProjectTimeline.Database")).Options);
        var model = db.GetService<IDesignTimeModel>().Model;
        var fact = model.FindEntityType(typeof(TimelineFact))!;
        var invalidation = model.FindEntityType(typeof(TimelineFactInvalidation))!;
        foreach (var (entity, property, principal) in new[] {
            (fact, "InvalidatedByFactId", typeof(TimelineFact)), (invalidation, "InvalidatingAdditionId", typeof(TimelineFactAddition)) })
        {
            Assert.True(entity.FindProperty(property)!.IsNullable);
            var fk = Assert.Single(entity.GetForeignKeys(), x => x.Properties.Single().Name == property);
            Assert.Equal(principal, fk.PrincipalEntityType.ClrType); Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);
            Assert.Contains(entity.GetIndexes(), x => x.Properties.Count == 1 && x.Properties[0].Name == property);
        }
        var check = Assert.Single(fact.GetCheckConstraints(), x => x.Name == "CK_TimelineEntryFacts_InvalidationCause").Sql;
        Assert.Contains("<> \"Id\"", check); Assert.Contains("NOT \"Valid\"", check);
        var migrator = db.GetService<IMigrator>();
        var up = migrator.GenerateScript("20261018000200_DirectTimelineFactExtraction", "20261018000300_FactInvalidationCause");
        var down = migrator.GenerateScript("20261018000300_FactInvalidationCause", "20261018000200_DirectTimelineFactExtraction");
        Assert.Contains("ADD COLUMN \"InvalidatedByFactId\" uuid;", up);
        Assert.Contains("ADD COLUMN \"InvalidatingAdditionId\" uuid;", up);
        Assert.Contains("CHECK", up); Assert.Contains("ON DELETE RESTRICT", up);
        Assert.Contains("IX_TimelineEntryFacts_InvalidatedByFactId", up);
        Assert.Contains("IX_TimelineFactInvalidations_InvalidatingAdditionId", up);
        Assert.DoesNotContain("UPDATE ", up); Assert.DoesNotContain("DELETE ", up.Replace("ON DELETE RESTRICT", ""));
        Assert.DoesNotContain("DROP ", up);
        Assert.Contains("WHERE \"InvalidatedByFactId\" IS NOT NULL", down);
        Assert.Contains("WHERE \"InvalidatingAdditionId\" IS NOT NULL", down);
        Assert.True(down.IndexOf("RAISE EXCEPTION", StringComparison.Ordinal) < down.IndexOf("DROP COLUMN", StringComparison.Ordinal));
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
