using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinora.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase6PushAndPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PushComments",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "PushFriendAccepted",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "PushFriendRequests",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "PushReviewLikes",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "EndpointHash",
                table: "Devices",
                type: "char(64)",
                unicode: false,
                fixedLength: true,
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_UserId_EndpointHash",
                table: "Devices",
                columns: new[] { "UserId", "EndpointHash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Devices_UserId_EndpointHash",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "PushComments",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PushFriendAccepted",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PushFriendRequests",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PushReviewLikes",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "EndpointHash",
                table: "Devices");
        }
    }
}
