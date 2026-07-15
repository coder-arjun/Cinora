using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinora.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReviewListIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Reviews_MovieId",
                table: "Reviews");

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_MovieId_CreatedAtUtc",
                table: "Reviews",
                columns: new[] { "MovieId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Reviews_MovieId_CreatedAtUtc",
                table: "Reviews");

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_MovieId",
                table: "Reviews",
                column: "MovieId");
        }
    }
}
