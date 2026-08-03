using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Director.Migrations
{
    /// <inheritdoc />
    public partial class AddAudioGenerationModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Role",
                table: "SceneMediaAssets",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "CharacterVoiceProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FilmProjectId = table.Column<int>(type: "int", nullable: false),
                    StoryCharacterId = table.Column<int>(type: "int", nullable: true),
                    ProfileName = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Provider = table.Column<int>(type: "int", nullable: false),
                    ModelType = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    VoicePresetKey = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    VoicePresetDisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Language = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    SpeakingRate = table.Column<double>(type: "float", nullable: false),
                    EmotionStyle = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    CfgScale = table.Column<double>(type: "float", nullable: true),
                    Seed = table.Column<int>(type: "int", nullable: true),
                    IsNarrator = table.Column<bool>(type: "bit", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETDATE()"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterVoiceProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterVoiceProfiles_FilmProjects_FilmProjectId",
                        column: x => x.FilmProjectId,
                        principalTable: "FilmProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CharacterVoiceProfiles_StoryCharacters_StoryCharacterId",
                        column: x => x.StoryCharacterId,
                        principalTable: "StoryCharacters",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SceneSpeechPlans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FilmProjectId = table.Column<int>(type: "int", nullable: false),
                    SceneId = table.Column<int>(type: "int", nullable: false),
                    TargetDurationSeconds = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETDATE()"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SceneSpeechPlans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SceneSpeechPlans_FilmProjects_FilmProjectId",
                        column: x => x.FilmProjectId,
                        principalTable: "FilmProjects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SceneSpeechPlans_FilmScenes_SceneId",
                        column: x => x.SceneId,
                        principalTable: "FilmScenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SceneSpeechSegments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SceneSpeechPlanId = table.Column<int>(type: "int", nullable: false),
                    SpeakerType = table.Column<int>(type: "int", nullable: false),
                    StoryCharacterId = table.Column<int>(type: "int", nullable: true),
                    SpeakerKey = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    TurkishText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Emotion = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    StartTimeSeconds = table.Column<double>(type: "float", nullable: false),
                    TargetDurationSeconds = table.Column<double>(type: "float", nullable: false),
                    ActualDurationSeconds = table.Column<double>(type: "float", nullable: true),
                    VoiceProfileId = table.Column<int>(type: "int", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETDATE()"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SceneSpeechSegments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SceneSpeechSegments_CharacterVoiceProfiles_VoiceProfileId",
                        column: x => x.VoiceProfileId,
                        principalTable: "CharacterVoiceProfiles",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SceneSpeechSegments_SceneSpeechPlans_SceneSpeechPlanId",
                        column: x => x.SceneSpeechPlanId,
                        principalTable: "SceneSpeechPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SceneSpeechSegments_StoryCharacters_StoryCharacterId",
                        column: x => x.StoryCharacterId,
                        principalTable: "StoryCharacters",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_CharacterVoiceProfiles_FilmProjectId_IsNarrator_IsDefault",
                table: "CharacterVoiceProfiles",
                columns: new[] { "FilmProjectId", "IsNarrator", "IsDefault" });

            migrationBuilder.CreateIndex(
                name: "IX_CharacterVoiceProfiles_FilmProjectId_StoryCharacterId_IsDefault",
                table: "CharacterVoiceProfiles",
                columns: new[] { "FilmProjectId", "StoryCharacterId", "IsDefault" });

            migrationBuilder.CreateIndex(
                name: "IX_CharacterVoiceProfiles_StoryCharacterId",
                table: "CharacterVoiceProfiles",
                column: "StoryCharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_SceneSpeechPlans_FilmProjectId",
                table: "SceneSpeechPlans",
                column: "FilmProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_SceneSpeechPlans_SceneId",
                table: "SceneSpeechPlans",
                column: "SceneId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SceneSpeechSegments_SceneSpeechPlanId_SortOrder",
                table: "SceneSpeechSegments",
                columns: new[] { "SceneSpeechPlanId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_SceneSpeechSegments_StoryCharacterId",
                table: "SceneSpeechSegments",
                column: "StoryCharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_SceneSpeechSegments_VoiceProfileId",
                table: "SceneSpeechSegments",
                column: "VoiceProfileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SceneSpeechSegments");

            migrationBuilder.DropTable(
                name: "CharacterVoiceProfiles");

            migrationBuilder.DropTable(
                name: "SceneSpeechPlans");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "SceneMediaAssets");
        }
    }
}
