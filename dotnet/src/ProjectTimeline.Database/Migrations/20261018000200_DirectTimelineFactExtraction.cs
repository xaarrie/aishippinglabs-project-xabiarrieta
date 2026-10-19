using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectTimeline.DataAccessLogic;

#nullable disable
namespace ProjectTimeline.Database.Migrations;

[Migration("20261018000200_DirectTimelineFactExtraction")]
[DbContext(typeof(ProjectTimelineDbContext))]
public partial class DirectTimelineFactExtraction : TimelineEntrySnapshotsModel
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE "TimelineFactJobs" ADD COLUMN "ExtractionRevision" bigint;
        ALTER TABLE "TimelineFactJobs" ADD COLUMN "ExtractionResultHash" varchar(64);
        ALTER TABLE "TimelineEntryFacts" ALTER COLUMN "CreationReviewId" DROP NOT NULL;
        ALTER TABLE "TimelineEntryFacts" ALTER COLUMN "AdditionId" DROP NOT NULL;
        ALTER TABLE "TimelineEntryFacts" ADD COLUMN "ExtractionJobId" uuid;
        ALTER TABLE "TimelineEntryFacts" ADD COLUMN "CandidateKey" varchar(64);
        ALTER TABLE "TimelineEntryFacts" ADD CONSTRAINT "FK_TimelineEntryFacts_TimelineFactJobs_ExtractionJobId"
            FOREIGN KEY ("ExtractionJobId") REFERENCES "TimelineFactJobs" ("Id") ON DELETE RESTRICT;
        CREATE UNIQUE INDEX "IX_TimelineEntryFacts_ExtractionJobId_CandidateKey"
            ON "TimelineEntryFacts" ("ExtractionJobId", "CandidateKey");
        ALTER TABLE "TimelineEntryFacts" ADD CONSTRAINT "CK_TimelineEntryFacts_Origin" CHECK (
            ("ExtractionJobId" IS NULL AND "CandidateKey" IS NULL AND "CreationReviewId" IS NOT NULL AND "AdditionId" IS NOT NULL)
            OR ("ExtractionJobId" IS NOT NULL AND "CandidateKey" IS NOT NULL AND "CreationReviewId" IS NULL AND "AdditionId" IS NULL
                AND "SourceKind" = 'Entry' AND "ClarificationEvidence" = ''));
        """);

    // Rollback cannot silently discard direct facts or invent review evidence for them.
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM "TimelineEntryFacts" WHERE "ExtractionJobId" IS NOT NULL) THEN
                RAISE EXCEPTION 'Direct extraction facts require explicit preservation before rollback';
            END IF;
        END $$;
        ALTER TABLE "TimelineEntryFacts" DROP CONSTRAINT "CK_TimelineEntryFacts_Origin";
        ALTER TABLE "TimelineEntryFacts" DROP CONSTRAINT "FK_TimelineEntryFacts_TimelineFactJobs_ExtractionJobId";
        DROP INDEX "IX_TimelineEntryFacts_ExtractionJobId_CandidateKey";
        ALTER TABLE "TimelineEntryFacts" DROP COLUMN "ExtractionJobId", DROP COLUMN "CandidateKey";
        ALTER TABLE "TimelineEntryFacts" ALTER COLUMN "CreationReviewId" SET NOT NULL;
        ALTER TABLE "TimelineEntryFacts" ALTER COLUMN "AdditionId" SET NOT NULL;
        ALTER TABLE "TimelineFactJobs" DROP COLUMN "ExtractionRevision", DROP COLUMN "ExtractionResultHash";
        """);
}
