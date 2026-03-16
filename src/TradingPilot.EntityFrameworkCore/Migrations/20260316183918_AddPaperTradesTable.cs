using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingPilot.Migrations
{
    /// <inheritdoc />
    public partial class AddPaperTradesTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PaperTrades",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SymbolId = table.Column<Guid>(type: "uuid", nullable: false),
                    TickerId = table.Column<long>(type: "bigint", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Action = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    SignalPrice = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    FilledPrice = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    Score = table.Column<decimal>(type: "numeric(8,6)", precision: 8, scale: 6, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    WebullOrderId = table.Column<long>(type: "bigint", nullable: true),
                    OrderStatus = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SignalId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaperTrades", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaperTrades_Symbols_SymbolId",
                        column: x => x.SymbolId,
                        principalTable: "Symbols",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaperTrades_SymbolId_Timestamp",
                table: "PaperTrades",
                columns: new[] { "SymbolId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_PaperTrades_Timestamp",
                table: "PaperTrades",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_PaperTrades_WebullOrderId",
                table: "PaperTrades",
                column: "WebullOrderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaperTrades");
        }
    }
}
