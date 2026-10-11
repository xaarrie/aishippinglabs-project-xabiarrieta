using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectTimeline.DataAccessLogic;

#nullable disable
namespace ProjectTimeline.Database.Migrations;

[Migration("202610020001_NormalizeUserRoleCatalog")]
[DbContext(typeof(ProjectTimelineDbContext))]
public partial class NormalizeUserRoleCatalog : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE \"Users\" ADD COLUMN \"IDUserRole\" uuid NULL;");
        migrationBuilder.Sql("""
CREATE TABLE "UserRoles_new" ("Id" uuid NOT NULL PRIMARY KEY, "RoleName" varchar(40) NOT NULL, "CreatedAtUtc" timestamp with time zone NOT NULL, "UpdatedAtUtc" timestamp with time zone NOT NULL);
INSERT INTO "UserRoles_new" ("Id", "RoleName", "CreatedAtUtc", "UpdatedAtUtc") VALUES
    ('00000000-0000-0000-0000-000000000001', 'Admin', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP),
    ('00000000-0000-0000-0000-000000000002', 'Consultant', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
""");
        migrationBuilder.Sql("""
UPDATE "Users" AS u
SET "IDUserRole" = r."Id"
FROM "UserRoles_new" AS r
WHERE r."RoleName" = CASE
    WHEN EXISTS (SELECT 1 FROM "UserRoles" old WHERE old."UserId" = u."Id" AND old."RoleName" = 'Administrator') THEN 'Admin'
    WHEN EXISTS (SELECT 1 FROM "UserRoles" old WHERE old."UserId" = u."Id" AND old."RoleName" = 'Consultant') THEN 'Consultant'
    ELSE 'Admin'
END;
""");
        migrationBuilder.Sql("ALTER TABLE \"UserRoles\" DROP CONSTRAINT IF EXISTS \"UserRoles_UserId_fkey\";");
        migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_UserRoles_UserId_RoleName\";");
        migrationBuilder.Sql("DROP TABLE \"UserRoles\";");
        migrationBuilder.Sql("ALTER TABLE \"UserRoles_new\" RENAME TO \"UserRoles\";");
        migrationBuilder.Sql("ALTER TABLE \"Users\" ALTER COLUMN \"IDUserRole\" SET NOT NULL;");
        migrationBuilder.Sql("ALTER TABLE \"Users\" ADD CONSTRAINT \"FK_Users_UserRoles_IDUserRole\" FOREIGN KEY (\"IDUserRole\") REFERENCES \"UserRoles\" (\"Id\") ON DELETE RESTRICT;");
        migrationBuilder.Sql("CREATE UNIQUE INDEX \"IX_UserRoles_RoleName\" ON \"UserRoles\" (\"RoleName\");");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE \"Users\" DROP CONSTRAINT IF EXISTS \"FK_Users_UserRoles_IDUserRole\";");
        migrationBuilder.Sql("ALTER TABLE \"Users\" DROP COLUMN \"IDUserRole\";");
        migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_UserRoles_RoleName\";");
        migrationBuilder.Sql("ALTER TABLE \"UserRoles\" ADD COLUMN \"UserId\" uuid NULL;");
        migrationBuilder.Sql("DROP TABLE \"UserRoles\";");
    }
}
