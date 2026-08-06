using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Director.Migrations
{
    /// <inheritdoc />
    public partial class FixAutonomousActiveRunUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AutonomousGenerationRuns_FilmProjectId",
                table: "AutonomousGenerationRuns");

            migrationBuilder.DropIndex(
                name: "IX_AutonomousGenerationRuns_FilmProjectId_Status",
                table: "AutonomousGenerationRuns");

            migrationBuilder.CreateIndex(
                name: "IX_AutonomousGenerationRuns_FilmProjectId_Active",
                table: "AutonomousGenerationRuns",
                column: "FilmProjectId",
                unique: true,
                filter: "[Status] IN (0, 1, 2, 3, 4, 5, 6, 7, 10, 12)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AutonomousGenerationRuns_FilmProjectId_Active",
                table: "AutonomousGenerationRuns");

            migrationBuilder.CreateIndex(
                name: "IX_AutonomousGenerationRuns_FilmProjectId",
                table: "AutonomousGenerationRuns",
                column: "FilmProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_AutonomousGenerationRuns_FilmProjectId_Status",
                table: "AutonomousGenerationRuns",
                columns: new[] { "FilmProjectId", "Status" },
                unique: true,
                filter: "[Status] IN (0, 1, 2, 3, 4, 5, 6, 7, 10, 12)");
        }
    }
}
