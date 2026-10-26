using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectTimeline.DataAccessLogic;

#nullable disable
namespace ProjectTimeline.Database.Migrations;

[Migration("20261025223000_EntryExtractionClarifications")]
[DbContext(typeof(ProjectTimelineDbContext))]
public partial class EntryExtractionClarifications : TimelineFactFullTextIndex
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("TimelineEntryClarifications", columns: table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            EntryId = table.Column<Guid>(type: "uuid", nullable: false),
            GenerationId = table.Column<Guid>(type: "uuid", nullable: false),
            SourceFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
            QuestionKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
            Question = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
            Origin = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
            Answer = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
            AnsweredById = table.Column<Guid>(type: "uuid", nullable: true),
            AnsweredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
            Version = table.Column<Guid>(type: "uuid", nullable: false),
            CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
            UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_TimelineEntryClarifications", x => x.Id);
            table.CheckConstraint("CK_EntryClarification_Answer", "(\"Answer\" IS NULL AND \"AnsweredById\" IS NULL AND \"AnsweredAtUtc\" IS NULL) OR (\"Answer\" IS NOT NULL AND \"AnsweredById\" IS NOT NULL AND \"AnsweredAtUtc\" IS NOT NULL)");
            table.ForeignKey("FK_TimelineEntryClarifications_TimelineEntries_EntryId", x => x.EntryId, "TimelineEntries", "Id", onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_TimelineEntryClarifications_TimelineFactJobs_GenerationId", x => x.GenerationId, "TimelineFactJobs", "Id", onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_TimelineEntryClarifications_Users_AnsweredById", x => x.AnsweredById, "Users", "Id", onDelete: ReferentialAction.Restrict);
        });
        migrationBuilder.CreateIndex("IX_TimelineEntryClarifications_EntryId", "TimelineEntryClarifications", "EntryId");
        migrationBuilder.CreateIndex("IX_TimelineEntryClarifications_AnsweredById", "TimelineEntryClarifications", "AnsweredById");
        migrationBuilder.CreateIndex("IX_TimelineEntryClarifications_GenerationId_QuestionKey", "TimelineEntryClarifications", new[] { "GenerationId", "QuestionKey" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("TimelineEntryClarifications");
}
