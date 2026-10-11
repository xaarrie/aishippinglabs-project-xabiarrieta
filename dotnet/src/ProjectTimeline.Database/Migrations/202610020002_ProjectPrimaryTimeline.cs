using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectTimeline.Database.Migrations
{
    /// <inheritdoc />
    [Migration("202610020002_ProjectPrimaryTimeline")]
    public partial class ProjectPrimaryTimeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PrimaryTimelineId",
                table: "Projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_PrimaryTimelineId",
                table: "Projects",
                column: "PrimaryTimelineId");

            migrationBuilder.AddForeignKey(
                name: "FK_Projects_Timelines_PrimaryTimelineId",
                table: "Projects",
                column: "PrimaryTimelineId",
                principalTable: "Timelines",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Projects_Timelines_PrimaryTimelineId",
                table: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_Projects_PrimaryTimelineId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "PrimaryTimelineId",
                table: "Projects");
        }
    }
}
