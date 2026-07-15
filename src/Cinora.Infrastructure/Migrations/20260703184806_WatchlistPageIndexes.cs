using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinora.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class WatchlistPageIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Watchlists_UserId_AddedAtUtc",
                table: "Watchlists",
                columns: new[] { "UserId", "AddedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Watchlists_UserId_Status_AddedAtUtc",
                table: "Watchlists",
                columns: new[] { "UserId", "Status", "AddedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Watchlists_UserId_AddedAtUtc",
                table: "Watchlists");

            migrationBuilder.DropIndex(
                name: "IX_Watchlists_UserId_Status_AddedAtUtc",
                table: "Watchlists");
        }
    }
}
