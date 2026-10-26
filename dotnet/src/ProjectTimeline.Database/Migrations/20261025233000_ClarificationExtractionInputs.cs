using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectTimeline.DataAccessLogic;

#nullable disable
namespace ProjectTimeline.Database.Migrations;

[Migration("20261025233000_ClarificationExtractionInputs")]
[DbContext(typeof(ProjectTimelineDbContext))]
public partial class ClarificationExtractionInputs : EntryExtractionClarifications
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("ExtractionInputJson", "TimelineFactJobs", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>("EffectiveInputFingerprint", "TimelineFactJobs", type: "character varying(64)", maxLength: 64, nullable: true);
        migrationBuilder.AddColumn<string>("ExtractionSupportJson", "TimelineFactJobs", type: "text", nullable: true);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("ExtractionSupportJson", "TimelineFactJobs");
        migrationBuilder.DropColumn("EffectiveInputFingerprint", "TimelineFactJobs");
        migrationBuilder.DropColumn("ExtractionInputJson", "TimelineFactJobs");
    }
}
