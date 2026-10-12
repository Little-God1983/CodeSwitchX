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
