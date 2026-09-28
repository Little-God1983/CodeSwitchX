using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeSwitchX.Data.Migrations
{
    /// <inheritdoc />
    public partial class CursorTitles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "TranscriptCursors",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TitleSource",
                table: "TranscriptCursors",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // The indexer of the versions before this took the first prompt as the title without storing it (and a stored
            // cursor covers lines, so a prompt at least): a later prompt must not rename the chat, while a generated title
            // or a /rename name still does.
            migrationBuilder.Sql("UPDATE \"TranscriptCursors\" SET \"TitleSource\" = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Title",
                table: "TranscriptCursors");

            migrationBuilder.DropColumn(
                name: "TitleSource",
                table: "TranscriptCursors");
        }
    }
}
