using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingPilot.Migrations
{
    /// <inheritdoc />
    public partial class AddIndicatorsToTradingSignals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AskSweepCost",
                table: "TradingSignals",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "AskWallSize",
                table: "TradingSignals",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "BidSweepCost",
                table: "TradingSignals",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "BidWallSize",
                table: "TradingSignals",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "BookDepthRatio",
                table: "TradingSignals",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "Ema20",
                table: "TradingSignals",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "Ema9",
                table: "TradingSignals",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ImbalanceVelocity",
                table: "TradingSignals",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "Rsi14",
                table: "TradingSignals",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "SpreadPercentile",
                table: "TradingSignals",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TickMomentum",
                table: "TradingSignals",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "VolumeRatio",
                table: "TradingSignals",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "Vwap",
                table: "TradingSignals",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AskSweepCost",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "AskWallSize",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "BidSweepCost",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "BidWallSize",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "BookDepthRatio",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "Ema20",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "Ema9",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "ImbalanceVelocity",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "Rsi14",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "SpreadPercentile",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "TickMomentum",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "VolumeRatio",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "Vwap",
                table: "TradingSignals");
        }
    }
}
