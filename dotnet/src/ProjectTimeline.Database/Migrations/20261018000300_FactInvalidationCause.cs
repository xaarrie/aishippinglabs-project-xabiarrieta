using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectTimeline.DataAccessLogic;

#nullable disable
namespace ProjectTimeline.Database.Migrations;

[Migration("20261018000300_FactInvalidationCause")]
[DbContext(typeof(ProjectTimelineDbContext))]
public partial class FactInvalidationCause : DirectTimelineFactExtraction
{
    // Historical invalidations remain null: there is no trustworthy replacement to backfill.
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE "TimelineEntryFacts" ADD COLUMN "InvalidatedByFactId" uuid;
        ALTER TABLE "TimelineFactInvalidations" ADD COLUMN "InvalidatingAdditionId" uuid;
        CREATE INDEX "IX_TimelineEntryFacts_InvalidatedByFactId" ON "TimelineEntryFacts" ("InvalidatedByFactId");
        CREATE INDEX "IX_TimelineFactInvalidations_InvalidatingAdditionId" ON "TimelineFactInvalidations" ("InvalidatingAdditionId");
        ALTER TABLE "TimelineEntryFacts" ADD CONSTRAINT "FK_TimelineEntryFacts_TimelineEntryFacts_InvalidatedByFactId"
            FOREIGN KEY ("InvalidatedByFactId") REFERENCES "TimelineEntryFacts" ("Id") ON DELETE RESTRICT;
        ALTER TABLE "TimelineFactInvalidations" ADD CONSTRAINT "FK_TimelineFactInvalidations_TimelineFactAdditions_InvalidatingAdditionId"
            FOREIGN KEY ("InvalidatingAdditionId") REFERENCES "TimelineFactAdditions" ("Id") ON DELETE RESTRICT;
        ALTER TABLE "TimelineEntryFacts" ADD CONSTRAINT "CK_TimelineEntryFacts_InvalidationCause" CHECK (
            "InvalidatedByFactId" IS NULL OR ("InvalidatedByFactId" <> "Id" AND NOT "Valid"
                AND "InvalidatedAtUtc" IS NOT NULL AND "InvalidationReviewId" IS NOT NULL));
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM "TimelineEntryFacts" WHERE "InvalidatedByFactId" IS NOT NULL)
                OR EXISTS (SELECT 1 FROM "TimelineFactInvalidations" WHERE "InvalidatingAdditionId" IS NOT NULL) THEN
                RAISE EXCEPTION 'Invalidation causality requires explicit preservation before rollback';
            END IF;
        END $$;
        ALTER TABLE "TimelineEntryFacts" DROP CONSTRAINT "CK_TimelineEntryFacts_InvalidationCause";
        ALTER TABLE "TimelineEntryFacts" DROP CONSTRAINT "FK_TimelineEntryFacts_TimelineEntryFacts_InvalidatedByFactId";
        ALTER TABLE "TimelineFactInvalidations" DROP CONSTRAINT "FK_TimelineFactInvalidations_TimelineFactAdditions_InvalidatingAdditionId";
        DROP INDEX "IX_TimelineEntryFacts_InvalidatedByFactId";
        DROP INDEX "IX_TimelineFactInvalidations_InvalidatingAdditionId";
        ALTER TABLE "TimelineEntryFacts" DROP COLUMN "InvalidatedByFactId";
        ALTER TABLE "TimelineFactInvalidations" DROP COLUMN "InvalidatingAdditionId";
        """);
}
