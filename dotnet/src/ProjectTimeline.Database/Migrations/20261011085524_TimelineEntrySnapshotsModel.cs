using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectTimeline.Database.Migrations
{
    public partial class TimelineEntrySnapshotsModel : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(name: "TimelineSnapshots", columns: table => new { Id = table.Column<Guid>(type: "uuid", nullable: false), TimelineId = table.Column<Guid>(type: "uuid", nullable: false), SnapshotNumber = table.Column<int>(type: "integer", nullable: false), CapturedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false), SummaryContent = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false), CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false), UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false) }, constraints: table => { table.PrimaryKey("PK_TimelineSnapshots", x => x.Id); table.ForeignKey("FK_TimelineSnapshots_Timelines_TimelineId", x => x.TimelineId, "Timelines", "Id", onDelete: ReferentialAction.Cascade); });
            migrationBuilder.CreateTable(name: "TimelineSnapshotEntries", columns: table => new { Id = table.Column<Guid>(type: "uuid", nullable: false), SnapshotId = table.Column<Guid>(type: "uuid", nullable: false), EntryId = table.Column<Guid>(type: "uuid", nullable: false), CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false), UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false) }, constraints: table => { table.PrimaryKey("PK_TimelineSnapshotEntries", x => x.Id); table.ForeignKey("FK_TimelineSnapshotEntries_TimelineEntries_EntryId", x => x.EntryId, "TimelineEntries", "Id", onDelete: ReferentialAction.Restrict); table.ForeignKey("FK_TimelineSnapshotEntries_TimelineSnapshots_SnapshotId", x => x.SnapshotId, "TimelineSnapshots", "Id", onDelete: ReferentialAction.Cascade); });
            migrationBuilder.CreateIndex(name: "IX_TimelineSnapshotEntries_EntryId", table: "TimelineSnapshotEntries", column: "EntryId");
            migrationBuilder.CreateIndex(name: "IX_TimelineSnapshotEntries_SnapshotId_EntryId", table: "TimelineSnapshotEntries", columns: new[] { "SnapshotId", "EntryId" }, unique: true);
            migrationBuilder.CreateIndex(name: "IX_TimelineSnapshots_TimelineId_SnapshotNumber", table: "TimelineSnapshots", columns: new[] { "TimelineId", "SnapshotNumber" }, unique: true);
            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS \"IX_TimelineEntries_Content_Fts\" ON \"TimelineEntries\" USING GIN (to_tsvector('simple', \"Content\"));");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_TimelineEntries_Content_Fts\";");
            migrationBuilder.DropTable(name: "TimelineSnapshotEntries");
            migrationBuilder.DropTable(name: "TimelineSnapshots");
        }
    }
}
