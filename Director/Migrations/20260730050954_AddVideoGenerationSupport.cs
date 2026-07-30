using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Director.Migrations
{
    /// <inheritdoc />
    public partial class AddVideoGenerationSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "Fps",
                table: "SceneMediaAssets",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FrameCount",
                table: "SceneMediaAssets",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceMediaAssetId",
                table: "SceneMediaAssets",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PromptPreparationModel",
                table: "GenerationJobs",
                type: "nvarchar(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PromptPreparedAt",
                table: "GenerationJobs",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceMediaAssetId",
                table: "GenerationJobs",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SceneMediaAssets_SourceMediaAssetId",
                table: "SceneMediaAssets",
                column: "SourceMediaAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_GenerationJobs_SourceMediaAssetId",
                table: "GenerationJobs",
                column: "SourceMediaAssetId");

            migrationBuilder.AddForeignKey(
                name: "FK_GenerationJobs_SceneMediaAssets_SourceMediaAssetId",
                table: "GenerationJobs",
                column: "SourceMediaAssetId",
                principalTable: "SceneMediaAssets",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SceneMediaAssets_SceneMediaAssets_SourceMediaAssetId",
                table: "SceneMediaAssets",
                column: "SourceMediaAssetId",
                principalTable: "SceneMediaAssets",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_GenerationJobs_SceneMediaAssets_SourceMediaAssetId",
                table: "GenerationJobs");

            migrationBuilder.DropForeignKey(
                name: "FK_SceneMediaAssets_SceneMediaAssets_SourceMediaAssetId",
                table: "SceneMediaAssets");

            migrationBuilder.DropIndex(
                name: "IX_SceneMediaAssets_SourceMediaAssetId",
                table: "SceneMediaAssets");

            migrationBuilder.DropIndex(
                name: "IX_GenerationJobs_SourceMediaAssetId",
                table: "GenerationJobs");

            migrationBuilder.DropColumn(
                name: "Fps",
                table: "SceneMediaAssets");

            migrationBuilder.DropColumn(
                name: "FrameCount",
                table: "SceneMediaAssets");

            migrationBuilder.DropColumn(
                name: "SourceMediaAssetId",
                table: "SceneMediaAssets");

            migrationBuilder.DropColumn(
                name: "PromptPreparationModel",
                table: "GenerationJobs");

            migrationBuilder.DropColumn(
                name: "PromptPreparedAt",
                table: "GenerationJobs");

            migrationBuilder.DropColumn(
                name: "SourceMediaAssetId",
                table: "GenerationJobs");
        }
    }
}
