using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.DataAccessLogic;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class TimelineFactDirectExtractionTests
{
    private sealed class Fixture : IAsyncDisposable, ITimelineFactProvider
    {
        private readonly string database = Guid.NewGuid().ToString();
        private readonly InMemoryDatabaseRoot root = new();
        public ProjectTimelineDbContext Db() => new(new DbContextOptionsBuilder<ProjectTimelineDbContext>()
            .UseInMemoryDatabase(database, root).Options);
        public DbContext Raw()
        {
            using var db = Db();
            return new(new DbContextOptionsBuilder().UseInMemoryDatabase(database, root).UseModel(db.Model).Options);
        }
        public User Actor = new() { Email = "direct@example.test", DisplayName = "Author", PasswordHash = "test" };
        public Project Project = new() { Code = "P", Name = "Project" };
        public Timeline Timeline = new() { Code = "T", Name = "Timeline" };
        public TimelineEntry Entry = new() { EntryNumber = 1, Title = "Delivery", Content = "Alice owns delivery." };
        public TimelineFactJob Job = null!;
        public TimelineFactScope Scope => new(Project.Id, Timeline.Id, Entry.Id);
        public string ExtractionModel => "fake";
        public int Extractions, Embeddings;
        public Func<Task>? DuringExtraction;
        public async Task Initialize()
        {
            await using var db = Db();
            Project.OwnerId = Actor.Id; Timeline.ProjectId = Project.Id;
            Entry.TimelineId = Timeline.Id; Entry.AuthorId = Actor.Id;
            Job = TimelineFactProcessing.NewExtractionJob(Entry);
            db.AddRange(Actor, Project, Timeline, Entry, Job); await db.SaveChangesAsync();
        }
        public async Task<IReadOnlyList<TimelineFactCandidate>> ExtractAsync(TimelineFactExtraction source, CancellationToken ct)
        {
            Extractions++;
            if (DuringExtraction is not null) await DuringExtraction();
            return [new("Alice owns delivery", "alice", "Person", Entry.Content)];
        }
        public Task<float[]> EmbedAsync(string claim, CancellationToken ct)
        { Embeddings++; return Task.FromResult(Enumerable.Repeat(0.1f, 1536).ToArray()); }
        public async Task SeedHistoricalDirectAsync()
        {
            // Pre-cleanup rows remain effective and immutable. This is historical data, not a new write path.
            await using var raw = Raw();
            var job = await raw.Set<TimelineFactJob>().SingleAsync();
            var fact = new TimelineFact { EntryId = Entry.Id, ExtractionJobId = job.Id, Claim = "Alice owns delivery",
                EntityKey = "alice", EntityType = "Person", SourceQuote = Entry.Content, SourceFingerprint = job.SourceFingerprint };
            fact.CandidateKey = TimelineFactEvidence.CandidateKey(fact);
            job.State = "Completed"; job.ExtractionResultHash = TimelineFactEvidence.ResultHash([fact]);
            raw.Add(fact);
            raw.Add(new TimelineFactJob { EntryId = Entry.Id, FactId = fact.Id, Kind = "Embedding",
                WorkKey = $"embedding:{fact.Id:N}", SourceFingerprint = job.SourceFingerprint });
            await raw.SaveChangesAsync();
        }
        public Task<string> Run() => new TimelineFactProcessing(Db, this).ProcessAsync(Job.Id, "worker");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Generation_is_pending_not_searchable_not_queued_and_replay_stable()
    {
        await using var f = new Fixture(); await f.Initialize();
        await using var before = f.Db(); var revision = (await before.Timelines.SingleAsync()).FactRevision;
        Assert.Equal("Completed", await f.Run());
        await using var db = f.Db();
        Assert.Empty(await db.TimelineFacts.ToListAsync());
        var proposal = await db.TimelineFactProposals.SingleAsync();
        Assert.Equal(f.Job.Id, proposal.Id); Assert.Equal("Generated", proposal.Status);
        var addition = await db.TimelineFactAdditions.SingleAsync();
        var job = await db.TimelineFactJobs.SingleAsync();
        Assert.Equal(TimelineFactEvidence.ResultHash([TimelineFactEvidence.CandidateEvidence(addition)]), job.ExtractionResultHash);
        Assert.Equal(revision, job.ExtractionRevision);
        Assert.Equal(revision + 1, (await db.Timelines.SingleAsync()).FactRevision);
        Assert.Empty(await new TimelineFactSearch(db, f.Scope, f.Actor.Id, false).SearchAsync("Alice"));
        Assert.Empty((await new TimelineFactInformationSearch(db, new(f.Project.Id, f.Timeline.Id), f.Actor.Id, false).SearchAsync("Alice")).Facts);
        Assert.Empty(await new TimelineFactService(db).EffectiveAsync(f.Scope, f.Actor.Id, false));
        Assert.Equal("Completed", await f.Run()); Assert.Equal(1, f.Extractions); Assert.Equal(0, f.Embeddings);
        await using var replay = f.Db();
        Assert.Single(await replay.TimelineFactAdditions.ToListAsync()); Assert.Single(await replay.TimelineFactJobs.ToListAsync());
        Assert.Equal(revision + 1, (await replay.Timelines.SingleAsync()).FactRevision);
    }

    [Fact]
    public async Task Planner_and_information_citations_carry_direct_provenance_without_review_acceptance()
    {
        await using var f = new Fixture(); await f.Initialize(); await f.SeedHistoricalDirectAsync();
        await using var db = f.Db(); var fact = await db.TimelineFacts.SingleAsync();
        var fake = new FactReviewFakeAgent { Result = new() };
        var plan = await new TimelineFactReviewPlanner(db, f.Scope, f.Actor.Id, false,
            new ProjectTimeline.BusinessLogic.Agents.TimelineFactOptimizationAgent(fake)).PlanAsync();
        using var input = System.Text.Json.JsonDocument.Parse(fake.Message!);
        var seed = Assert.Single(input.RootElement.GetProperty("seed_facts").EnumerateArray());
        Assert.Equal(f.Job.Id, seed.GetProperty("ExtractionJobId").GetGuid());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, seed.GetProperty("CreationReviewId").ValueKind);
        Assert.Empty(plan.Draft.Additions); Assert.Empty(await db.TimelineFactProposals.ToListAsync());
        var information = new InformationFakeAgent { Result = new() { Content = "Alice owns delivery.", CitedFactIds = [fact.Id] } };
        var response = await new ProjectTimeline.BusinessLogic.Agents.SearchEntriesAgent(db,
            new(f.Project.Id, f.Timeline.Id), f.Actor.Id, false, () => information).RunAsync("Alice");
        var citation = Assert.Single(response.Citations);
        Assert.Equal(f.Job.Id, citation.ExtractionJobId); Assert.Null(citation.CreationReviewId);
    }

    [Fact]
    public async Task Later_review_preserves_direct_history_and_normal_review_origin()
    {
        await using var f = new Fixture(); await f.Initialize(); await f.SeedHistoricalDirectAsync();
        await using var db = f.Db();
        var original = await db.TimelineFacts.SingleAsync();
        var correction = new TimelineEntry { TimelineId = f.Timeline.Id, AuthorId = f.Actor.Id,
            EntryNumber = f.Entry.EntryNumber + 1, Title = f.Entry.Title, Content = f.Entry.Content };
        db.Add(correction); await db.SaveChangesAsync();
        var correctionScope = f.Scope with { EntryId = correction.Id };
        var service = new TimelineFactService(db);
        var review = await service.PrepareAsync(correctionScope, f.Actor.Id, false, (await db.Timelines.SingleAsync()).FactRevision,
            new([new("Delivery belongs to Alice", "alice", "Person", f.Entry.Content)], [new(original.Id, "Rephrased", 0)], []));
        Assert.True((await db.TimelineFacts.AsNoTracking().SingleAsync()).Valid);
        await service.AcceptAsync(correctionScope, review.Id, f.Actor.Id, false);
        await service.AcceptAsync(correctionScope, review.Id, f.Actor.Id, false);
        var replacement = Assert.Single(await service.EffectiveAsync(correctionScope, f.Actor.Id, false));
        Assert.Equal(review.Id, replacement.CreationReviewId); Assert.Null(replacement.ExtractionJobId);
        Assert.False(original.Valid); Assert.Equal(review.Id, original.InvalidationReviewId); Assert.Equal(f.Job.Id, original.ExtractionJobId);
        Assert.Equal(replacement.Id, original.InvalidatedByFactId);
        Assert.Equal(f.Entry.Id, original.EntryId); Assert.Equal(correction.Id, replacement.EntryId);
        Assert.Equal(f.Entry.Content, original.SourceQuote); Assert.Equal(correction.Content, replacement.SourceQuote);
        Assert.Equal(2, await db.TimelineFacts.CountAsync());
        Assert.Equal("Skipped", await new TimelineFactProcessing(f.Db, f).ProcessAsync(
            (await db.TimelineFactJobs.SingleAsync(x => x.FactId == original.Id)).Id, "embed-test"));
        Assert.Equal(0, f.Embeddings);
    }

    [Theory]
    [InlineData("missing-job")]
    [InlineData("pending-job")]
    [InlineData("mixed-review")]
    [InlineData("wrong-entry")]
    [InlineData("wrong-source")]
    [InlineData("quote")]
    [InlineData("candidate-key")]
    [InlineData("forged-cause")]
    [InlineData("result-hash")]
    [InlineData("missing-embedding")]
    [InlineData("expired-lease")]
    [InlineData("missing-lease")]
    [InlineData("stale-revision")]
    [InlineData("duplicate")]
    public async Task Guard_rejects_forged_or_partial_completion_atomically(string attack)
    {
        await using var f = new Fixture(); await f.Initialize();
        await using (var claim = f.Db())
        {
            var job = await claim.TimelineFactJobs.SingleAsync();
            job.State = "Running"; job.Model = "fake"; job.LeaseOwner = "worker";
            job.LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(3);
            job.ExtractionRevision = (await claim.Timelines.SingleAsync()).FactRevision;
            if (attack == "expired-lease") job.LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
            if (attack == "missing-lease") job.LeaseExpiresAtUtc = null;
            if (attack == "stale-revision") job.ExtractionRevision--;
            await claim.SaveChangesAsync();
        }
        await using (var completion = f.Db())
        {
            var job = await completion.TimelineFactJobs.SingleAsync();
            var fact = new TimelineFact { EntryId = f.Entry.Id, ExtractionJobId = job.Id, Claim = "Alice owns delivery",
                EntityKey = "alice", EntityType = "Person", SourceQuote = f.Entry.Content, SourceFingerprint = job.SourceFingerprint };
            switch (attack)
            {
                case "missing-job": fact.ExtractionJobId = Guid.NewGuid(); break;
                case "mixed-review": fact.CreationReviewId = Guid.NewGuid(); fact.AdditionId = Guid.NewGuid(); break;
                case "wrong-entry": fact.EntryId = Guid.NewGuid(); break;
                case "wrong-source": fact.SourceFingerprint = "wrong"; break;
                case "quote": fact.SourceQuote = "Unsupported"; break;
                case "forged-cause": fact.InvalidatedByFactId = Guid.NewGuid(); break;
            }
            fact.CandidateKey = TimelineFactEvidence.CandidateKey(fact);
            job.State = "Completed"; job.LeaseOwner = null; job.LeaseExpiresAtUtc = null;
            job.ExtractionResultHash = TimelineFactEvidence.ResultHash([fact]);
            if (attack == "candidate-key") fact.CandidateKey = "wrong";
            if (attack == "result-hash") job.ExtractionResultHash = "wrong";
            if (attack == "pending-job") job.State = "Pending";
            completion.Add(fact);
            if (attack == "duplicate") completion.Add(new TimelineFact { EntryId = fact.EntryId, ExtractionJobId = job.Id,
                Claim = fact.Claim, EntityKey = fact.EntityKey, EntityType = fact.EntityType, SourceQuote = fact.SourceQuote,
                SourceFingerprint = fact.SourceFingerprint, CandidateKey = fact.CandidateKey });
            if (attack != "missing-embedding") completion.Add(new TimelineFactJob { EntryId = fact.EntryId, FactId = fact.Id,
                Kind = "Embedding", WorkKey = $"embedding:{fact.Id:N}", SourceFingerprint = fact.SourceFingerprint });
            await Assert.ThrowsAsync<TimelineFactConflictException>(() => completion.SaveChangesAsync());
        }
        await using var verify = f.Db();
        Assert.Empty(await verify.TimelineFacts.ToListAsync()); Assert.Single(await verify.TimelineFactJobs.ToListAsync());
        Assert.Equal("Running", (await verify.TimelineFactJobs.SingleAsync()).State);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("authorization")]
    [InlineData("competing-work")]
    public async Task Concurrent_scope_change_discards_provider_result(string change)
    {
        await using var f = new Fixture(); await f.Initialize();
        f.DuringExtraction = async () =>
        {
            await using var db = f.Db();
            if (change == "authorization") (await db.Users.SingleAsync()).IsActive = false;
            else if (change == "competing-work")
            {
                // Simulate an external writer that did not CAS the timeline revision.
                await using var raw = f.Raw();
                raw.Add(new TimelineFactJob { EntryId = f.Entry.Id, Kind = "Embedding", WorkKey = "external" });
                await raw.SaveChangesAsync();
            }
            else db.Add(new TimelineEntry { TimelineId = f.Timeline.Id, AuthorId = f.Actor.Id, EntryNumber = 2, Title = "Later", Content = "New entry" });
            await db.SaveChangesAsync();
        };
        Assert.Equal("Retry", await f.Run());
        await using var verify = f.Db(); Assert.Empty(await verify.TimelineFacts.ToListAsync());
        Assert.Empty(await verify.TimelineFactProposals.ToListAsync());
        Assert.Equal(change == "competing-work" ? 2 : 1, await verify.TimelineFactJobs.CountAsync());
    }

    [Fact]
    public async Task Existing_accepted_review_evidence_is_not_reextracted_or_replaced()
    {
        await using var f = new Fixture(); await f.Initialize();
        await using (var db = f.Db())
        {
            var service = new TimelineFactService(db);
            var proposal = await service.PrepareAsync(f.Scope, f.Actor.Id, false, (await db.Timelines.SingleAsync()).FactRevision,
                new([new("Alice owns delivery", "alice", "Person", f.Entry.Content)], [], []));
            await service.AcceptAsync(f.Scope, proposal.Id, f.Actor.Id, false);
        }
        Assert.Equal("Retry", await f.Run()); Assert.Equal(0, f.Extractions);
        await using var verify = f.Db();
        Assert.Single(await verify.TimelineFacts.ToListAsync());
        Assert.Equal("Accepted", (await verify.TimelineFactProposals.SingleAsync()).Status);
        Assert.Single(await new TimelineFactService(verify).EffectiveAsync(f.Scope, f.Actor.Id, false));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("job-source")]
    [InlineData("job-kind")]
    [InlineData("job-entry")]
    [InlineData("incomplete")]
    public async Task Invalid_persisted_provenance_never_becomes_search_or_embedding_evidence(string attack)
    {
        await using var f = new Fixture(); await f.Initialize(); await f.SeedHistoricalDirectAsync();
        await using (var raw = f.Raw())
        {
            var fact = await raw.Set<TimelineFact>().SingleAsync();
            var job = await raw.Set<TimelineFactJob>().SingleAsync(x => x.Id == f.Job.Id);
            if (attack == "source") fact.SourceFingerprint = "wrong";
            if (attack == "job-source") job.SourceFingerprint = "wrong";
            if (attack == "job-kind") job.Kind = "Embedding";
            if (attack == "job-entry") job.EntryId = Guid.NewGuid();
            if (attack == "incomplete") job.ExtractionResultHash = null;
            await raw.SaveChangesAsync();
        }
        await using var verify = f.Db();
        Assert.Empty(await new TimelineFactSearch(verify, f.Scope, f.Actor.Id, false).SearchAsync("alice"));
        Assert.Empty((await new TimelineFactInformationSearch(verify, new(f.Project.Id, f.Timeline.Id), f.Actor.Id, false).SearchAsync("alice")).Facts);
        var embedding = await verify.TimelineFactJobs.SingleAsync(x => x.FactId != null);
        Assert.Equal("Skipped", await new TimelineFactProcessing(f.Db, f).ProcessAsync(embedding.Id, "embed-test"));
        Assert.Equal(0, f.Embeddings);
    }

    [Fact]
    public async Task Source_change_during_provider_call_discards_result_without_losing_original_evidence()
    {
        await using var f = new Fixture(); await f.Initialize();
        f.DuringExtraction = async () =>
        {
            await using var raw = f.Raw();
            (await raw.Set<TimelineEntry>().SingleAsync()).Content = "Externally changed source";
            await raw.SaveChangesAsync();
        };
        Assert.Equal("Retry", await f.Run());
        await using var verify = f.Db(); Assert.Empty(await verify.TimelineFacts.ToListAsync());
        Assert.Equal(f.Job.SourceFingerprint, (await verify.TimelineFactJobs.SingleAsync()).SourceFingerprint);
    }

    [Fact]
    public async Task Completed_job_and_fact_origin_cannot_be_reused_for_additional_insertions()
    {
        await using var f = new Fixture(); await f.Initialize(); await f.SeedHistoricalDirectAsync();
        await using (var db = f.Db())
        {
            var fact = await db.TimelineFacts.SingleAsync();
            db.Add(new TimelineFact { EntryId = f.Entry.Id, ExtractionJobId = f.Job.Id, Claim = fact.Claim,
                EntityKey = fact.EntityKey, EntityType = fact.EntityType, SourceQuote = fact.SourceQuote,
                SourceFingerprint = fact.SourceFingerprint, CandidateKey = fact.CandidateKey });
            await Assert.ThrowsAsync<TimelineFactConflictException>(() => db.SaveChangesAsync());
        }
        await using (var db = f.Db())
        {
            (await db.TimelineFactJobs.SingleAsync(x => x.Id == f.Job.Id)).ExtractionResultHash = "rewritten";
            await Assert.ThrowsAsync<TimelineFactConflictException>(() => db.SaveChangesAsync());
        }
        await using (var db = f.Db())
        {
            db.Add(new TimelineFact { EntryId = f.Entry.Id, Claim = "Alice owns delivery", SourceQuote = f.Entry.Content,
                SourceFingerprint = f.Job.SourceFingerprint });
            await Assert.ThrowsAsync<TimelineFactConflictException>(() => db.SaveChangesAsync());
        }
        await using var verify = f.Db(); Assert.Single(await verify.TimelineFacts.ToListAsync());
    }

    [Fact]
    public void Forward_migration_preserves_history_and_nullable_unique_semantics_offline()
    {
        using var db = new ProjectTimelineDbContext(new DbContextOptionsBuilder<ProjectTimelineDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused", x => x.MigrationsAssembly("ProjectTimeline.Database")).Options);
        var sql = db.GetService<IMigrator>().GenerateScript("20261018000100_TimelineFacts", "20261018000200_DirectTimelineFactExtraction");
        Assert.Contains("ALTER COLUMN \"CreationReviewId\" DROP NOT NULL", sql);
        Assert.Contains("ALTER COLUMN \"AdditionId\" DROP NOT NULL", sql);
        Assert.Contains("CREATE UNIQUE INDEX \"IX_TimelineEntryFacts_ExtractionJobId_CandidateKey\"", sql);
        Assert.Contains("CK_TimelineEntryFacts_Origin", sql); Assert.Contains("ON DELETE RESTRICT", sql);
        Assert.DoesNotContain("DROP TABLE", sql); Assert.DoesNotContain("DELETE FROM", sql); Assert.DoesNotContain("NULLS NOT DISTINCT", sql);
        Assert.False(db.Database.HasPendingModelChanges());
        // Translation only; no connection is opened and no PostgreSQL behavior is claimed.
        var query = TimelineFactEvidence.Effective(db, Guid.NewGuid()).ToQueryString();
        Assert.Contains("TimelineFactJobs", query); Assert.Contains("TimelineFactProposals", query);
    }
}
