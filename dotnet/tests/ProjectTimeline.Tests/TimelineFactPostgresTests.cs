using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.DataAccessLogic;
using Xunit;
using Xunit.Sdk;

namespace ProjectTimeline.Tests;

public sealed class TimelineFactPostgresFactAttribute : FactAttribute
{
    public TimelineFactPostgresFactAttribute()
    {
        if (!TimelineFactPostgresSafety.Enabled(Environment.GetEnvironmentVariable("PROJECT_TIMELINE_FACTS_POSTGRES_TESTS")))
            Skip = "PostgreSQL opt-in disabled; no connection attempted.";
    }
}

// No IConfiguration, launchSettings, runsettings or application connection fallback.
public static class TimelineFactPostgresSafety
{
    public static bool Enabled(string? value) => value == "1";
    public static bool Owns(string name, string generated, bool created) => created && name == generated
        && name.StartsWith("timeline_facts_test_", StringComparison.Ordinal)
        && name.Length == 52 && Guid.TryParseExact(name[20..], "N", out _);
    public static string RequireConnection(string? flag, string? connection)
    {
        if (!Enabled(flag) || string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("Explicit PostgreSQL test opt-in and dedicated connection are required.");
        try
        {
            var parsed = new NpgsqlConnectionStringBuilder(connection);
            if (parsed.Database != "postgres" || string.IsNullOrWhiteSpace(parsed.Host) || string.IsNullOrWhiteSpace(parsed.Username))
                throw new InvalidOperationException();
            parsed.Pooling = false;
            parsed.IncludeErrorDetail = false;
            parsed.LogParameters = false;
            parsed.Timeout = 15;
            parsed.CommandTimeout = 30;
            return parsed.ConnectionString;
        }
        catch { throw new InvalidOperationException("Dedicated test connection must explicitly target the postgres maintenance database."); }
    }
}

public sealed class TimelineFactPostgresSafetyTests
{
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("true")] [InlineData("0")]
    public void Non_explicit_flags_cannot_reach_connection_parsing(string? flag)
    {
        Assert.False(TimelineFactPostgresSafety.Enabled(flag));
        Assert.Throws<InvalidOperationException>(() => TimelineFactPostgresSafety.RequireConnection(flag, "not a connection"));
    }

    [Fact]
    public void Opt_in_still_requires_dedicated_maintenance_connection()
    {
        Assert.Throws<InvalidOperationException>(() => TimelineFactPostgresSafety.RequireConnection("1", null));
        Assert.Throws<InvalidOperationException>(() => TimelineFactPostgresSafety.RequireConnection("1", "Host=localhost;Username=test;Database=projecttimeline"));
        Assert.Throws<InvalidOperationException>(() => TimelineFactPostgresSafety.RequireConnection("1", "malformed"));
    }

    [Fact]
    public void Cleanup_requires_exact_generated_name_and_successful_creation()
    {
        var owned = "timeline_facts_test_" + Guid.NewGuid().ToString("N");
        // Prefix length is part of the deletion boundary, not a caller-supplied database selector.
        Assert.True(TimelineFactPostgresSafety.Owns(owned, owned, true));
        Assert.False(TimelineFactPostgresSafety.Owns(owned, owned, false));
        Assert.False(TimelineFactPostgresSafety.Owns(owned, "timeline_facts_test_" + Guid.NewGuid().ToString("N"), true));
        Assert.False(TimelineFactPostgresSafety.Owns("projecttimeline", "projecttimeline", true));
        Assert.False(TimelineFactPostgresSafety.Owns(owned + "\";", owned + "\";", true));
    }
}

// These cases are intentionally unexecuted until administrator setup and explicit operator consent.
public sealed class TimelineFactPostgresTests
{
    private sealed class DisposableDatabase : IAsyncDisposable
    {
        private readonly string generated = "timeline_facts_test_" + Guid.NewGuid().ToString("N");
        private readonly string maintenance;
        private bool created;
        public DisposableDatabase() => maintenance = TimelineFactPostgresSafety.RequireConnection(
            Environment.GetEnvironmentVariable("PROJECT_TIMELINE_FACTS_POSTGRES_TESTS"),
            Environment.GetEnvironmentVariable("PROJECT_TIMELINE_FACTS_TEST_CONNECTION"));
        public ProjectTimelineDbContext Context(params IInterceptor[] interceptors)
        {
            if (!TimelineFactPostgresSafety.Owns(generated, generated, created)) throw new InvalidOperationException("Test database not owned.");
            var connection = new NpgsqlConnectionStringBuilder(maintenance) { Database = generated };
            return new(new DbContextOptionsBuilder<ProjectTimelineDbContext>()
                .UseNpgsql(connection.ConnectionString, x => x.MigrationsAssembly("ProjectTimeline.Database"))
                .AddInterceptors(interceptors).Options);
        }
        public async Task CreateAsync()
        {
            await using var connection = new NpgsqlConnection(maintenance);
            await connection.OpenAsync();
            // Identifier is generated internally, never taken from configuration. No FORCE or session termination.
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{generated}\" TEMPLATE template0", connection);
            await command.ExecuteNonQueryAsync();
            created = true;
            await using var db = Context();
            await db.Database.MigrateAsync();
        }
        public async ValueTask DisposeAsync()
        {
            if (!TimelineFactPostgresSafety.Owns(generated, generated, created)) return;
            await using var connection = new NpgsqlConnection(maintenance);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE \"{generated}\"", connection);
            await command.ExecuteNonQueryAsync();
            created = false;
        }
    }

    // No raw provider/connection exceptions (including cleanup failures) enter test output.
    private static async Task RunAsync(Func<DisposableDatabase, Task> body)
    {
        try
        {
            await using var database = new DisposableDatabase();
            await database.CreateAsync();
            await body(database);
        }
        // Assertion failures can also embed raw provider exceptions (e.g. ThrowsAsync mismatch).
        catch { throw new XunitException("Disposable PostgreSQL check failed (including assertions or cleanup). Inspect prerequisites/privileges privately; cleanup may require operator inspection of owned timeline_facts_test_* databases. No application database was selected."); }
    }

    private sealed record Seed(Guid Actor, TimelineFactScope Scope);
    private static async Task<Seed> SeedAsync(DisposableDatabase database)
    {
        await using var db = database.Context();
        var role = await db.UserRoles.FirstAsync();
        var actor = new User { Email = "relational@example.test", DisplayName = "Test", PasswordHash = "fake", IDUserRole = role.Id };
        var project = new Project { Code = "P", Name = "Test", OwnerId = actor.Id };
        var timeline = new Timeline { ProjectId = project.Id, Code = "T", Name = "Test" };
        var entry = new TimelineEntry { TimelineId = timeline.Id, AuthorId = actor.Id, EntryNumber = 1, Title = "Original", Content = "Alice owns delivery. Bob owns testing." };
        db.AddRange(actor, project, timeline, entry);
        await db.SaveChangesAsync();
        return new(actor.Id, new(project.Id, timeline.Id, entry.Id));
    }
    private static TimelineFactDraft Draft() => new([new("Alice owns delivery", "alice", "Person", "Alice owns delivery.")], [], []);
    private static async Task<Guid> PrepareAsync(DisposableDatabase database, Seed seed, TimelineFactDraft? draft = null)
    {
        await using var db = database.Context();
        var revision = await db.Timelines.Select(x => x.FactRevision).SingleAsync();
        return (await new TimelineFactService(db).PrepareAsync(seed.Scope, seed.Actor, false, revision, draft ?? Draft())).Id;
    }
    private sealed class FakeProvider : ITimelineFactProvider
    {
        public int Extractions;
        public int Embeddings;
        public string ExtractionModel => "fake-extractor";
        public Task<IReadOnlyList<TimelineFactCandidate>> ExtractAsync(TimelineFactExtraction source, CancellationToken ct)
        { Extractions++; return Task.FromResult(Draft().Additions); }
        public Task<float[]> EmbedAsync(string claim, CancellationToken ct)
        { Embeddings++; return Task.FromResult(Enumerable.Repeat(0.25f, 1536).ToArray()); }
    }

    [TimelineFactPostgresFact]
    public Task Migrations_frozen_model_and_vector_roundtrip_with_fake_provider() => RunAsync(async database =>
    {
        var seed = await SeedAsync(database);
        var id = await PrepareAsync(database, seed);
        await using (var db = database.Context())
        {
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.False(db.Database.HasPendingModelChanges());
            Assert.Equal("vector(1536)", db.Model.FindEntityType(typeof(TimelineFact))!.FindProperty(nameof(TimelineFact.Embedding))!.GetColumnType());
            Assert.Empty(await db.TimelineFacts.ToListAsync());
            await new TimelineFactService(db).AcceptAsync(seed.Scope, id, seed.Actor, false);
        }
        var provider = new FakeProvider();
        var processor = new TimelineFactProcessing(() => database.Context(), provider);
        Guid job;
        await using (var db = database.Context()) job = await db.TimelineFactJobs.Select(x => x.Id).SingleAsync();
        Assert.Equal("Completed", await processor.ProcessAsync(job, "test"));
        Assert.Equal("Completed", await processor.ProcessAsync(job, "replay"));
        Assert.Equal(1, provider.Embeddings);
        await using var reader = database.Context();
        var fact = await reader.TimelineFacts.SingleAsync();
        Assert.Equal(Enumerable.Repeat(0.25f, 1536), fact.Embedding!.ToArray());
        Assert.Equal("text-embedding-3-small", fact.EmbeddingModel);
        Assert.Equal("Original", (await reader.TimelineEntries.SingleAsync()).Title);
    });

    private sealed class InjectedFailure : Exception;
    private sealed class RollbackProbe : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            Assert.Equal(IsolationLevel.Serializable, eventData.Context!.Database.GetDbConnection().State == ConnectionState.Open
                ? eventData.Context.Database.CurrentTransaction!.GetDbTransaction().IsolationLevel : IsolationLevel.Unspecified);
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
            => throw new InjectedFailure();
    }

    [TimelineFactPostgresFact]
    public Task Serializable_acceptance_rolls_back_after_SQL_writes_before_commit() => RunAsync(async database =>
    {
        var seed = await SeedAsync(database);
        var first = await PrepareAsync(database, seed);
        Guid old;
        await using (var db = database.Context())
        {
            await new TimelineFactService(db).AcceptAsync(seed.Scope, first, seed.Actor, false);
            old = await db.TimelineFacts.Select(x => x.Id).SingleAsync();
        }
        var replacement = await PrepareAsync(database, seed, new([new("Bob owns testing", "bob", "Person", "Bob owns testing.")], [new(old, "Replaced", 0)], []));
        long revision;
        await using (var db = database.Context()) revision = await db.Timelines.Select(x => x.FactRevision).SingleAsync();
        await using (var db = database.Context(new RollbackProbe()))
            await Assert.ThrowsAsync<InjectedFailure>(() => new TimelineFactService(db).AcceptAsync(seed.Scope, replacement, seed.Actor, false));
        await using var reader = database.Context();
        var original = await reader.TimelineFacts.SingleAsync();
        Assert.True(original.Valid);
        Assert.Null(original.InvalidatedByFactId);
        Assert.Null(original.InvalidationReviewId);
        Assert.Null(original.InvalidatedAtUtc);
        Assert.Single(await reader.TimelineFactJobs.ToListAsync());
        Assert.Equal("Pending", (await reader.TimelineFactProposals.SingleAsync(x => x.Id == replacement)).Status);
        Assert.Equal(revision, await reader.Timelines.Select(x => x.FactRevision).SingleAsync());
        var causeId = await reader.TimelineFactAdditions.Where(x => x.ProposalId == replacement).Select(x => x.Id).SingleAsync();
        await new TimelineFactService(reader).AcceptAsync(seed.Scope, replacement, seed.Actor, false);
        Assert.Equal(causeId, original.InvalidatedByFactId);
        Assert.Equal(causeId, (await reader.TimelineFacts.SingleAsync(x => x.CreationReviewId == replacement)).Id);
        await new TimelineFactService(reader).AcceptAsync(seed.Scope, replacement, seed.Actor, false);
        Assert.Equal(2, await reader.TimelineFacts.CountAsync());
        Assert.Equal(2, await reader.TimelineFactJobs.CountAsync());
    });

    private sealed class ConcurrentSaveGate : SaveChangesInterceptor
    {
        private int arrived;
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref arrived) == 2) ready.SetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            return result;
        }
    }

    [TimelineFactPostgresFact]
    public Task Concurrent_acceptance_and_replay_have_no_duplicate_effects() => RunAsync(async database =>
    {
        var seed = await SeedAsync(database);
        var id = await PrepareAsync(database, seed);
        var gate = new ConcurrentSaveGate();
        async Task<bool> AcceptAsync()
        {
            await using var db = database.Context(gate);
            try { await new TimelineFactService(db).AcceptAsync(seed.Scope, id, seed.Actor, false); return true; }
            catch (TimelineFactConflictException) { return false; }
        }
        var outcomes = await Task.WhenAll(AcceptAsync(), AcceptAsync());
        Assert.Contains(true, outcomes);
        await using var reader = database.Context();
        await new TimelineFactService(reader).AcceptAsync(seed.Scope, id, seed.Actor, false);
        Assert.Single(await reader.TimelineFacts.ToListAsync());
        Assert.Single(await reader.TimelineFactJobs.ToListAsync());
        Assert.Equal("Accepted", (await reader.TimelineFactProposals.SingleAsync()).Status);
    });

    [TimelineFactPostgresFact]
    public Task Durable_extraction_replay_does_not_duplicate_pending_proposals() => RunAsync(async database =>
    {
        var seed = await SeedAsync(database);
        Guid job;
        await using (var db = database.Context())
        {
            var source = await db.TimelineEntries.SingleAsync();
            var work = TimelineFactProcessing.NewExtractionJob(source);
            db.TimelineFactJobs.Add(work); await db.SaveChangesAsync(); job = work.Id;
        }
        var provider = new FakeProvider();
        var processor = new TimelineFactProcessing(() => database.Context(), provider);
        Assert.Equal("Completed", await processor.ProcessAsync(job, "first"));
        Assert.Equal("Completed", await processor.ProcessAsync(job, "replay"));
        Assert.Equal(1, provider.Extractions);
        await using var reader = database.Context();
        Assert.Equal(job, (await reader.TimelineFactProposals.SingleAsync()).Id);
        Assert.Empty(await reader.TimelineFacts.ToListAsync());
        Assert.Equal("Pending", (await reader.TimelineFactProposals.SingleAsync()).Status);
        Assert.Equal(0, provider.Embeddings);
    });
}
