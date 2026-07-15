using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinora.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DisplayNameHandleAndDefaultLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DefaultLanguage",
                table: "Users",
                type: "nvarchar(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedDisplayName",
                table: "Users",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            // Backfill existing rows to match User.Normalize (Trim + upper-invariant) BEFORE the unique index is
            // created, so pre-existing users get a real, unique login handle rather than the "" AddColumn default.
            // Wrapped in EXEC(N'...') so that in the IDEMPOTENT migration SCRIPT this DML sits opaque at batch
            // compile time: SQL Server binds UPDATE column names up front, and a bare UPDATE here fails with
            // "Invalid column name 'NormalizedDisplayName'" because the column is only ADDED earlier in the same
            // batch (added at run time, invisible to the compiler). EXEC defers binding to execution, after the
            // ADD COLUMN has run. Programmatic migration is unaffected — each op already runs as its own command.
            migrationBuilder.Sql(
                "EXEC(N'UPDATE [Users] SET [NormalizedDisplayName] = UPPER(LTRIM(RTRIM([DisplayName])))');");

            migrationBuilder.CreateIndex(
                name: "IX_Users_NormalizedDisplayName",
                table: "Users",
                column: "NormalizedDisplayName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_NormalizedDisplayName",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DefaultLanguage",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "NormalizedDisplayName",
                table: "Users");
        }
    }
}
