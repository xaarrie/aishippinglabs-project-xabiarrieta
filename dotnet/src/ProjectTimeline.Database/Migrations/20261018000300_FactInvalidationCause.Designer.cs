using Microsoft.EntityFrameworkCore;

#nullable disable
namespace ProjectTimeline.Database.Migrations;

public partial class FactInvalidationCause
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        base.BuildTargetModel(modelBuilder);
        FactInvalidationCauseSnapshotModel.Configure(modelBuilder);
    }
}

// Frozen extension of the direct-extraction metadata, independent of runtime configuration.
internal static class FactInvalidationCauseSnapshotModel
{
    private const string Prefix = "ProjectTimeline.DataAccessLogic.";
    public static void Configure(ModelBuilder model)
    {
        var fact = model.Entity(Prefix + "TimelineFact");
        fact.Property<Guid?>("InvalidatedByFactId").HasColumnType("uuid");
        fact.HasIndex("InvalidatedByFactId");
        fact.HasOne(Prefix + "TimelineFact", null).WithMany().HasForeignKey("InvalidatedByFactId").OnDelete(DeleteBehavior.Restrict);
        fact.ToTable("TimelineEntryFacts", t => t.HasCheckConstraint("CK_TimelineEntryFacts_InvalidationCause",
            "\"InvalidatedByFactId\" IS NULL OR (\"InvalidatedByFactId\" <> \"Id\" AND NOT \"Valid\" AND \"InvalidatedAtUtc\" IS NOT NULL AND \"InvalidationReviewId\" IS NOT NULL)"));
        var invalidation = model.Entity(Prefix + "TimelineFactInvalidation");
        invalidation.Property<Guid?>("InvalidatingAdditionId").HasColumnType("uuid");
        invalidation.HasIndex("InvalidatingAdditionId");
        invalidation.HasOne(Prefix + "TimelineFactAddition", null).WithMany().HasForeignKey("InvalidatingAdditionId").OnDelete(DeleteBehavior.Restrict);
    }
}
