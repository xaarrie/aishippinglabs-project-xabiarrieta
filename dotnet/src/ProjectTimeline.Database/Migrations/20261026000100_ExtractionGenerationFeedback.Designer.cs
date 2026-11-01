using Microsoft.EntityFrameworkCore;

#nullable disable
namespace ProjectTimeline.Database.Migrations;

public partial class ExtractionGenerationFeedback
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        base.BuildTargetModel(modelBuilder);
        ExtractionGenerationFeedbackSnapshotModel.Configure(modelBuilder);
    }
}

// Frozen additive metadata; historical targets never depend on live runtime mapping.
internal static class ExtractionGenerationFeedbackSnapshotModel
{
    public static void Configure(ModelBuilder model)
        => model.Entity("ProjectTimeline.DataAccessLogic.TimelineFactJob")
            .Property<string>("ExtractionFeedbackJson").HasColumnType("text");
}
