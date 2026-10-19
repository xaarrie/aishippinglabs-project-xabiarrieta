using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

#nullable disable
namespace ProjectTimeline.Database.Migrations;

public partial class TimelineFacts
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        // The preceding migration's designer is a frozen complete baseline, NOT the current snapshot.
        base.BuildTargetModel(modelBuilder);
        TimelineFactsSnapshotModel.Configure(modelBuilder);
    }
}

// Frozen T1 metadata shared only with this migration and the current snapshot.
internal static class TimelineFactsSnapshotModel
{
    private const string Prefix = "ProjectTimeline.DataAccessLogic.";
    public static void Configure(ModelBuilder model)
    {
        model.HasPostgresExtension("vector");
        model.Entity(Prefix + "Timeline").Property<long>("FactRevision").IsConcurrencyToken().HasColumnType("bigint");
        var proposal = Entity(model, "TimelineFactProposal", "TimelineFactProposals");
        Id(proposal, "EntryId"); Id(proposal, "AcceptedById", true);
        proposal.Property<long>("ExpectedRevision").HasColumnType("bigint");
        Text(proposal, "SourceFingerprint", 64); Text(proposal, "QuestionsJson", 20000);
        Text(proposal, "ClarificationEvidence", 20000); Text(proposal, "Status", 30).IsConcurrencyToken();
        Time(proposal, "AcceptedAtUtc");
        proposal.HasIndex("EntryId"); proposal.HasIndex("AcceptedById");
        Foreign(proposal, "TimelineEntry", "EntryId"); Foreign(proposal, "User", "AcceptedById");

        var addition = Entity(model, "TimelineFactAddition", "TimelineFactAdditions");
        Id(addition, "ProposalId"); Claim(addition);
        addition.HasIndex("ProposalId");
        addition.HasOne(Prefix + "TimelineFactProposal", null).WithMany("Additions").HasForeignKey("ProposalId").OnDelete(DeleteBehavior.Restrict).IsRequired();

        var fact = Entity(model, "TimelineFact", "TimelineEntryFacts");
        Id(fact, "EntryId"); Id(fact, "CreationReviewId"); Id(fact, "AdditionId"); Id(fact, "InvalidationReviewId", true);
        Claim(fact); Text(fact, "SourceFingerprint", 64); Text(fact, "ClarificationEvidence", 20000);
        fact.Property<bool>("Valid").HasColumnType("boolean");
        Time(fact, "InvalidatedAtUtc"); Time(fact, "EmbeddedAtUtc");
        fact.Property<Pgvector.Vector>("Embedding").HasColumnType("vector(1536)");
        Text(fact, "EmbeddingModel", 100);
        fact.HasIndex("AdditionId").IsUnique(); fact.HasIndex("EntryId", "Valid");
        fact.HasIndex("CreationReviewId"); fact.HasIndex("InvalidationReviewId");
        Foreign(fact, "TimelineEntry", "EntryId"); Foreign(fact, "TimelineFactProposal", "CreationReviewId");
        Foreign(fact, "TimelineFactAddition", "AdditionId"); Foreign(fact, "TimelineFactProposal", "InvalidationReviewId");

        var invalidation = Entity(model, "TimelineFactInvalidation", "TimelineFactInvalidations");
        Id(invalidation, "ProposalId"); Id(invalidation, "FactId"); Text(invalidation, "Reason", 4000);
        invalidation.HasIndex("ProposalId", "FactId").IsUnique(); invalidation.HasIndex("FactId");
        invalidation.HasOne(Prefix + "TimelineFactProposal", null).WithMany("Invalidations").HasForeignKey("ProposalId").OnDelete(DeleteBehavior.Restrict).IsRequired();
        Foreign(invalidation, "TimelineFact", "FactId");

        var job = Entity(model, "TimelineFactJob", "TimelineFactJobs");
        Id(job, "EntryId"); Id(job, "FactId", true);
        Text(job, "WorkKey", 300); Text(job, "Kind", 30); Text(job, "SourceFingerprint", 64);
        Text(job, "State", 30); Text(job, "Model", 100);
        job.Property<int>("Attempts").HasColumnType("integer");
        Time(job, "NextAttemptAtUtc"); Time(job, "LeaseExpiresAtUtc");
        Text(job, "LeaseOwner", 200, false); Text(job, "LastErrorCode", 100, false);
        job.Property<Guid>("Version").IsConcurrencyToken().HasColumnType("uuid");
        job.HasIndex("WorkKey").IsUnique(); job.HasIndex("EntryId"); job.HasIndex("FactId");
        job.HasIndex("State", "NextAttemptAtUtc", "LeaseExpiresAtUtc");
        Foreign(job, "TimelineEntry", "EntryId"); Foreign(job, "TimelineFact", "FactId");
    }
    private static EntityTypeBuilder Entity(ModelBuilder model, string name, string table)
    {
        var entity = model.Entity(Prefix + name);
        entity.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
        entity.Property<DateTime>("CreatedAtUtc").HasColumnType("timestamp with time zone");
        entity.Property<DateTime>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
        entity.HasKey("Id"); entity.ToTable(table);
        return entity;
    }
    private static void Claim(EntityTypeBuilder entity)
    {
        Text(entity, "Claim", 4000); Text(entity, "EntityKey", 300); Text(entity, "EntityType", 100);
        Text(entity, "SourceQuote", 20000); Text(entity, "SourceKind", 30);
    }
    private static PropertyBuilder Text(EntityTypeBuilder entity, string name, int max, bool required = true) =>
        entity.Property<string>(name).IsRequired(required).HasMaxLength(max).HasColumnType($"character varying({max})");
    private static void Id(EntityTypeBuilder entity, string name, bool nullable = false)
    {
        if (nullable) entity.Property<Guid?>(name).HasColumnType("uuid");
        else entity.Property<Guid>(name).HasColumnType("uuid");
    }
    private static void Time(EntityTypeBuilder entity, string name) => entity.Property<DateTime?>(name).HasColumnType("timestamp with time zone");
    private static void Foreign(EntityTypeBuilder entity, string target, string key) =>
        entity.HasOne(Prefix + target, null).WithMany().HasForeignKey(key).OnDelete(DeleteBehavior.Restrict);
}
