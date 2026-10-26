using Microsoft.EntityFrameworkCore;

#nullable disable
namespace ProjectTimeline.Database.Migrations;

public partial class TimelineFactFullTextIndex
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        base.BuildTargetModel(modelBuilder);
        TimelineFactFullTextSnapshotModel.Configure(modelBuilder);
    }
}

// Frozen metadata extends the prior migration without consulting runtime model configuration.
internal static class TimelineFactFullTextSnapshotModel
{
    public static void Configure(ModelBuilder model) => model.Entity("ProjectTimeline.DataAccessLogic.TimelineFact")
        .HasIndex("Claim", "EntityKey", "EntityType").HasDatabaseName("IX_TimelineEntryFacts_FullText")
        .HasMethod("gin").IsTsVectorExpressionIndex("simple");
}
