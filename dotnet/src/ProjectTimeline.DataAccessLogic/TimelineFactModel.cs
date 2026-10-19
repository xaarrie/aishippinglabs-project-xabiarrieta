using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ProjectTimeline.DataAccessLogic;

public static class TimelineFactModel
{
    public static void Configure(ModelBuilder b, bool postgres)
    {
        b.Entity<Timeline>().Property(x => x.FactRevision).IsConcurrencyToken();
        var fact = b.Entity<TimelineFact>();
        fact.ToTable("TimelineEntryFacts", t => t.HasCheckConstraint("CK_TimelineEntryFacts_Origin",
            "(\"ExtractionJobId\" IS NULL AND \"CandidateKey\" IS NULL AND \"CreationReviewId\" IS NOT NULL AND \"AdditionId\" IS NOT NULL) OR (\"ExtractionJobId\" IS NOT NULL AND \"CandidateKey\" IS NOT NULL AND \"CreationReviewId\" IS NULL AND \"AdditionId\" IS NULL AND \"SourceKind\" = 'Entry' AND \"ClarificationEvidence\" = '')"));
        fact.ToTable("TimelineEntryFacts", t => t.HasCheckConstraint("CK_TimelineEntryFacts_InvalidationCause",
            "\"InvalidatedByFactId\" IS NULL OR (\"InvalidatedByFactId\" <> \"Id\" AND NOT \"Valid\" AND \"InvalidatedAtUtc\" IS NOT NULL AND \"InvalidationReviewId\" IS NOT NULL)"));
        fact.HasOne<TimelineFact>().WithMany().HasForeignKey(x => x.InvalidatedByFactId).OnDelete(DeleteBehavior.Restrict);
        fact.HasOne<TimelineFactJob>().WithMany().HasForeignKey(x => x.ExtractionJobId).OnDelete(DeleteBehavior.Restrict);
        fact.Property(x => x.CandidateKey).HasMaxLength(64);
        fact.HasIndex(x => new { x.ExtractionJobId, x.CandidateKey }).IsUnique();
        fact.HasOne<TimelineEntry>().WithMany().HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.Restrict);
        fact.HasOne<TimelineFactProposal>().WithMany().HasForeignKey(x => x.CreationReviewId).OnDelete(DeleteBehavior.Restrict);
        fact.HasOne<TimelineFactProposal>().WithMany().HasForeignKey(x => x.InvalidationReviewId).OnDelete(DeleteBehavior.Restrict);
        fact.HasOne<TimelineFactAddition>().WithMany().HasForeignKey(x => x.AdditionId).OnDelete(DeleteBehavior.Restrict);
        fact.HasIndex(x => x.AdditionId).IsUnique();
        fact.HasIndex(x => new { x.EntryId, x.Valid });
        Text(fact.Property(x => x.Claim), 4000);
        Text(fact.Property(x => x.EntityKey), 300);
        Text(fact.Property(x => x.EntityType), 100);
        Text(fact.Property(x => x.SourceQuote), 20000);
        Text(fact.Property(x => x.SourceKind), 30);
        Text(fact.Property(x => x.SourceFingerprint), 64);
        Text(fact.Property(x => x.ClarificationEvidence), 20000);
        Text(fact.Property(x => x.EmbeddingModel), 100);
        if (postgres) fact.Property(x => x.Embedding).HasColumnType("vector(1536)");
        else fact.Ignore(x => x.Embedding); // Explicitly no vector support on SQLite/InMemory.

        var proposal = b.Entity<TimelineFactProposal>();
        proposal.ToTable("TimelineFactProposals");
        proposal.HasOne<TimelineEntry>().WithMany().HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.Restrict);
        proposal.HasOne<User>().WithMany().HasForeignKey(x => x.AcceptedById).OnDelete(DeleteBehavior.Restrict);
        proposal.Property(x => x.Status).IsConcurrencyToken();
        Text(proposal.Property(x => x.Status), 30);
        Text(proposal.Property(x => x.SourceFingerprint), 64);
        Text(proposal.Property(x => x.QuestionsJson), 20000);
        Text(proposal.Property(x => x.ClarificationEvidence), 20000);
        proposal.HasMany(x => x.Additions).WithOne().HasForeignKey(x => x.ProposalId).OnDelete(DeleteBehavior.Restrict);
        proposal.HasMany(x => x.Invalidations).WithOne().HasForeignKey(x => x.ProposalId).OnDelete(DeleteBehavior.Restrict);

        var addition = b.Entity<TimelineFactAddition>();
        addition.ToTable("TimelineFactAdditions");
        Text(addition.Property(x => x.Claim), 4000);
        Text(addition.Property(x => x.EntityKey), 300);
        Text(addition.Property(x => x.EntityType), 100);
        Text(addition.Property(x => x.SourceQuote), 20000);
        Text(addition.Property(x => x.SourceKind), 30);
        var invalidation = b.Entity<TimelineFactInvalidation>();
        invalidation.ToTable("TimelineFactInvalidations");
        invalidation.HasOne<TimelineFact>().WithMany().HasForeignKey(x => x.FactId).OnDelete(DeleteBehavior.Restrict);
        invalidation.HasOne<TimelineFactAddition>().WithMany().HasForeignKey(x => x.InvalidatingAdditionId).OnDelete(DeleteBehavior.Restrict);
        invalidation.HasIndex(x => new { x.ProposalId, x.FactId }).IsUnique();
        Text(invalidation.Property(x => x.Reason), 4000);

        var job = b.Entity<TimelineFactJob>();
        job.ToTable("TimelineFactJobs");
        job.HasOne<TimelineEntry>().WithMany().HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.Restrict);
        job.HasOne<TimelineFact>().WithMany().HasForeignKey(x => x.FactId).OnDelete(DeleteBehavior.Restrict);
        job.HasIndex(x => x.WorkKey).IsUnique();
        job.HasIndex(x => new { x.State, x.NextAttemptAtUtc, x.LeaseExpiresAtUtc });
        job.Property(x => x.Version).IsConcurrencyToken();
        job.Property(x => x.ExtractionResultHash).HasMaxLength(64);
        Text(job.Property(x => x.WorkKey), 300);
        Text(job.Property(x => x.Kind), 30);
        Text(job.Property(x => x.SourceFingerprint), 64);
        Text(job.Property(x => x.State), 30);
        Text(job.Property(x => x.Model), 100);
        job.Property(x => x.LeaseOwner).HasMaxLength(200);
        job.Property(x => x.LastErrorCode).HasMaxLength(100);
    }

    private static void Text(PropertyBuilder<string> property, int maxLength) => property.IsRequired().HasMaxLength(maxLength);
}
