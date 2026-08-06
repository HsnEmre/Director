using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Director.Migrations
{
    /// <inheritdoc />
    public partial class AddAutonomousGeneration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AutonomousGenerationRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FilmProjectId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CurrentStage = table.Column<int>(type: "int", nullable: false),
                    CurrentSceneId = table.Column<int>(type: "int", nullable: true),
                    CurrentSceneNumber = table.Column<int>(type: "int", nullable: true),
                    TotalSceneCount = table.Column<int>(type: "int", nullable: false),
                    CompletedSceneCount = table.Column<int>(type: "int", nullable: false),
                    OverallProgressPercentage = table.Column<double>(type: "float", nullable: false),
                    StageProgressPercentage = table.Column<double>(type: "float", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastHeartbeatAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CancellationRequested = table.Column<bool>(type: "bit", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ConfigurationSnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LastMessage = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutonomousGenerationRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutonomousGenerationRuns_FilmProjects_FilmProjectId",
                        column: x => x.FilmProjectId,
                        principalTable: "FilmProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AutonomousGenerationRuns_FilmScenes_CurrentSceneId",
                        column: x => x.CurrentSceneId,
                        principalTable: "FilmScenes",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "AutonomousSceneWorkItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AutonomousGenerationRunId = table.Column<int>(type: "int", nullable: false),
                    StorySceneId = table.Column<int>(type: "int", nullable: false),
                    SceneNumber = table.Column<int>(type: "int", nullable: false),
                    ImageStatus = table.Column<int>(type: "int", nullable: false),
                    ImageAttemptCount = table.Column<int>(type: "int", nullable: false),
                    ImageMediaAssetId = table.Column<int>(type: "int", nullable: true),
                    VideoStatus = table.Column<int>(type: "int", nullable: false),
                    VideoAttemptCount = table.Column<int>(type: "int", nullable: false),
                    VideoMediaAssetId = table.Column<int>(type: "int", nullable: true),
                    AudioStatus = table.Column<int>(type: "int", nullable: false),
                    AudioAttemptCount = table.Column<int>(type: "int", nullable: false),
                    AudioMediaAssetId = table.Column<int>(type: "int", nullable: true),
                    FinalizationStatus = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutonomousSceneWorkItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutonomousSceneWorkItems_AutonomousGenerationRuns_AutonomousGenerationRunId",
                        column: x => x.AutonomousGenerationRunId,
                        principalTable: "AutonomousGenerationRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AutonomousSceneWorkItems_FilmScenes_StorySceneId",
                        column: x => x.StorySceneId,
                        principalTable: "FilmScenes",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AutonomousSceneWorkItems_SceneMediaAssets_AudioMediaAssetId",
                        column: x => x.AudioMediaAssetId,
                        principalTable: "SceneMediaAssets",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AutonomousSceneWorkItems_SceneMediaAssets_ImageMediaAssetId",
                        column: x => x.ImageMediaAssetId,
                        principalTable: "SceneMediaAssets",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AutonomousSceneWorkItems_SceneMediaAssets_VideoMediaAssetId",
                        column: x => x.VideoMediaAssetId,
                        principalTable: "SceneMediaAssets",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutonomousGenerationRuns_CorrelationId",
                table: "AutonomousGenerationRuns",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_AutonomousGenerationRuns_CurrentSceneId",
                table: "AutonomousGenerationRuns",
                column: "CurrentSceneId");

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

            migrationBuilder.CreateIndex(
                name: "IX_AutonomousSceneWorkItems_AudioMediaAssetId",
                table: "AutonomousSceneWorkItems",
                column: "AudioMediaAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_AutonomousSceneWorkItems_AutonomousGenerationRunId_SceneNumber",
                table: "AutonomousSceneWorkItems",
                columns: new[] { "AutonomousGenerationRunId", "SceneNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutonomousSceneWorkItems_ImageMediaAssetId",
                table: "AutonomousSceneWorkItems",
                column: "ImageMediaAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_AutonomousSceneWorkItems_StorySceneId_AudioStatus",
                table: "AutonomousSceneWorkItems",
                columns: new[] { "StorySceneId", "AudioStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_AutonomousSceneWorkItems_StorySceneId_AutonomousGenerationRunId",
                table: "AutonomousSceneWorkItems",
                columns: new[] { "StorySceneId", "AutonomousGenerationRunId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutonomousSceneWorkItems_StorySceneId_ImageStatus",
                table: "AutonomousSceneWorkItems",
                columns: new[] { "StorySceneId", "ImageStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_AutonomousSceneWorkItems_StorySceneId_VideoStatus",
                table: "AutonomousSceneWorkItems",
                columns: new[] { "StorySceneId", "VideoStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_AutonomousSceneWorkItems_VideoMediaAssetId",
                table: "AutonomousSceneWorkItems",
                column: "VideoMediaAssetId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutonomousSceneWorkItems");

            migrationBuilder.DropTable(
                name: "AutonomousGenerationRuns");
        }
    }
}
