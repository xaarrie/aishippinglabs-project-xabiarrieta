using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectTimeline.DataAccessLogic;

#nullable disable
namespace ProjectTimeline.Database.Migrations;

[Migration("20261026000100_ExtractionGenerationFeedback")]
[DbContext(typeof(ProjectTimelineDbContext))]
public partial class ExtractionGenerationFeedback : ClarificationExtractionInputs
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.AddColumn<string>("ExtractionFeedbackJson", "TimelineFactJobs", type: "text", nullable: true);
    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropColumn("ExtractionFeedbackJson", "TimelineFactJobs");
}
