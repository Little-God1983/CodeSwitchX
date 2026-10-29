using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeSwitchX.Data.Migrations
{
    /// <inheritdoc />
    public partial class CursorContextBase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ContextBase",
                table: "TranscriptCursors",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            // A cursor stored before this read replies without keeping the smallest, so the next reply read after the upgrade
            // can be far bigger than Claude Code's own part. -1 is that mark: the indexer learns the part from the first reply
            // after a compact instead, and until then a compact counts what it kept alone.
            migrationBuilder.Sql("UPDATE \"TranscriptCursors\" SET \"ContextBase\" = -1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContextBase",
                table: "TranscriptCursors");
        }
    }
}
