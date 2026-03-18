using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingPilot.Migrations
{
    /// <inheritdoc />
    public partial class DropTickSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TickSnapshots");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TickSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AskSweepCost = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    AskWallSize = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: false),
                    BidSweepCost = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    BidWallSize = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: false),
                    BookDepthRatio = table.Column<decimal>(type: "numeric(8,6)", precision: 8, scale: 6, nullable: false),
                    DowntickCount = table.Column<int>(type: "integer", nullable: false),
                    Ema20 = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    Ema9 = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    High = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    ImbalanceVelocity = table.Column<decimal>(type: "numeric(10,6)", precision: 10, scale: 6, nullable: false),
                    Low = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    Open = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    Price = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    Rsi14 = table.Column<decimal>(type: "numeric(8,4)", precision: 8, scale: 4, nullable: false),
                    SpreadPercentile = table.Column<decimal>(type: "numeric(6,4)", precision: 6, scale: 4, nullable: false),
                    SymbolId = table.Column<string>(type: "character varying(10)", nullable: false),
                    TickMomentum = table.Column<decimal>(type: "numeric(8,6)", precision: 8, scale: 6, nullable: false),
                    TickerId = table.Column<long>(type: "bigint", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UptickCount = table.Column<int>(type: "integer", nullable: false),
                    Volume = table.Column<long>(type: "bigint", nullable: false),
                    VolumeRatio = table.Column<decimal>(type: "numeric(8,4)", precision: 8, scale: 4, nullable: false),
                    Vwap = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TickSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TickSnapshots_Symbols_SymbolId",
                        column: x => x.SymbolId,
                        principalTable: "Symbols",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TickSnapshots_SymbolId_Timestamp",
                table: "TickSnapshots",
                columns: new[] { "SymbolId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_TickSnapshots_TickerId_Timestamp",
                table: "TickSnapshots",
                columns: new[] { "TickerId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_TickSnapshots_Timestamp",
                table: "TickSnapshots",
                column: "Timestamp");
        }
    }
}
