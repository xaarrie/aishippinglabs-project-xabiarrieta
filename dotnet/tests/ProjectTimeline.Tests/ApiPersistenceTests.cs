using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using ProjectTimeline.Database.Migrations;
using ProjectTimeline.Database;
using ProjectTimeline.BusinessLogic;
using ProjectTimeline.DataAccessLogic;
using ProjectTimeline.UI.WebApi;
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
        Assert.Contains("202610020001_NormalizeUserRoleCatalog", db.Database.GetMigrations());
        Assert.Contains("202610020002_ProjectPrimaryTimeline", db.Database.GetMigrations());
    }

    [Fact]
    public void Current_model_has_a_required_user_role_catalog_relationship()
    {
        var options = new DbContextOptionsBuilder<ProjectTimelineDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var db = new ProjectTimelineDbContext(options);
        var user = db.Model.FindEntityType(typeof(User))!;
        var role = db.Model.FindEntityType(typeof(UserRole))!;
        Assert.NotNull(user.FindProperty(nameof(User.IDUserRole)));
        Assert.False(user.FindProperty(nameof(User.IDUserRole))!.IsNullable);
        Assert.Null(role.FindProperty("UserId"));
        Assert.NotNull(user.FindNavigation(nameof(User.UserRole)));
        Assert.Contains(role.GetIndexes(), index => index.IsUnique && index.Properties.Single().Name == nameof(UserRole.RoleName));
    }

    [Fact]
    public void User_contract_exposes_one_selected_role_and_catalog_display_fields()
    {
        var role = new UserRole { RoleName = "Admin" };
        var user = new User { Email = "admin@example.test", DisplayName = "Admin", PasswordHash = "hash", IDUserRole = role.Id, UserRole = role };
        var dto = ApiMappers.ToDto(user);
        var roleDto = ApiMappers.ToDto(role);
        Assert.Equal(role.Id, dto.IDUserRole);
        Assert.Equal("Admin", dto.RoleCode);
        Assert.Equal("Admin", dto.RoleName);
        Assert.Equal("Admin", roleDto.Code);
        Assert.Equal("Admin", roleDto.Name);
        Assert.Equal(["Admin", "Consultant"], RoleCatalog.SelectableCodes.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Relationship_contracts_expose_related_display_labels()
    {
        var owner = new User { DisplayName = "Owner" };
        var project = new Project { Code = "P1", Name = "Project", OwnerId = owner.Id, Owner = owner };
        var timeline = new Timeline { ProjectId = project.Id, Project = project, Code = "T1", Name = "Timeline" };
        var author = new User { DisplayName = "Author" };
        var entry = new TimelineEntry { TimelineId = timeline.Id, Timeline = timeline, AuthorId = author.Id, Author = author, Title = "Title", Content = "Content" };

        Assert.Equal("Owner", ApiMappers.ToDto(project).OwnerDisplayName);
        Assert.Equal("Project", ApiMappers.ToDto(timeline).ProjectName);
        Assert.Equal("Author", ApiMappers.ToDto(entry).AuthorDisplayName);
    }

    [Fact]
    public void Normalize_migration_contains_nullable_backfill_required_and_fk_sequence()
    {
        var migration = new NormalizeUserRoleCatalog();
        var up = typeof(NormalizeUserRoleCatalog).GetMethod("Up", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        up.Invoke(migration, [builder]);
        var sql = string.Join("\n", builder.Operations.OfType<SqlOperation>().Select(x => x.Sql));
        Assert.Contains("ADD COLUMN \"IDUserRole\" uuid NULL", sql);
        Assert.Contains("old.\"RoleName\" = 'Administrator'", sql);
        Assert.Contains("'Administrator') THEN 'Admin'", sql);
        Assert.Contains("ALTER COLUMN \"IDUserRole\" SET NOT NULL", sql);
        Assert.Contains("FOREIGN KEY (\"IDUserRole\")", sql);
    }

    [Fact]
    public async Task Fresh_seed_maintains_catalog_and_creates_one_admin()
    {
        var options = new DbContextOptionsBuilder<ProjectTimelineDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ProjectTimelineDbContext(options);
        await DatabaseSetup.SeedAsync(db, "password-hash");
        await DatabaseSetup.SeedAsync(db, "ignored-on-repeat");
        Assert.Equal(["Admin", "Consultant"], await db.UserRoles.OrderBy(x => x.RoleName).Select(x => x.RoleName).ToArrayAsync());
        var admin = await db.Users.Include(x => x.UserRole).SingleAsync();
        Assert.Equal("x.arrieta@ibernova.com", admin.Email);
        Assert.Equal("Admin", admin.UserRole.RoleName);
    }

    [Fact]
    public void Project_primary_timeline_is_nullable_indexed_and_set_null_on_delete()
    {
        using var db = new ProjectTimelineDbContext(new DbContextOptionsBuilder<ProjectTimelineDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var project = db.Model.FindEntityType(typeof(Project))!;
        var property = project.FindProperty(nameof(Project.PrimaryTimelineId))!;
        Assert.True(property.IsNullable);
        Assert.Contains(project.GetIndexes(), index => index.Properties.Single().Name == nameof(Project.PrimaryTimelineId));
        var relationship = project.GetForeignKeys().Single(x => x.Properties.Single().Name == nameof(Project.PrimaryTimelineId));
        Assert.Equal(DeleteBehavior.SetNull, relationship.DeleteBehavior);
    }

    [Fact]
    public void Primary_timeline_migration_is_forward_only_and_uses_set_null_fk()
    {
        var migration = new ProjectPrimaryTimeline();
        var up = typeof(ProjectPrimaryTimeline).GetMethod("Up", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        up.Invoke(migration, [builder]);
        var column = builder.Operations.OfType<AddColumnOperation>().Single(x => x.Name == nameof(Project.PrimaryTimelineId));
        var foreignKey = builder.Operations.OfType<AddForeignKeyOperation>().Single(x => x.Name == "FK_Projects_Timelines_PrimaryTimelineId");
        Assert.True(column.IsNullable);
        Assert.Equal(ReferentialAction.SetNull, foreignKey.OnDelete);
        Assert.Contains(builder.Operations.OfType<CreateIndexOperation>(), x => x.Name == "IX_Projects_PrimaryTimelineId");
    }
}
