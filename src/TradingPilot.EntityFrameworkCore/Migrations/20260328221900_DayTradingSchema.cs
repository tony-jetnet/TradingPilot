using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingPilot.Migrations
{
    /// <inheritdoc />
    public partial class DayTradingSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CapitalFlowScore",
                table: "TradingSignals",
                type: "numeric(8,6)",
                precision: 8,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ContextScore",
                table: "TradingSignals",
                type: "numeric(8,6)",
                precision: 8,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Ema20_15m",
                table: "TradingSignals",
                type: "numeric(12,4)",
                precision: 12,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Ema20_5m",
                table: "TradingSignals",
                type: "numeric(12,4)",
                precision: 12,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Ema50",
                table: "TradingSignals",
                type: "numeric(12,4)",
                precision: 12,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Ema50_15m",
                table: "TradingSignals",
                type: "numeric(12,4)",
                precision: 12,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Ema50_5m",
                table: "TradingSignals",
                type: "numeric(12,4)",
                precision: 12,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasCatalyst",
                table: "TradingSignals",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NewsCount2Hr",
                table: "TradingSignals",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "NewsSentiment",
                table: "TradingSignals",
                type: "numeric(6,4)",
                precision: 6,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PriceAfter1Hr",
                table: "TradingSignals",
                type: "numeric(12,4)",
                precision: 12,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PriceAfter2Hr",
                table: "TradingSignals",
                type: "numeric(12,4)",
                precision: 12,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PriceAfter4Hr",
                table: "TradingSignals",
                type: "numeric(12,4)",
                precision: 12,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RelativeVolume",
                table: "TradingSignals",
                type: "numeric(8,4)",
                precision: 8,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Rsi14_15m",
                table: "TradingSignals",
                type: "numeric(8,4)",
                precision: 8,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Rsi14_5m",
                table: "TradingSignals",
                type: "numeric(8,4)",
                precision: 8,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SetupScore",
                table: "TradingSignals",
                type: "numeric(8,6)",
                precision: 8,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SignalCatalystType",
                table: "TradingSignals",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "SignalSetupType",
                table: "TradingSignals",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "Source",
                table: "TradingSignals",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TimingScore",
                table: "TradingSignals",
                type: "numeric(8,6)",
                precision: 8,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TrendDirection_15m",
                table: "TradingSignals",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TrendDirection_5m",
                table: "TradingSignals",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TrendStrength",
                table: "TradingSignals",
                type: "numeric(8,6)",
                precision: 8,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "VwapDeviation",
                table: "TradingSignals",
                type: "numeric(8,6)",
                precision: 8,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WasCorrect1Hr",
                table: "TradingSignals",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WasCorrect2Hr",
                table: "TradingSignals",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WasCorrect4Hr",
                table: "TradingSignals",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsActiveForTrading",
                table: "Symbols",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CatalystType",
                table: "SymbolNews",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ScoredAt",
                table: "SymbolNews",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SentimentMethod",
                table: "SymbolNews",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SentimentScore",
                table: "SymbolNews",
                type: "numeric(6,4)",
                precision: 6,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "DayTradeSetupType",
                table: "CompletedTrades",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "DayTradeSource",
                table: "CompletedTrades",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HoldSeconds",
                table: "CompletedTrades",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SetupInvalidated",
                table: "CompletedTrades",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SetupScore",
                table: "CompletedTrades",
                type: "numeric(8,6)",
                precision: 8,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "StopDistance",
                table: "CompletedTrades",
                type: "numeric(12,4)",
                precision: 12,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TimingScore",
                table: "CompletedTrades",
                type: "numeric(8,6)",
                precision: 8,
                scale: 6,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BarSetups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SymbolId = table.Column<string>(type: "character varying(10)", nullable: false),
                    TickerId = table.Column<long>(type: "bigint", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    SetupType = table.Column<byte>(type: "smallint", nullable: false),
                    Direction = table.Column<byte>(type: "smallint", nullable: false),
                    Strength = table.Column<decimal>(type: "numeric(8,6)", precision: 8, scale: 6, nullable: false),
                    EntryZoneLow = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    EntryZoneHigh = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    StopLevel = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    TargetLevel = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Price = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    Ema9 = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    Ema20 = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    Rsi14 = table.Column<decimal>(type: "numeric(8,4)", precision: 8, scale: 4, nullable: false),
                    Vwap = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    Atr14 = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: false),
                    VolumeRatio = table.Column<decimal>(type: "numeric(8,4)", precision: 8, scale: 4, nullable: false),
                    TrendDirection = table.Column<int>(type: "integer", nullable: false),
                    Ema20_5m = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    Ema50_5m = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    Rsi14_5m = table.Column<decimal>(type: "numeric(8,4)", precision: 8, scale: 4, nullable: false),
                    Atr14_5m = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: false),
                    TrendDirection_5m = table.Column<int>(type: "integer", nullable: false),
                    Ema20_15m = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    Ema50_15m = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    Rsi14_15m = table.Column<decimal>(type: "numeric(8,4)", precision: 8, scale: 4, nullable: false),
                    TrendDirection_15m = table.Column<int>(type: "integer", nullable: false),
                    CapitalFlowScore = table.Column<decimal>(type: "numeric(8,6)", precision: 8, scale: 6, nullable: true),
                    NewsSentiment = table.Column<decimal>(type: "numeric(6,4)", precision: 6, scale: 4, nullable: true),
                    HasCatalyst = table.Column<bool>(type: "boolean", nullable: false),
                    CatalystType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    NewsCount2Hr = table.Column<int>(type: "integer", nullable: false),
                    PriceAfter1Hr = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    PriceAfter2Hr = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    PriceAfter4Hr = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    MaxFavorable = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    MaxAdverse = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    WasCorrect1Hr = table.Column<bool>(type: "boolean", nullable: true),
                    WasCorrect2Hr = table.Column<bool>(type: "boolean", nullable: true),
                    WasCorrect4Hr = table.Column<bool>(type: "boolean", nullable: true),
                    WasTradeable = table.Column<bool>(type: "boolean", nullable: true),
                    VerifiedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BarSetups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BarSetups_Symbols_SymbolId",
                        column: x => x.SymbolId,
                        principalTable: "Symbols",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DailyWatchlists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Selections = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyWatchlists", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BarSetups_SetupType_Direction",
                table: "BarSetups",
                columns: new[] { "SetupType", "Direction" });

            migrationBuilder.CreateIndex(
                name: "IX_BarSetups_SymbolId_Timestamp",
                table: "BarSetups",
                columns: new[] { "SymbolId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_DailyWatchlists_Date",
                table: "DailyWatchlists",
                column: "Date",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BarSetups");

            migrationBuilder.DropTable(
                name: "DailyWatchlists");

            migrationBuilder.DropColumn(
                name: "CapitalFlowScore",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "ContextScore",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "Ema20_15m",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "Ema20_5m",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "Ema50",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "Ema50_15m",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "Ema50_5m",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "HasCatalyst",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "NewsCount2Hr",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "NewsSentiment",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "PriceAfter1Hr",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "PriceAfter2Hr",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "PriceAfter4Hr",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "RelativeVolume",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "Rsi14_15m",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "Rsi14_5m",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "SetupScore",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "SignalCatalystType",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "SignalSetupType",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "TimingScore",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "TrendDirection_15m",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "TrendDirection_5m",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "TrendStrength",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "VwapDeviation",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "WasCorrect1Hr",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "WasCorrect2Hr",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "WasCorrect4Hr",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "IsActiveForTrading",
                table: "Symbols");

            migrationBuilder.DropColumn(
                name: "CatalystType",
                table: "SymbolNews");

            migrationBuilder.DropColumn(
                name: "ScoredAt",
                table: "SymbolNews");

            migrationBuilder.DropColumn(
                name: "SentimentMethod",
                table: "SymbolNews");

            migrationBuilder.DropColumn(
                name: "SentimentScore",
                table: "SymbolNews");

            migrationBuilder.DropColumn(
                name: "DayTradeSetupType",
                table: "CompletedTrades");

            migrationBuilder.DropColumn(
                name: "DayTradeSource",
                table: "CompletedTrades");

            migrationBuilder.DropColumn(
                name: "HoldSeconds",
                table: "CompletedTrades");

            migrationBuilder.DropColumn(
                name: "SetupInvalidated",
                table: "CompletedTrades");

            migrationBuilder.DropColumn(
                name: "SetupScore",
                table: "CompletedTrades");

            migrationBuilder.DropColumn(
                name: "StopDistance",
                table: "CompletedTrades");

            migrationBuilder.DropColumn(
                name: "TimingScore",
                table: "CompletedTrades");
        }
    }
}
