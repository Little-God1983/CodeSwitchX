using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeSwitchX.Data.Migrations
{
    /// <inheritdoc />
    public partial class SessionLastRequestFromUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The cursors are past the replies of the stored chats, so the usage kept is what tells their last request, to the
            // minute. A sub-agent's usage counts for its chat here: a one-off, a little warm, gone with the chat's next reply.
            migrationBuilder.Sql("""
                UPDATE "Sessions" SET "LastRequestAt" = (SELECT MAX(b."MinuteUtc") FROM "UsageBuckets" b WHERE b."SessionId" = "Sessions"."Id")
                WHERE EXISTS (SELECT 1 FROM "UsageBuckets" b WHERE b."SessionId" = "Sessions"."Id" AND b."MinuteUtc" > COALESCE("Sessions"."LastRequestAt", 0));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
