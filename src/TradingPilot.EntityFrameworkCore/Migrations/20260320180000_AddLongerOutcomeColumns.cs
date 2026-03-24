using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingPilot.Migrations;

/// <summary>
/// Adds PriceAfter15Min, PriceAfter30Min, WasCorrect15Min, WasCorrect30Min
/// columns to TradingSignals for longer-horizon outcome verification.
/// </summary>
public partial class AddLongerOutcomeColumns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "TradingSignals" ADD COLUMN "PriceAfter15Min" numeric(12,4);
            ALTER TABLE "TradingSignals" ADD COLUMN "PriceAfter30Min" numeric(12,4);
            ALTER TABLE "TradingSignals" ADD COLUMN "WasCorrect15Min" boolean;
            ALTER TABLE "TradingSignals" ADD COLUMN "WasCorrect30Min" boolean;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "TradingSignals" DROP COLUMN "PriceAfter15Min";
            ALTER TABLE "TradingSignals" DROP COLUMN "PriceAfter30Min";
            ALTER TABLE "TradingSignals" DROP COLUMN "WasCorrect15Min";
            ALTER TABLE "TradingSignals" DROP COLUMN "WasCorrect30Min";
            """);
    }
}
