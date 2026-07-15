using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinora.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddChatMovieShare : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SharedMovieMediaType",
                table: "Messages",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SharedMoviePosterPath",
                table: "Messages",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SharedMovieTitle",
                table: "Messages",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SharedMovieTmdbId",
                table: "Messages",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SharedMovieMediaType",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "SharedMoviePosterPath",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "SharedMovieTitle",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "SharedMovieTmdbId",
                table: "Messages");
        }
    }
}
