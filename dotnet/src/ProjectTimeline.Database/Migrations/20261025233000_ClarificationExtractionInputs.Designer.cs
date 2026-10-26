using Microsoft.EntityFrameworkCore;

#nullable disable
namespace ProjectTimeline.Database.Migrations;

public partial class ClarificationExtractionInputs
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        base.BuildTargetModel(modelBuilder);
        ClarificationExtractionInputsSnapshotModel.Configure(modelBuilder);
    }
}

// Frozen additive metadata; never call live runtime mapping from a historical target.
internal static class ClarificationExtractionInputsSnapshotModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity("ProjectTimeline.DataAccessLogic.TimelineFactJob", b =>
        {
            b.Property<string>("ExtractionInputJson").HasColumnType("text");
            b.Property<string>("EffectiveInputFingerprint").HasMaxLength(64).HasColumnType("character varying(64)");
            b.Property<string>("ExtractionSupportJson").HasColumnType("text");
        });
    }
}
