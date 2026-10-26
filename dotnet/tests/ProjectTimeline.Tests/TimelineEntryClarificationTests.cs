using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.DataAccessLogic;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class TimelineEntryClarificationTests
{
    private sealed class Fixture
    {
        private readonly DbContextOptions<ProjectTimelineDbContext> options = new DbContextOptionsBuilder<ProjectTimelineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        public ProjectTimelineDbContext Db() => new(options);
        public User Actor = new() { Email = "clarify@example.test", DisplayName = "Author", PasswordHash = "test" };
        public Project Project = new() { Code = "P", Name = "Project" };
        public Timeline Timeline = new() { Code = "T", Name = "Timeline" };
        public TimelineEntry Source = null!;
        public Guid Generation;
        public TimelineFactScope Scope => new(Project.Id, Timeline.Id, Source.Id);
        public async Task Initialize()
        {
            await using var db = Db();
            Project.OwnerId = Actor.Id; Timeline.ProjectId = Project.Id;
            db.AddRange(Actor, Project, Timeline); await db.SaveChangesAsync();
            Source = await new TimelineService(db).AddEntryAsync(Timeline.Id, Actor.Id, "Delivery", "They own delivery.", default);
            var job = await db.TimelineFactJobs.SingleAsync();
            Generation = job.Id; job.State = "Running"; job.LeaseOwner = "worker";
            job.LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(5); job.ExtractionRevision = (await db.Timelines.SingleAsync()).FactRevision;
            await db.SaveChangesAsync();
        }
        public async Task<Guid[]> Pause()
        {
            await using var db = Db();
            var job = await db.TimelineFactJobs.SingleAsync(x => x.Id == Generation);
            await db.StageEntryQuestionsForLeaseAsync(Generation, "worker", job.Version,
                [new("owner", "Who owns delivery?"), new("date", "When is delivery?")]);
            await db.SaveChangesAsync();
            return await db.TimelineEntryClarifications.OrderBy(x => x.QuestionKey).Select(x => x.Id).ToArrayAsync();
        }
    }

    [Fact]
    public async Task Direct_ORM_question_creation_cannot_forge_trusted_extraction_origin()
    {
        var f = new Fixture(); await f.Initialize(); await using var db = f.Db();
        db.TimelineEntryClarifications.Add(new() { EntryId = f.Source.Id, GenerationId = f.Generation,
            SourceFingerprint = TimelineFactEvidence.Fingerprint(f.Source), QuestionKey = "owner", Question = "Who owns delivery?" });
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Persisted_human_provenance_and_ordered_immutable_snapshot_preserve_source_and_legacy_hash()
    {
        var f = new Fixture(); await f.Initialize(); var ids = await f.Pause();
        await using var db = f.Db(); var service = new TimelineEntryClarificationService(db);
        var originalHash = TimelineFactEvidence.Fingerprint(f.Source);
        Assert.Equal(originalHash, TimelineEntryClarificationEvidence.EffectiveFingerprint(f.Source, []));
        var before = DateTime.UtcNow;
        await service.AnswerAsync(f.Scope, f.Generation, ids[1], f.Actor.Id, false, "Alice");
        var first = await service.AnswersAsync(f.Scope, f.Actor.Id, false);
        Assert.Single(first); Assert.Equal(f.Actor.Id, first[0].AnsweredById);
        Assert.InRange(first[0].AnsweredAtUtc, before, DateTime.UtcNow);
        var firstHash = TimelineEntryClarificationEvidence.EffectiveFingerprint(f.Source, first);
        Assert.NotEqual(originalHash, firstHash);
        await service.AnswerAsync(f.Scope, f.Generation, ids[0], f.Actor.Id, false, "Friday");
        var all = await service.AnswersAsync(f.Scope, f.Actor.Id, false);
        Assert.Equal(new[] { "date", "owner" }, all.Select(x => x.QuestionKey));
        Assert.Single(first); // Snapshot is detached and immutable, not a live collection.
        Assert.NotEqual(firstHash, TimelineEntryClarificationEvidence.EffectiveFingerprint(f.Source, all));
        Assert.Equal(TimelineEntryClarificationEvidence.EffectiveFingerprint(f.Source, all),
            TimelineEntryClarificationEvidence.EffectiveFingerprint(f.Source, all.Reverse()));
        var job = await db.TimelineFactJobs.AsNoTracking().SingleAsync();
        var bound = TimelineEntryClarificationEvidence.BindToJob(job, f.Source, all);
        Assert.Equal(job.Id, bound.JobId); Assert.Equal(2, bound.Answers.Count);
        job.EntryId = Guid.NewGuid();
        Assert.Throws<TimelineFactConflictException>(() => TimelineEntryClarificationEvidence.BindToJob(job, f.Source, all));
        var stored = await db.TimelineEntryClarifications.AsNoTracking().SingleAsync(x => x.Id == ids[1]);
        Assert.Equal("Extraction", stored.Origin); Assert.True(stored.CreatedAtUtc >= before.AddMinutes(-1));
        Assert.Equal("Alice", stored.Answer);
        Assert.Equal(f.Source.Content, (await db.TimelineEntries.AsNoTracking().SingleAsync()).Content);
        Assert.Equal(originalHash, TimelineFactEvidence.Fingerprint(await db.TimelineEntries.AsNoTracking().SingleAsync()));
        Assert.Empty(await db.TimelineFacts.ToListAsync());
    }

    [Fact]
    public async Task Consistent_duplicate_is_idempotent_but_replacement_or_foreign_scope_is_rejected()
    {
        var f = new Fixture(); await f.Initialize(); var ids = await f.Pause();
        await using var db = f.Db(); var service = new TimelineEntryClarificationService(db);
        await service.AnswerAsync(f.Scope, f.Generation, ids[0], f.Actor.Id, false, "Friday");
        var row = await db.TimelineEntryClarifications.AsNoTracking().SingleAsync(x => x.Id == ids[0]);
        await service.AnswerAsync(f.Scope, f.Generation, ids[0], f.Actor.Id, false, "Friday");
        var duplicate = await db.TimelineEntryClarifications.AsNoTracking().SingleAsync(x => x.Id == ids[0]);
        Assert.Equal(row.Version, duplicate.Version); Assert.Equal(row.AnsweredAtUtc, duplicate.AnsweredAtUtc);
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => service.AnswerAsync(f.Scope, f.Generation, ids[0], f.Actor.Id, false, "Monday"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.AnswerAsync(f.Scope, f.Generation, ids[1], Guid.Empty, false, "Friday"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.AnswersAsync(f.Scope, Guid.NewGuid(), false));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.AnswerAsync(f.Scope with { EntryId = Guid.NewGuid() }, f.Generation, ids[1], f.Actor.Id, false, "Friday"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.AnswerAsync(f.Scope, Guid.NewGuid(), ids[1], f.Actor.Id, false, "Friday"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.AnswersAsync(f.Scope with { TimelineId = Guid.NewGuid() }, f.Actor.Id, false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.AnswersAsync(f.Scope with { ProjectId = Guid.NewGuid() }, f.Actor.Id, false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_change_and_even_restore_revokes_old_generation_answers(bool restore)
    {
        var f = new Fixture(); await f.Initialize(); var ids = await f.Pause();
        await using (var edit = f.Db())
        {
            var source = await edit.TimelineEntries.SingleAsync(); source.Content = "Changed source";
            await edit.SaveChangesAsync();
            if (restore) { source.Content = f.Source.Content; await edit.SaveChangesAsync(); }
        }
        await using var db = f.Db(); var service = new TimelineEntryClarificationService(db);
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => service.AnswerAsync(f.Scope, f.Generation, ids[0], f.Actor.Id, false, "Friday"));
        Assert.Empty(await service.AnswersAsync(f.Scope, f.Actor.Id, false));
        Assert.Equal(2, await db.TimelineEntryClarifications.CountAsync());
    }

    [Theory]
    [InlineData("answer")]
    [InlineData("question")]
    [InlineData("delete")]
    [InlineData("provenance")]
    public async Task ORM_cannot_replace_delete_or_forge_answered_evidence(string mutation)
    {
        var f = new Fixture(); await f.Initialize(); var ids = await f.Pause();
        await using (var answer = f.Db())
            await new TimelineEntryClarificationService(answer).AnswerAsync(f.Scope, f.Generation, ids[0], f.Actor.Id, false, "Friday");
        await using var db = f.Db(); var row = await db.TimelineEntryClarifications.SingleAsync(x => x.Id == ids[0]);
        if (mutation == "answer") row.Answer = "Monday";
        if (mutation == "question") row.Question = "Replacement?";
        if (mutation == "delete") db.Remove(row);
        if (mutation == "provenance") row.AnsweredById = Guid.NewGuid();
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Competing_context_answers_cannot_overwrite_the_winner()
    {
        var f = new Fixture(); await f.Initialize(); var ids = await f.Pause();
        await using var left = f.Db(); await using var right = f.Db();
        await left.StageEntryAnswerAsync(ids[0], f.Actor.Id, "Friday");
        await right.StageEntryAnswerAsync(ids[0], f.Actor.Id, "Monday");
        await left.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => right.SaveChangesAsync());
        await using var verify = f.Db();
        Assert.Equal("Friday", (await verify.TimelineEntryClarifications.SingleAsync(x => x.Id == ids[0])).Answer);
    }

    [Fact]
    public async Task Competing_lease_or_expired_lease_cannot_persist_questions()
    {
        var f = new Fixture(); await f.Initialize();
        await using var stale = f.Db(); var job = await stale.TimelineFactJobs.SingleAsync();
        await stale.StageEntryQuestionsForLeaseAsync(job.Id, "worker", job.Version, [new("owner", "Who?")]);
        await using (var renew = f.Db())
        {
            var current = await renew.TimelineFactJobs.SingleAsync(); current.LeaseOwner = "other"; await renew.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => stale.SaveChangesAsync());
        await using var expired = f.Db(); job = await expired.TimelineFactJobs.SingleAsync();
        job.LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1); await expired.SaveChangesAsync();
        await expired.StageEntryQuestionsForLeaseAsync(job.Id, "other", job.Version, [new("owner", "Who?")]);
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => expired.SaveChangesAsync());
    }

    [Fact]
    public async Task Bounded_questions_answers_and_malformed_snapshots_fail_closed()
    {
        var f = new Fixture(); await f.Initialize();
        await using (var db = f.Db())
        {
            var job = await db.TimelineFactJobs.SingleAsync();
            foreach (var questions in new TimelineEntryQuestion[][] { [], [new("", "Who?")], [new("key", new string('x', 2001))],
                [new("same", "Who?"), new("same", "When?")], Enumerable.Range(0, 21).Select(i => new TimelineEntryQuestion(i.ToString(), "Who?")).ToArray() })
                await Assert.ThrowsAsync<TimelineFactConflictException>(() => db.StageEntryQuestionsForLeaseAsync(job.Id, "worker", job.Version, questions));
        }
        var ids = await f.Pause();
        await using var answerDb = f.Db(); var service = new TimelineEntryClarificationService(answerDb);
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => service.AnswerAsync(f.Scope, f.Generation, ids[0], f.Actor.Id, false, new string('x', 4001)));
        await service.AnswerAsync(f.Scope, f.Generation, ids[0], f.Actor.Id, false, "Friday");
        var valid = (await service.AnswersAsync(f.Scope, f.Actor.Id, false)).Single();
        foreach (var invalid in new[] { valid with { EntryId = Guid.NewGuid() }, valid with { SourceFingerprint = "wrong" },
            valid with { Answer = "" }, valid with { AnsweredById = Guid.Empty }, valid with { AnsweredAtUtc = default } })
            Assert.Throws<TimelineFactConflictException>(() => TimelineEntryClarificationEvidence.Snapshot(f.Source, [invalid]));
        Assert.Throws<TimelineFactConflictException>(() => TimelineEntryClarificationEvidence.Snapshot(f.Source, [valid, valid]));
        Assert.Throws<TimelineFactConflictException>(() => TimelineEntryClarificationEvidence.Snapshot(f.Source, [null!]));
    }

    [Fact]
    public async Task Active_foreign_actor_and_revoked_writer_cannot_answer_or_forge_ORM_provenance()
    {
        var f = new Fixture(); await f.Initialize(); var ids = await f.Pause();
        var foreign = new User { Email = "foreign@example.test", DisplayName = "Foreign", PasswordHash = "test" };
        await using (var setup = f.Db()) { setup.Users.Add(foreign); await setup.SaveChangesAsync(); }
        await using (var db = f.Db())
        {
            var service = new TimelineEntryClarificationService(db);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.AnswerAsync(f.Scope, f.Generation, ids[0], foreign.Id, false, "Friday"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReadAsync(f.Scope, foreign.Id, false));
            await db.StageEntryAnswerAsync(ids[0], foreign.Id, "Friday");
            await Assert.ThrowsAsync<TimelineFactConflictException>(() => db.SaveChangesAsync());
        }
        await using (var revoke = f.Db()) { (await revoke.Users.SingleAsync(x => x.Id == f.Actor.Id)).IsActive = false; await revoke.SaveChangesAsync(); }
        await using var inactive = f.Db();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new TimelineEntryClarificationService(inactive)
            .AnswerAsync(f.Scope, f.Generation, ids[0], f.Actor.Id, false, "Friday"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Staged_actual_answer_provenance_cannot_be_replaced_before_save(bool changeText)
    {
        var f = new Fixture(); await f.Initialize(); var ids = await f.Pause();
        await using var db = f.Db();
        await db.StageEntryAnswerAsync(ids[0], f.Actor.Id, "Friday");
        var row = await db.TimelineEntryClarifications.SingleAsync(x => x.Id == ids[0]);
        if (changeText) row.Answer = "Forged replacement";
        else row.AnsweredAtUtc = DateTime.UtcNow.AddDays(-10);
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Concurrent_generation_revocation_conflicts_with_staged_answer()
    {
        var f = new Fixture(); await f.Initialize(); var ids = await f.Pause();
        await using var answer = f.Db();
        await answer.TimelineFactJobs.SingleAsync(); // Capture old generation CAS.
        await answer.StageEntryAnswerAsync(ids[0], f.Actor.Id, "Friday");
        await using (var revoke = f.Db())
        {
            var job = await revoke.TimelineFactJobs.SingleAsync(); job.State = "Superseded"; await revoke.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<TimelineFactConflictException>(() => answer.SaveChangesAsync());
    }

    [Fact]
    public void Migration_only_adds_clarification_storage_and_preserves_legacy_origin_and_FTS()
    {
        using var db = new ProjectTimelineDbContext(new DbContextOptionsBuilder<ProjectTimelineDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused", x => x.MigrationsAssembly("ProjectTimeline.Database")).Options);
        var migrator = db.GetService<IMigrator>();
        var sql = migrator.GenerateScript("20261025163000_TimelineFactFullTextIndex", "20261025223000_EntryExtractionClarifications");
        Assert.Contains("CREATE TABLE \"TimelineEntryClarifications\"", sql);
        Assert.Contains("CK_EntryClarification_Answer", sql);
        Assert.DoesNotContain("UPDATE ", sql); Assert.DoesNotContain("DELETE FROM ", sql); Assert.DoesNotContain("DROP ", sql);
        var target = new ProjectTimeline.Database.Migrations.EntryExtractionClarifications().TargetModel;
        var facts = target.FindEntityType(typeof(TimelineFact).FullName!)!;
        Assert.Contains(facts.GetIndexes(), x => x.GetDatabaseName() == "IX_TimelineEntryFacts_FullText");
        Assert.Contains("\"SourceKind\" = 'Entry'", facts.GetCheckConstraints().Single(x => x.Name == "CK_TimelineEntryFacts_Origin").Sql);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Adding_actual_answer_preserves_existing_accepted_facts_and_their_evidence()
    {
        var f = new Fixture(); await f.Initialize();
        // Establish independent historical accepted evidence through the existing normal review path.
        await using (var setup = f.Db())
        {
            var history = await new TimelineService(setup).AddEntryAsync(f.Timeline.Id, f.Actor.Id, "History", "Alice owns history.", default);
            var service = new TimelineFactService(setup);
            var revision = (await setup.Timelines.SingleAsync()).FactRevision;
            var scope = new TimelineFactScope(f.Project.Id, f.Timeline.Id, history.Id);
            var proposal = await service.PrepareAsync(scope, f.Actor.Id, false, revision,
                new([new("Alice owns history", "alice", "Person", "Alice owns history.")], [], []));
            await service.AcceptAsync(scope, proposal.Id, f.Actor.Id, false);
            var job = await setup.TimelineFactJobs.SingleAsync(x => x.Id == f.Generation);
            job.ExtractionRevision = (await setup.Timelines.SingleAsync()).FactRevision;
            await setup.SaveChangesAsync();
        }
        var ids = await f.Pause();
        await using var db = f.Db(); var before = await db.TimelineFacts.AsNoTracking().SingleAsync();
        await new TimelineEntryClarificationService(db).AnswerAsync(f.Scope, f.Generation, ids[0], f.Actor.Id, false, "Friday");
        var after = await db.TimelineFacts.AsNoTracking().SingleAsync();
        Assert.Equal(before.Claim, after.Claim); Assert.Equal(before.SourceFingerprint, after.SourceFingerprint);
        Assert.Equal(before.ClarificationEvidence, after.ClarificationEvidence); Assert.Equal(before.UpdatedAtUtc, after.UpdatedAtUtc);
        Assert.True(after.Valid);
    }
}

