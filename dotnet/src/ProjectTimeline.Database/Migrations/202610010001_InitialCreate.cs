using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using ProjectTimeline.DataAccessLogic;

#nullable disable
namespace ProjectTimeline.Database.Migrations;

[Migration("202610010001_InitialCreate")]
[DbContext(typeof(ProjectTimelineDbContext))]
public partial class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE "Users" ("Id" uuid NOT NULL PRIMARY KEY, "Email" varchar(320) NOT NULL, "DisplayName" varchar(160) NOT NULL, "PasswordHash" varchar(512) NOT NULL, "IsActive" boolean NOT NULL, "CreatedAtUtc" timestamp with time zone NOT NULL, "UpdatedAtUtc" timestamp with time zone NOT NULL);
CREATE UNIQUE INDEX "IX_Users_Email" ON "Users" ("Email");
CREATE TABLE "Projects" ("Id" uuid NOT NULL PRIMARY KEY, "Code" varchar(32) NOT NULL, "Name" varchar(160) NOT NULL, "Description" varchar(2000), "OwnerId" uuid NOT NULL REFERENCES "Users"("Id") ON DELETE RESTRICT, "CreatedAtUtc" timestamp with time zone NOT NULL, "UpdatedAtUtc" timestamp with time zone NOT NULL);
CREATE UNIQUE INDEX "IX_Projects_Code" ON "Projects" ("Code");
CREATE TABLE "UserRoles" ("Id" uuid NOT NULL PRIMARY KEY, "UserId" uuid NOT NULL REFERENCES "Users"("Id") ON DELETE CASCADE, "RoleName" varchar(40) NOT NULL, "CreatedAtUtc" timestamp with time zone NOT NULL, "UpdatedAtUtc" timestamp with time zone NOT NULL);
CREATE UNIQUE INDEX "IX_UserRoles_UserId_RoleName" ON "UserRoles" ("UserId", "RoleName");
CREATE TABLE "ProjectParticipants" ("Id" uuid NOT NULL PRIMARY KEY, "ProjectId" uuid NOT NULL REFERENCES "Projects" ON DELETE CASCADE, "UserId" uuid NOT NULL REFERENCES "Users" ON DELETE CASCADE, "ProjectRoleName" varchar(40) NOT NULL, "IsInvited" boolean NOT NULL, "CreatedAtUtc" timestamp with time zone NOT NULL, "UpdatedAtUtc" timestamp with time zone NOT NULL);
CREATE UNIQUE INDEX "IX_ProjectParticipants_ProjectId_UserId" ON "ProjectParticipants" ("ProjectId", "UserId");
CREATE TABLE "ProjectRoles" ("Id" uuid NOT NULL PRIMARY KEY, "ProjectId" uuid NOT NULL REFERENCES "Projects" ON DELETE CASCADE, "Name" varchar(40) NOT NULL, "CreatedAtUtc" timestamp with time zone NOT NULL, "UpdatedAtUtc" timestamp with time zone NOT NULL);
CREATE UNIQUE INDEX "IX_ProjectRoles_ProjectId_Name" ON "ProjectRoles" ("ProjectId", "Name");
CREATE TABLE "Timelines" ("Id" uuid NOT NULL PRIMARY KEY, "ProjectId" uuid NOT NULL REFERENCES "Projects"("Id") ON DELETE CASCADE, "Code" varchar(32) NOT NULL, "Name" varchar(160) NOT NULL, "CreatedAtUtc" timestamp with time zone NOT NULL, "UpdatedAtUtc" timestamp with time zone NOT NULL);
CREATE UNIQUE INDEX "IX_Timelines_ProjectId_Code" ON "Timelines" ("ProjectId", "Code");
CREATE TABLE "TimelineEntries" ("Id" uuid NOT NULL PRIMARY KEY, "TimelineId" uuid NOT NULL REFERENCES "Timelines"("Id") ON DELETE CASCADE, "EntryNumber" integer NOT NULL, "Title" varchar(200) NOT NULL, "Content" varchar(20000) NOT NULL, "Status" varchar(30) NOT NULL, "AuthorId" uuid NOT NULL REFERENCES "Users"("Id") ON DELETE RESTRICT, "CreatedAtUtc" timestamp with time zone NOT NULL, "UpdatedAtUtc" timestamp with time zone NOT NULL);
CREATE UNIQUE INDEX "IX_TimelineEntries_TimelineId_EntryNumber" ON "TimelineEntries" ("TimelineId", "EntryNumber");
CREATE TABLE "TimelineArtifacts" ("Id" uuid NOT NULL PRIMARY KEY, "EntryId" uuid NOT NULL REFERENCES "TimelineEntries"("Id") ON DELETE CASCADE, "FileName" varchar(255) NOT NULL, "RelativePath" varchar(1024) NOT NULL, "ContentType" text NOT NULL, "Length" bigint NOT NULL, "CreatedAtUtc" timestamp with time zone NOT NULL, "UpdatedAtUtc" timestamp with time zone NOT NULL);
CREATE TABLE "MergeRequests" ("Id" uuid NOT NULL PRIMARY KEY, "TimelineId" uuid NOT NULL REFERENCES "Timelines"("Id") ON DELETE CASCADE, "AuthorId" uuid NOT NULL REFERENCES "Users"("Id") ON DELETE RESTRICT, "Summary" varchar(4000) NOT NULL, "Status" varchar(30) NOT NULL, "CreatedAtUtc" timestamp with time zone NOT NULL, "UpdatedAtUtc" timestamp with time zone NOT NULL);
""");
    }
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("DROP TABLE IF EXISTS \"MergeRequests\", \"TimelineArtifacts\", \"TimelineEntries\", \"Timelines\", \"ProjectRoles\", \"ProjectParticipants\", \"Projects\", \"UserRoles\", \"Users\";");
}
