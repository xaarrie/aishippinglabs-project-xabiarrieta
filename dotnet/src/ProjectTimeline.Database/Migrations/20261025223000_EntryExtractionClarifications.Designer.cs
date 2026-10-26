using System;
using Microsoft.EntityFrameworkCore;

#nullable disable
namespace ProjectTimeline.Database.Migrations;

public partial class EntryExtractionClarifications
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        base.BuildTargetModel(modelBuilder);
        EntryExtractionClarificationsSnapshotModel.Configure(modelBuilder);
    }
}

// Frozen metadata: do not call runtime mapping from migration snapshots.
internal static class EntryExtractionClarificationsSnapshotModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity("ProjectTimeline.DataAccessLogic.TimelineEntryClarification", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid>("EntryId").HasColumnType("uuid");
            b.Property<Guid>("GenerationId").HasColumnType("uuid");
            b.Property<Guid>("Version").IsConcurrencyToken().HasColumnType("uuid");
            b.Property<Guid?>("AnsweredById").HasColumnType("uuid");
            b.Property<DateTime?>("AnsweredAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTime>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTime>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<string>("SourceFingerprint").IsRequired().HasMaxLength(64).HasColumnType("character varying(64)");
            b.Property<string>("QuestionKey").IsRequired().HasMaxLength(100).HasColumnType("character varying(100)");
            b.Property<string>("Question").IsRequired().HasMaxLength(2000).HasColumnType("character varying(2000)");
            b.Property<string>("Origin").IsRequired().HasMaxLength(30).HasColumnType("character varying(30)");
            b.Property<string>("Answer").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.HasKey("Id");
            b.HasIndex("EntryId");
            b.HasIndex("AnsweredById");
            b.HasIndex("GenerationId", "QuestionKey").IsUnique();
            b.ToTable("TimelineEntryClarifications", t => t.HasCheckConstraint("CK_EntryClarification_Answer",
                "(\"Answer\" IS NULL AND \"AnsweredById\" IS NULL AND \"AnsweredAtUtc\" IS NULL) OR (\"Answer\" IS NOT NULL AND \"AnsweredById\" IS NOT NULL AND \"AnsweredAtUtc\" IS NOT NULL)"));
            b.HasOne("ProjectTimeline.DataAccessLogic.TimelineEntry", null).WithMany().HasForeignKey("EntryId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("ProjectTimeline.DataAccessLogic.TimelineFactJob", null).WithMany().HasForeignKey("GenerationId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("ProjectTimeline.DataAccessLogic.User", null).WithMany().HasForeignKey("AnsweredById").OnDelete(DeleteBehavior.Restrict);
        });
    }
}
