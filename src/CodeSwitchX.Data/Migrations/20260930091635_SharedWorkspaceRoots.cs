using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeSwitchX.Data.Migrations
{
    /// <inheritdoc />
    public partial class SharedWorkspaceRoots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Workspaces_RootPath",
                table: "Workspaces");

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_RootPath",
                table: "Workspaces",
                column: "RootPath");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The index stays non-unique: once two workspaces share a root, a unique one cannot be created again, and
            // the builds before this migration work with either.
        }
    }
}
