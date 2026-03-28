using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingPilot.Migrations
{
    /// <inheritdoc />
    public partial class AddCompletedTradesTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // PriceAfter15Min, PriceAfter30Min, WasCorrect15Min, WasCorrect30Min
            // already exist in DB from prior manual additions — skip AddColumn calls.

            migrationBuilder.CreateTable(
                name: "CompletedTrades",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Ticker = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TickerId = table.Column<long>(type: "bigint", nullable: false),
                    IsLong = table.Column<bool>(type: "boolean", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    EntryPrice = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    ExitPrice = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    EntryTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ExitTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Pnl = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    EntrySource = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EntryScore = table.Column<decimal>(type: "numeric(8,6)", precision: 8, scale: 6, nullable: false),
                    ExitReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompletedTrades", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompletedTrades_ExitTime",
                table: "CompletedTrades",
                column: "ExitTime");

            migrationBuilder.CreateIndex(
                name: "IX_CompletedTrades_Ticker_ExitTime",
                table: "CompletedTrades",
                columns: new[] { "Ticker", "ExitTime" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompletedTrades");
        }
    }
}
