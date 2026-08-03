using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Director.Migrations
{
    /// <inheritdoc />
    public partial class AddLtxNativeDialogueMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LtxNativeVoiceProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FilmProjectId = table.Column<int>(type: "int", nullable: false),
                    StoryCharacterId = table.Column<int>(type: "int", nullable: false),
                    VoiceDescription = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Language = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    SpeakingStyle = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    PerceivedAge = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    GenderPresentation = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    AccentDescription = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: false),
                    PitchDescription = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    TempoDescription = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    IsLocked = table.Column<bool>(type: "bit", nullable: false),
                    SettingsHash = table.Column<string>(type: "nvarchar(96)", maxLength: 96, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETDATE()"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LtxNativeVoiceProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LtxNativeVoiceProfiles_FilmProjects_FilmProjectId",
                        column: x => x.FilmProjectId,
                        principalTable: "FilmProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LtxNativeVoiceProfiles_StoryCharacters_StoryCharacterId",
                        column: x => x.StoryCharacterId,
                        principalTable: "StoryCharacters",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_LtxNativeVoiceProfiles_FilmProjectId_StoryCharacterId",
                table: "LtxNativeVoiceProfiles",
                columns: new[] { "FilmProjectId", "StoryCharacterId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LtxNativeVoiceProfiles_StoryCharacterId",
                table: "LtxNativeVoiceProfiles",
                column: "StoryCharacterId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LtxNativeVoiceProfiles");
        }
    }
}
