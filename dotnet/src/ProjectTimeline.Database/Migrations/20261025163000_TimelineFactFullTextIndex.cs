using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectTimeline.DataAccessLogic;

#nullable disable
namespace ProjectTimeline.Database.Migrations;

[Migration("20261025163000_TimelineFactFullTextIndex")]
[DbContext(typeof(ProjectTimelineDbContext))]
public partial class TimelineFactFullTextIndex : FactInvalidationCause
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateIndex(
        name: "IX_TimelineEntryFacts_FullText", table: "TimelineEntryFacts",
        columns: new[] { "Claim", "EntityKey", "EntityType" })
        .Annotation("Npgsql:IndexMethod", "gin")
        .Annotation("Npgsql:TsVectorConfig", "simple");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropIndex(
        name: "IX_TimelineEntryFacts_FullText", table: "TimelineEntryFacts");
}
