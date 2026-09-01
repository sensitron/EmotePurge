using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EmotePurge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUsageStatBotUseCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NOT NULL DEFAULT 0 on the column itself, not just the CLR default: Postgres >= 11 adds
            // a NOT NULL DEFAULT <constant> column as a catalog-only change (no table rewrite of the
            // largest table), and the still-running old image's UNNEST upsert writes without this
            // column at all until it is redeployed — it needs the default to come from the column,
            // not from application code it does not yet have.
            migrationBuilder.AddColumn<int>(
                name: "BotUseCount",
                table: "UsageStats",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BotUseCount",
                table: "UsageStats");
        }
    }
}
