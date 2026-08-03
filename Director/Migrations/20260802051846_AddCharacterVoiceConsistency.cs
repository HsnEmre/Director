using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Director.Migrations
{
    /// <inheritdoc />
    public partial class AddCharacterVoiceConsistency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DoSample",
                table: "CharacterVoiceProfiles",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsLocked",
                table: "CharacterVoiceProfiles",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MaxNewTokens",
                table: "CharacterVoiceProfiles",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SettingsHash",
                table: "CharacterVoiceProfiles",
                type: "nvarchar(96)",
                maxLength: 96,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<double>(
                name: "Temperature",
                table: "CharacterVoiceProfiles",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UseEmotionStyling",
                table: "CharacterVoiceProfiles",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_CharacterVoiceProfiles_FilmProjectId_StoryCharacterId_IsDefault_IsLocked",
                table: "CharacterVoiceProfiles",
                columns: new[] { "FilmProjectId", "StoryCharacterId", "IsDefault", "IsLocked" },
                unique: true,
                filter: "[StoryCharacterId] IS NOT NULL AND [IsDefault] = 1 AND [IsLocked] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CharacterVoiceProfiles_FilmProjectId_StoryCharacterId_IsDefault_IsLocked",
                table: "CharacterVoiceProfiles");

            migrationBuilder.DropColumn(
                name: "DoSample",
                table: "CharacterVoiceProfiles");

            migrationBuilder.DropColumn(
                name: "IsLocked",
                table: "CharacterVoiceProfiles");

            migrationBuilder.DropColumn(
                name: "MaxNewTokens",
                table: "CharacterVoiceProfiles");

            migrationBuilder.DropColumn(
                name: "SettingsHash",
                table: "CharacterVoiceProfiles");

            migrationBuilder.DropColumn(
                name: "Temperature",
                table: "CharacterVoiceProfiles");

            migrationBuilder.DropColumn(
                name: "UseEmotionStyling",
                table: "CharacterVoiceProfiles");
        }
    }
}
