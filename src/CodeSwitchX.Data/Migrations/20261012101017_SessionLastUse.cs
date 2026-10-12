using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeSwitchX.Data.Migrations
{
    /// <inheritdoc />
    public partial class SessionLastUse : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LastUseAt",
                table: "Sessions",
                type: "INTEGER",
                nullable: true);

            // The chats stored so far are given their last use once, as the Yard read it at startup before (#276): by the
            // hook events kept that use a chat (a prompt, a tool, a question; not its start, a turn's end or its end). One
            // none of whose events are kept (older than their retention, or one only its transcript told of) by its last
            // event. From here on the engine stamps it, and the column is all the startup read goes by.
            migrationBuilder.Sql("""
                UPDATE "Sessions" SET "LastUseAt" = CASE
                    WHEN EXISTS (SELECT 1 FROM "SessionEvents" e WHERE e."SessionId" = "Sessions"."Id")
                        THEN (SELECT MAX(e."At") FROM "SessionEvents" e WHERE e."SessionId" = "Sessions"."Id"
                              AND e."Kind" IN ('UserPromptSubmit', 'PreToolUse', 'PostToolUse', 'PermissionRequest'))
                    ELSE "LastEventAt"
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastUseAt",
                table: "Sessions");
        }
    }
}
