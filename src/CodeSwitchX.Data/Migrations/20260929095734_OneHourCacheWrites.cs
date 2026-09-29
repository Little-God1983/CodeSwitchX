using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeSwitchX.Data.Migrations
{
    /// <inheritdoc />
    public partial class OneHourCacheWrites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CacheWrite1h",
                table: "UsageBuckets",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ContextCacheWrite1h",
                table: "Sessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<double>(
                name: "CacheWrite1hPerM",
                table: "PricingRules",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CacheWrite1h",
                table: "UsageBuckets");

            migrationBuilder.DropColumn(
                name: "ContextCacheWrite1h",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "CacheWrite1hPerM",
                table: "PricingRules");
        }
    }
}
