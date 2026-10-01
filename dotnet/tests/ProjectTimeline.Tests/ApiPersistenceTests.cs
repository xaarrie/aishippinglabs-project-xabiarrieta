using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using ProjectTimeline.Database.Migrations;
using ProjectTimeline.DataAccessLogic;
using Xunit;

namespace ProjectTimeline.Tests;
public sealed class ApiPersistenceTests
{
    [Fact]
    public async Task User_email_and_project_code_are_modelled_as_unique()
    {
        var options = new DbContextOptionsBuilder<ProjectTimelineDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ProjectTimelineDbContext(options); db.Users.Add(new User { Email = "admin@example.test", DisplayName = "Admin", PasswordHash = "not-a-secret" }); await db.SaveChangesAsync();
        Assert.True(await db.Users.AnyAsync(x => x.Email == "admin@example.test")); Assert.Contains(nameof(ProjectTimelineDbContext.Users), typeof(ProjectTimelineDbContext).GetProperties().Select(x => x.Name));
    }

    [Fact]
    public void Initial_migration_is_registered_and_contains_unescaped_postgresql_identifiers()
    {
        var attribute = typeof(InitialCreate).GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single();
        Assert.Equal("202610010001_InitialCreate", attribute.Id);

        var migration = new InitialCreate();
        var up = typeof(InitialCreate).GetMethod("Up", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        up.Invoke(migration, [builder]);
        var sql = builder.Operations.OfType<SqlOperation>().Single().Sql;
        Assert.DoesNotContain("\\\"", sql);
        Assert.Contains("CREATE TABLE \"Users\"", sql);
        Assert.Contains("REFERENCES \"Users\"(\"Id\")", sql);
    }

    [Fact]
    public void Context_options_point_ef_core_at_the_database_migrations_assembly()
    {
        var options = new DbContextOptionsBuilder<ProjectTimelineDbContext>()
            .UseNpgsql("Host=localhost;Database=test;Username=test", o => o.MigrationsAssembly("ProjectTimeline.Database"))
            .Options;
        using var db = new ProjectTimelineDbContext(options);
        Assert.Contains("202610010001_InitialCreate", db.Database.GetMigrations());
    }
}
