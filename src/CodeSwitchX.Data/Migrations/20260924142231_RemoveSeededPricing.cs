using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeSwitchX.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveSeededPricing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Earlier versions copied the shipped prices into this table on every start. A stored rule overrides the shipped
            // one for its model, so a corrected price never reached them. Nothing has written a rule of the user's own yet
            // (UpsertPricingAsync has no caller), so every row is such a copy.
            migrationBuilder.Sql("DELETE FROM \"PricingRules\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The copies are not restored: the shipped prices are in code (DefaultPricing).
        }
    }
}
