using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeSwitchX.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkspaceNumbers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Number",
                table: "Workspaces",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // Numbers the workspaces stored so far once, in the order the Yard shows them (by track, then by name), before
            // the unique index that would refuse their shared 0.
            migrationBuilder.Sql("""
                UPDATE "Workspaces" SET "Number" = (
                    SELECT r."N" FROM (
                        SELECT w."Id" AS "Id",
                               ROW_NUMBER() OVER (ORDER BY t."SortOrder", t."Name" COLLATE NOCASE, w."Name" COLLATE NOCASE, w."Id") AS "N"
                        FROM "Workspaces" w JOIN "Tracks" t ON t."Id" = w."TrackId"
                    ) r
                    WHERE r."Id" = "Workspaces"."Id");
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_Number",
                table: "Workspaces",
                column: "Number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Workspaces_Number",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "Number",
                table: "Workspaces");
        }
    }
}
