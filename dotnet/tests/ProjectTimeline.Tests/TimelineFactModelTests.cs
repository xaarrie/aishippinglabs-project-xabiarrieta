using Microsoft.EntityFrameworkCore;
using ProjectTimeline.DataAccessLogic;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace ProjectTimeline.Tests;

public sealed class TimelineFactModelTests
{
    [Fact]
    public void Facts_proposals_and_jobs_have_separate_persistent_models()
    {
        using var db = new ProjectTimelineDbContext(new DbContextOptionsBuilder<ProjectTimelineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        foreach (var name in new[] { "TimelineFact", "TimelineFactProposal", "TimelineFactAddition", "TimelineFactInvalidation", "TimelineFactJob" })
            Assert.NotNull(db.Model.FindEntityType($"ProjectTimeline.DataAccessLogic.{name}"));
        Assert.Null(db.Model.FindEntityType(typeof(TimelineFact))!.FindProperty(nameof(TimelineFact.Embedding)));
        Assert.True(db.Model.FindEntityType(typeof(Timeline))!.FindProperty(nameof(Timeline.FactRevision))!.IsConcurrencyToken);
        Assert.True(db.Model.FindEntityType(typeof(TimelineFactJob))!.FindProperty(nameof(TimelineFactJob.Version))!.IsConcurrencyToken);
    }

    [Fact]
    public void Effective_facts_use_requested_physical_table_without_renaming_CLR_contract()
    {
        using var db = new ProjectTimelineDbContext(new DbContextOptionsBuilder<ProjectTimelineDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused", x => x.MigrationsAssembly("ProjectTimeline.Database")).Options);
        var target = new ProjectTimeline.Database.Migrations.TimelineFacts().TargetModel;
        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot!.Model;
        foreach (var model in new[] { db.Model, target, snapshot })
        {
            var fact = model.FindEntityType(typeof(TimelineFact).FullName!)!;
            Assert.Equal("TimelineEntryFacts", fact.GetTableName());
            Assert.DoesNotContain(model.GetEntityTypes(), x => x.GetTableName() == "TimelineFacts");
            Assert.Equal("PK_TimelineEntryFacts", fact.FindPrimaryKey()!.GetName());
            Assert.All(fact.GetIndexes(), x => Assert.StartsWith("IX_TimelineEntryFacts_", x.GetDatabaseName()));
            Assert.All(fact.GetForeignKeys(), x => Assert.StartsWith("FK_TimelineEntryFacts_", x.GetConstraintName()));
            Assert.All(fact.GetReferencingForeignKeys(), x => Assert.Contains("_TimelineEntryFacts_", x.GetConstraintName()));
        }
        Assert.Same(db.Set<TimelineFact>(), db.TimelineFacts);
    }

    [Fact]
    public void Facts_migration_scripts_only_create_reference_and_drop_requested_physical_table()
    {
        using var db = new ProjectTimelineDbContext(new DbContextOptionsBuilder<ProjectTimelineDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused", x => x.MigrationsAssembly("ProjectTimeline.Database")).Options);
        var migrator = db.GetService<IMigrator>();
        var forward = migrator.GenerateScript("20261011085524_TimelineEntrySnapshotsModel", "20261018000100_TimelineFacts");
        var reverse = migrator.GenerateScript("20261018000100_TimelineFacts", "20261011085524_TimelineEntrySnapshotsModel");
        Assert.Contains("CREATE TABLE \"TimelineEntryFacts\"", forward);
        Assert.Contains("CONSTRAINT \"PK_TimelineEntryFacts\"", forward);
        Assert.Contains("CREATE UNIQUE INDEX \"IX_TimelineEntryFacts_AdditionId\" ON \"TimelineEntryFacts\"", forward);
        Assert.Contains("\"FK_TimelineFactInvalidations_TimelineEntryFacts_FactId\" REFERENCES \"TimelineEntryFacts\"", forward);
        Assert.Contains("\"FK_TimelineFactJobs_TimelineEntryFacts_FactId\" REFERENCES \"TimelineEntryFacts\"", forward);
        Assert.Contains("DROP TABLE \"TimelineEntryFacts\"", reverse);
        var target = new ProjectTimeline.Database.Migrations.TimelineFacts().TargetModel;
        var fact = target.FindEntityType(typeof(TimelineFact).FullName!)!;
        foreach (var foreignKey in fact.GetForeignKeys().Concat(fact.GetReferencingForeignKeys()))
            Assert.Contains($"CONSTRAINT \"{foreignKey.GetConstraintName()}\"", forward);
        foreach (var index in fact.GetIndexes())
            Assert.Contains($"INDEX \"{index.GetDatabaseName()}\" ON \"TimelineEntryFacts\"", forward);
        foreach (var sql in new[] { forward, reverse })
        {
            Assert.DoesNotContain("IX_TimelineEntryFacts_FullText", sql);
            foreach (var obsolete in new[] { "\"TimelineFacts\"", "PK_TimelineFacts", "IX_TimelineFacts_", "FK_TimelineFacts_", "_TimelineFacts_FactId" })
                Assert.DoesNotContain(obsolete, sql);
        }
    }

    [Fact]
    public void Npgsql_model_has_real_vector_mapping_and_migration_snapshot_matches_without_connecting()
    {
        // Metadata/script generation only. This is NOT a PostgreSQL roundtrip or extension-installation test.
        using var db = new ProjectTimelineDbContext(new DbContextOptionsBuilder<ProjectTimelineDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused", x => x.MigrationsAssembly("ProjectTimeline.Database")).Options);
        var vector = db.Model.FindEntityType(typeof(TimelineFact))!.FindProperty(nameof(TimelineFact.Embedding))!;
        Assert.Equal("vector(1536)", vector.GetColumnType());
        Assert.True(vector.IsNullable);
        Assert.Equal(typeof(Pgvector.Vector), vector.ClrType);
        Assert.False(db.Database.HasPendingModelChanges());
        var target = new ProjectTimeline.Database.Migrations.ClarificationExtractionInputs().TargetModel;
        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot!.Model;
        Assert.Equal(snapshot.GetEntityTypes().Select(x => x.Name).Order(), target.GetEntityTypes().Select(x => x.Name).Order());
        foreach (var entity in snapshot.GetEntityTypes())
        {
            var targetEntity = target.FindEntityType(entity.Name)!;
            Assert.Equal(entity.GetProperties().Select(x => (x.Name, x.GetColumnType(), x.IsNullable, x.IsConcurrencyToken)).OrderBy(x => x.Name),
                targetEntity.GetProperties().Select(x => (x.Name, x.GetColumnType(), x.IsNullable, x.IsConcurrencyToken)).OrderBy(x => x.Name));
            Assert.Equal(entity.GetForeignKeys().Select(x => (x.GetConstraintName(), x.DeleteBehavior)).OrderBy(x => x.Item1),
                targetEntity.GetForeignKeys().Select(x => (x.GetConstraintName(), x.DeleteBehavior)).OrderBy(x => x.Item1));
            Assert.Equal(entity.GetIndexes().Select(x => (x.GetDatabaseName(), x.IsUnique)).OrderBy(x => x.Item1),
                targetEntity.GetIndexes().Select(x => (x.GetDatabaseName(), x.IsUnique)).OrderBy(x => x.Item1));
            Assert.Equal(entity.GetCheckConstraints().Select(x => (x.Name, x.Sql)).OrderBy(x => x.Name),
                targetEntity.GetCheckConstraints().Select(x => (x.Name, x.Sql)).OrderBy(x => x.Name));
        }
        Assert.Contains("20261018000100_TimelineFacts", db.Database.GetMigrations());
        Assert.DoesNotContain("202610180001_TimelineFacts", db.Database.GetMigrations());
        var sql = db.GetService<IMigrator>().GenerateScript("20261011085524_TimelineEntrySnapshotsModel", "20261018000100_TimelineFacts");
        Assert.Contains("CREATE EXTENSION IF NOT EXISTS vector", sql);
        Assert.Contains("vector(1536)", sql);
        Assert.Contains("ON DELETE RESTRICT", sql);
    }
}
