using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Director.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FilmProjects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TotalDurationMinutes = table.Column<int>(type: "int", nullable: false),
                    ClipDurationSeconds = table.Column<int>(type: "int", nullable: false),
                    CalculatedClipCount = table.Column<int>(type: "int", nullable: false),
                    Language = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    TargetAudience = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    StoryGenre = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    VisualStyle = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    VideoStyle = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    AspectRatio = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Resolution = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    UseNarrator = table.Column<bool>(type: "bit", nullable: false),
                    NarratorTone = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    MainCharacterDescription = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AdditionalInstructions = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETDATE()"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FilmProjects", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FilmProjects");
        }
    }
}
