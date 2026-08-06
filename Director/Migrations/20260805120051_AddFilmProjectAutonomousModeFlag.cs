using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Director.Migrations
{
    /// <inheritdoc />
    public partial class AddFilmProjectAutonomousModeFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutonomousModeEnabled",
                table: "FilmProjects",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutonomousModeEnabled",
                table: "FilmProjects");
        }
    }
}
