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
                nullable: false,
                defaultValue: 0.0);

            // A rule stored before this column existed priced every cache write at its 5-minute rate; at Anthropic's list
            // prices a 1-hour write is 2x input. Nothing has written a rule of the user's own yet, so this sets the rate the
            // shipped rules carry rather than leaving such writes free.
            migrationBuilder.Sql("UPDATE \"PricingRules\" SET \"CacheWrite1hPerM\" = \"InputPerM\" * 2;");
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
