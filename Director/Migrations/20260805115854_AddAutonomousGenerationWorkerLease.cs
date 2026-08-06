using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Director.Migrations
{
    /// <inheritdoc />
    public partial class AddAutonomousGenerationWorkerLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LeaseExpiresAtUtc",
                table: "AutonomousGenerationRuns",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkerId",
                table: "AutonomousGenerationRuns",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LeaseExpiresAtUtc",
                table: "AutonomousGenerationRuns");

            migrationBuilder.DropColumn(
                name: "WorkerId",
                table: "AutonomousGenerationRuns");
        }
    }
}
