using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Director.Migrations
{
    /// <inheritdoc />
    public partial class AddStoryGenerationModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FilmStories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FilmProjectId = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    Logline = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Synopsis = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OpeningSummary = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DevelopmentSummary = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ClimaxSummary = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EndingSummary = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    WorldDescription = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    VisualDirection = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContinuityRulesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETDATE()"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FilmStories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FilmStories_FilmProjects_FilmProjectId",
                        column: x => x.FilmProjectId,
                        principalTable: "FilmProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FilmScenes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FilmProjectId = table.Column<int>(type: "int", nullable: false),
                    FilmStoryId = table.Column<int>(type: "int", nullable: false),
                    SceneNumber = table.Column<int>(type: "int", nullable: false),
                    DurationSeconds = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    StoryBeat = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SceneDescription = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LocationDescription = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TimeOfDay = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    CharactersJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContinuityFromPreviousScene = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ImagePrompt = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ImageNegativePrompt = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    VideoPrompt = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    VideoNegativePrompt = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    NarrationText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DialogueJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ValidationChecklistJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETDATE()"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FilmScenes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FilmScenes_FilmProjects_FilmProjectId",
                        column: x => x.FilmProjectId,
                        principalTable: "FilmProjects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_FilmScenes_FilmStories_FilmStoryId",
                        column: x => x.FilmStoryId,
                        principalTable: "FilmStories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StoryCharacters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FilmStoryId = table.Column<int>(type: "int", nullable: false),
                    CharacterKey = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    PhysicalDescription = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ClothingDescription = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PersonalityDescription = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    VoiceDescription = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContinuityDescription = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ForbiddenChangesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoryCharacters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StoryCharacters_FilmStories_FilmStoryId",
                        column: x => x.FilmStoryId,
                        principalTable: "FilmStories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FilmScenes_FilmProjectId_SceneNumber",
                table: "FilmScenes",
                columns: new[] { "FilmProjectId", "SceneNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FilmScenes_FilmStoryId",
                table: "FilmScenes",
                column: "FilmStoryId");

            migrationBuilder.CreateIndex(
                name: "IX_FilmStories_FilmProjectId",
                table: "FilmStories",
                column: "FilmProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StoryCharacters_FilmStoryId_CharacterKey",
                table: "StoryCharacters",
                columns: new[] { "FilmStoryId", "CharacterKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FilmScenes");

            migrationBuilder.DropTable(
                name: "StoryCharacters");

            migrationBuilder.DropTable(
                name: "FilmStories");
        }
    }
}
