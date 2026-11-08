using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeSwitchX.Data.Migrations
{
    /// <inheritdoc />
    public partial class SessionWaitingAgents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WaitingAgents",
                table: "Sessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WaitingTurnEnded",
                table: "Sessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WaitingAgents",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "WaitingTurnEnded",
                table: "Sessions");
        }
    }
}
