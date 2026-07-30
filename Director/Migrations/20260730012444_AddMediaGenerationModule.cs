using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Director.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaGenerationModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GenerationJobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FilmProjectId = table.Column<int>(type: "int", nullable: false),
                    SceneId = table.Column<int>(type: "int", nullable: false),
                    MediaType = table.Column<int>(type: "int", nullable: false),
                    Provider = table.Column<int>(type: "int", nullable: false),
                    ExternalJobId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ModelType = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Prompt = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    NegativePrompt = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SettingsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ProgressPercentage = table.Column<double>(type: "float", nullable: false),
                    CurrentPhase = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    CurrentStep = table.Column<int>(type: "int", nullable: true),
                    TotalSteps = table.Column<int>(type: "int", nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancelRequestedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETDATE()"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenerationJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GenerationJobs_FilmProjects_FilmProjectId",
                        column: x => x.FilmProjectId,
                        principalTable: "FilmProjects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_GenerationJobs_FilmScenes_SceneId",
                        column: x => x.SceneId,
                        principalTable: "FilmScenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SceneMediaAssets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FilmProjectId = table.Column<int>(type: "int", nullable: false),
                    SceneId = table.Column<int>(type: "int", nullable: false),
                    GenerationJobId = table.Column<int>(type: "int", nullable: false),
                    MediaType = table.Column<int>(type: "int", nullable: false),
                    FilePath = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ThumbnailPath = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OriginalFileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    FileExtension = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    Width = table.Column<int>(type: "int", nullable: true),
                    Height = table.Column<int>(type: "int", nullable: true),
                    DurationSeconds = table.Column<double>(type: "float", nullable: true),
                    Seed = table.Column<int>(type: "int", nullable: true),
                    ModelType = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    MetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    IsSelected = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SceneMediaAssets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SceneMediaAssets_FilmProjects_FilmProjectId",
                        column: x => x.FilmProjectId,
                        principalTable: "FilmProjects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SceneMediaAssets_FilmScenes_SceneId",
                        column: x => x.SceneId,
                        principalTable: "FilmScenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SceneMediaAssets_GenerationJobs_GenerationJobId",
                        column: x => x.GenerationJobId,
                        principalTable: "GenerationJobs",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_GenerationJobs_ExternalJobId",
                table: "GenerationJobs",
                column: "ExternalJobId");

            migrationBuilder.CreateIndex(
                name: "IX_GenerationJobs_FilmProjectId",
                table: "GenerationJobs",
                column: "FilmProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_GenerationJobs_SceneId_MediaType_Status",
                table: "GenerationJobs",
                columns: new[] { "SceneId", "MediaType", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SceneMediaAssets_FilmProjectId",
                table: "SceneMediaAssets",
                column: "FilmProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_SceneMediaAssets_GenerationJobId",
                table: "SceneMediaAssets",
                column: "GenerationJobId");

            migrationBuilder.CreateIndex(
                name: "IX_SceneMediaAssets_SceneId_MediaType_IsSelected",
                table: "SceneMediaAssets",
                columns: new[] { "SceneId", "MediaType", "IsSelected" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SceneMediaAssets");

            migrationBuilder.DropTable(
                name: "GenerationJobs");
        }
    }
}
