using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingPilot.Migrations
{
    /// <inheritdoc />
    public partial class AddTradingSignalsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TradingSignals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SymbolId = table.Column<Guid>(type: "uuid", nullable: false),
                    TickerId = table.Column<long>(type: "bigint", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Type = table.Column<byte>(type: "smallint", nullable: false),
                    Strength = table.Column<byte>(type: "smallint", nullable: false),
                    Price = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    Score = table.Column<decimal>(type: "numeric(8,6)", precision: 8, scale: 6, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ObiSmoothed = table.Column<decimal>(type: "numeric(8,6)", precision: 8, scale: 6, nullable: false),
                    Wobi = table.Column<decimal>(type: "numeric(8,6)", precision: 8, scale: 6, nullable: false),
                    PressureRoc = table.Column<decimal>(type: "numeric(8,6)", precision: 8, scale: 6, nullable: false),
                    SpreadSignal = table.Column<decimal>(type: "numeric(8,6)", precision: 8, scale: 6, nullable: false),
                    LargeOrderSignal = table.Column<decimal>(type: "numeric(8,6)", precision: 8, scale: 6, nullable: false),
                    Spread = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: false),
                    Imbalance = table.Column<decimal>(type: "numeric(8,6)", precision: 8, scale: 6, nullable: false),
                    BidLevels = table.Column<int>(type: "integer", nullable: false),
                    AskLevels = table.Column<int>(type: "integer", nullable: false),
                    PriceAfter1Min = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    PriceAfter5Min = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    PriceAfter15Min = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    PriceAfter30Min = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    WasCorrect1Min = table.Column<bool>(type: "boolean", nullable: true),
                    WasCorrect5Min = table.Column<bool>(type: "boolean", nullable: true),
                    WasCorrect15Min = table.Column<bool>(type: "boolean", nullable: true),
                    WasCorrect30Min = table.Column<bool>(type: "boolean", nullable: true),
                    VerifiedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradingSignals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TradingSignals_Symbols_SymbolId",
                        column: x => x.SymbolId,
                        principalTable: "Symbols",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TradingSignals_SymbolId_Timestamp",
                table: "TradingSignals",
                columns: new[] { "SymbolId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_TradingSignals_Timestamp",
                table: "TradingSignals",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_TradingSignals_Type_Strength_Timestamp",
                table: "TradingSignals",
                columns: new[] { "Type", "Strength", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_TradingSignals_VerifiedAt",
                table: "TradingSignals",
                column: "VerifiedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TradingSignals");
        }
    }
}
