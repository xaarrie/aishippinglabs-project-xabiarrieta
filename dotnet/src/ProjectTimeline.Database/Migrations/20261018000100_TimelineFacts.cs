using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectTimeline.DataAccessLogic;

#nullable disable
namespace ProjectTimeline.Database.Migrations;

// Inherit only the frozen preceding target model; both DDL directions are overridden here.
// This avoids referencing the mutable current snapshot/runtime model from a historical migration.
[Migration("20261018000100_TimelineFacts")]
[DbContext(typeof(ProjectTimelineDbContext))]
public partial class TimelineFacts : TimelineEntrySnapshotsModel
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE EXTENSION IF NOT EXISTS vector;
        ALTER TABLE "Timelines" ADD COLUMN "FactRevision" bigint NOT NULL DEFAULT 0;
        CREATE TABLE "TimelineFactProposals" (
            "Id" uuid NOT NULL CONSTRAINT "PK_TimelineFactProposals" PRIMARY KEY,
            "EntryId" uuid NOT NULL CONSTRAINT "FK_TimelineFactProposals_TimelineEntries_EntryId" REFERENCES "TimelineEntries"("Id") ON DELETE RESTRICT,
            "ExpectedRevision" bigint NOT NULL, "SourceFingerprint" varchar(64) NOT NULL,
            "QuestionsJson" varchar(20000) NOT NULL, "ClarificationEvidence" varchar(20000) NOT NULL,
            "Status" varchar(30) NOT NULL, "AcceptedById" uuid CONSTRAINT "FK_TimelineFactProposals_Users_AcceptedById" REFERENCES "Users"("Id") ON DELETE RESTRICT,
            "AcceptedAtUtc" timestamp with time zone,
            "CreatedAtUtc" timestamp with time zone NOT NULL, "UpdatedAtUtc" timestamp with time zone NOT NULL
        );
        CREATE INDEX "IX_TimelineFactProposals_EntryId" ON "TimelineFactProposals" ("EntryId");
        CREATE INDEX "IX_TimelineFactProposals_AcceptedById" ON "TimelineFactProposals" ("AcceptedById");
        CREATE TABLE "TimelineFactAdditions" (
            "Id" uuid NOT NULL CONSTRAINT "PK_TimelineFactAdditions" PRIMARY KEY,
            "ProposalId" uuid NOT NULL CONSTRAINT "FK_TimelineFactAdditions_TimelineFactProposals_ProposalId" REFERENCES "TimelineFactProposals"("Id") ON DELETE RESTRICT,
            "Claim" varchar(4000) NOT NULL, "EntityKey" varchar(300) NOT NULL, "EntityType" varchar(100) NOT NULL,
            "SourceQuote" varchar(20000) NOT NULL, "SourceKind" varchar(30) NOT NULL,
            "CreatedAtUtc" timestamp with time zone NOT NULL, "UpdatedAtUtc" timestamp with time zone NOT NULL
        );
        CREATE INDEX "IX_TimelineFactAdditions_ProposalId" ON "TimelineFactAdditions" ("ProposalId");
        CREATE TABLE "TimelineEntryFacts" (
            "Id" uuid NOT NULL CONSTRAINT "PK_TimelineEntryFacts" PRIMARY KEY,
            "EntryId" uuid NOT NULL CONSTRAINT "FK_TimelineEntryFacts_TimelineEntries_EntryId" REFERENCES "TimelineEntries"("Id") ON DELETE RESTRICT,
            "CreationReviewId" uuid NOT NULL CONSTRAINT "FK_TimelineEntryFacts_TimelineFactProposals_CreationReviewId" REFERENCES "TimelineFactProposals"("Id") ON DELETE RESTRICT,
            "AdditionId" uuid NOT NULL CONSTRAINT "FK_TimelineEntryFacts_TimelineFactAdditions_AdditionId" REFERENCES "TimelineFactAdditions"("Id") ON DELETE RESTRICT,
            "Claim" varchar(4000) NOT NULL, "EntityKey" varchar(300) NOT NULL, "EntityType" varchar(100) NOT NULL,
            "SourceQuote" varchar(20000) NOT NULL, "SourceKind" varchar(30) NOT NULL,
            "SourceFingerprint" varchar(64) NOT NULL, "ClarificationEvidence" varchar(20000) NOT NULL,
            "Valid" boolean NOT NULL, "InvalidatedAtUtc" timestamp with time zone,
            "InvalidationReviewId" uuid CONSTRAINT "FK_TimelineEntryFacts_TimelineFactProposals_InvalidationReview~" REFERENCES "TimelineFactProposals"("Id") ON DELETE RESTRICT,
            "Embedding" vector(1536), "EmbeddingModel" varchar(100) NOT NULL, "EmbeddedAtUtc" timestamp with time zone,
            "CreatedAtUtc" timestamp with time zone NOT NULL, "UpdatedAtUtc" timestamp with time zone NOT NULL
        );
        CREATE UNIQUE INDEX "IX_TimelineEntryFacts_AdditionId" ON "TimelineEntryFacts" ("AdditionId");
        CREATE INDEX "IX_TimelineEntryFacts_EntryId_Valid" ON "TimelineEntryFacts" ("EntryId", "Valid");
        CREATE INDEX "IX_TimelineEntryFacts_CreationReviewId" ON "TimelineEntryFacts" ("CreationReviewId");
        CREATE INDEX "IX_TimelineEntryFacts_InvalidationReviewId" ON "TimelineEntryFacts" ("InvalidationReviewId");
        CREATE TABLE "TimelineFactInvalidations" (
            "Id" uuid NOT NULL CONSTRAINT "PK_TimelineFactInvalidations" PRIMARY KEY,
            "ProposalId" uuid NOT NULL CONSTRAINT "FK_TimelineFactInvalidations_TimelineFactProposals_ProposalId" REFERENCES "TimelineFactProposals"("Id") ON DELETE RESTRICT,
            "FactId" uuid NOT NULL CONSTRAINT "FK_TimelineFactInvalidations_TimelineEntryFacts_FactId" REFERENCES "TimelineEntryFacts"("Id") ON DELETE RESTRICT,
            "Reason" varchar(4000) NOT NULL,
            "CreatedAtUtc" timestamp with time zone NOT NULL, "UpdatedAtUtc" timestamp with time zone NOT NULL
        );
        CREATE UNIQUE INDEX "IX_TimelineFactInvalidations_ProposalId_FactId" ON "TimelineFactInvalidations" ("ProposalId", "FactId");
        CREATE INDEX "IX_TimelineFactInvalidations_FactId" ON "TimelineFactInvalidations" ("FactId");
        CREATE TABLE "TimelineFactJobs" (
            "Id" uuid NOT NULL CONSTRAINT "PK_TimelineFactJobs" PRIMARY KEY,
            "EntryId" uuid NOT NULL CONSTRAINT "FK_TimelineFactJobs_TimelineEntries_EntryId" REFERENCES "TimelineEntries"("Id") ON DELETE RESTRICT,
            "FactId" uuid CONSTRAINT "FK_TimelineFactJobs_TimelineEntryFacts_FactId" REFERENCES "TimelineEntryFacts"("Id") ON DELETE RESTRICT,
            "WorkKey" varchar(300) NOT NULL, "Kind" varchar(30) NOT NULL, "SourceFingerprint" varchar(64) NOT NULL,
            "State" varchar(30) NOT NULL, "Model" varchar(100) NOT NULL, "Attempts" integer NOT NULL,
            "NextAttemptAtUtc" timestamp with time zone, "LeaseOwner" varchar(200), "LeaseExpiresAtUtc" timestamp with time zone,
            "LastErrorCode" varchar(100), "Version" uuid NOT NULL,
            "CreatedAtUtc" timestamp with time zone NOT NULL, "UpdatedAtUtc" timestamp with time zone NOT NULL
        );
        CREATE UNIQUE INDEX "IX_TimelineFactJobs_WorkKey" ON "TimelineFactJobs" ("WorkKey");
        CREATE INDEX "IX_TimelineFactJobs_EntryId" ON "TimelineFactJobs" ("EntryId");
        CREATE INDEX "IX_TimelineFactJobs_FactId" ON "TimelineFactJobs" ("FactId");
        CREATE INDEX "IX_TimelineFactJobs_State_NextAttemptAtUtc_LeaseExpiresAtUtc" ON "TimelineFactJobs" ("State", "NextAttemptAtUtc", "LeaseExpiresAtUtc");
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP TABLE "TimelineFactJobs";
        DROP TABLE "TimelineFactInvalidations";
        DROP TABLE "TimelineEntryFacts";
        DROP TABLE "TimelineFactAdditions";
        DROP TABLE "TimelineFactProposals";
        ALTER TABLE "Timelines" DROP COLUMN "FactRevision";
        -- Deliberately keep the shared vector extension.
        """);
}
