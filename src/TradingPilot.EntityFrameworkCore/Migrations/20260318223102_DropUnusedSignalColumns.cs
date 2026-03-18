using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingPilot.Migrations
{
    /// <inheritdoc />
    public partial class DropUnusedSignalColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaperTrades");

            migrationBuilder.DropColumn(
                name: "PriceAfter15Min",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "PriceAfter30Min",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "WasCorrect15Min",
                table: "TradingSignals");

            migrationBuilder.DropColumn(
                name: "WasCorrect30Min",
                table: "TradingSignals");

            migrationBuilder.CreateTable(
                name: "BrokerSymbolMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SymbolId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    BrokerName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    BrokerSymbolId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrokerSymbolMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BrokerSymbolMappings_Symbols_SymbolId",
                        column: x => x.SymbolId,
                        principalTable: "Symbols",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BrokerSymbolMappings_Broker_BrokerId",
                table: "BrokerSymbolMappings",
                columns: new[] { "BrokerName", "BrokerSymbolId" });

            migrationBuilder.CreateIndex(
                name: "IX_BrokerSymbolMappings_Symbol_Broker",
                table: "BrokerSymbolMappings",
                columns: new[] { "SymbolId", "BrokerName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BrokerSymbolMappings");

            migrationBuilder.AddColumn<decimal>(
                name: "PriceAfter15Min",
                table: "TradingSignals",
                type: "numeric(12,4)",
                precision: 12,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PriceAfter30Min",
                table: "TradingSignals",
                type: "numeric(12,4)",
                precision: 12,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WasCorrect15Min",
                table: "TradingSignals",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WasCorrect30Min",
                table: "TradingSignals",
                type: "boolean",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PaperTrades",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    FilledPrice = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    OrderStatus = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Score = table.Column<decimal>(type: "numeric(8,6)", precision: 8, scale: 6, nullable: false),
                    SignalId = table.Column<Guid>(type: "uuid", nullable: true),
                    SignalPrice = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    SymbolId = table.Column<string>(type: "character varying(10)", nullable: false),
                    TickerId = table.Column<long>(type: "bigint", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    WebullOrderId = table.Column<long>(type: "bigint", nullable: true)
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
    }
}
