using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinora.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FriendLookupIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Friends_AddresseeId",
                table: "Friends");

            migrationBuilder.CreateIndex(
                name: "IX_Friends_AddresseeId_Status",
                table: "Friends",
                columns: new[] { "AddresseeId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Friends_RequesterId_Status",
                table: "Friends",
                columns: new[] { "RequesterId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Friends_AddresseeId_Status",
                table: "Friends");

            migrationBuilder.DropIndex(
                name: "IX_Friends_RequesterId_Status",
                table: "Friends");

            migrationBuilder.CreateIndex(
                name: "IX_Friends_AddresseeId",
                table: "Friends",
                column: "AddresseeId");
        }
    }
}
