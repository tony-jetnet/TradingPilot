using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingPilot.Migrations;

/// <summary>
/// Adds BrokerSymbolMappings table, seeds Webull ticker IDs,
/// and drops PaperTrades table (broker API is now sole source of truth).
/// </summary>
public partial class BrokerAbstraction : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // 1. Create BrokerSymbolMappings table
        migrationBuilder.CreateTable(
            name: "BrokerSymbolMappings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                SymbolId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                BrokerName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                BrokerSymbolId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
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
            name: "IX_BrokerSymbolMappings_Symbol_Broker",
            table: "BrokerSymbolMappings",
            columns: new[] { "SymbolId", "BrokerName" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_BrokerSymbolMappings_Broker_BrokerId",
            table: "BrokerSymbolMappings",
            columns: new[] { "BrokerName", "BrokerSymbolId" });

        // 2. Seed Webull symbol mappings from existing data
        var mappings = new (string Symbol, string TickerId)[]
        {
            ("AMD", "913254235"),
            ("LLY", "913255489"),
            ("NVDA", "913257561"),
            ("PLTR", "950172475"),
            ("RIVN", "950188536"),
            ("RKLB", "950178054"),
            ("SMCI", "913257435"),
            ("SMR", "950178709"),
            ("SOFI", "950178653"),
            ("TSLA", "913255598"),
        };

        foreach (var (symbol, tickerId) in mappings)
        {
            migrationBuilder.InsertData(
                table: "BrokerSymbolMappings",
                columns: new[] { "Id", "SymbolId", "BrokerName", "BrokerSymbolId" },
                values: new object[] { Guid.NewGuid(), symbol, "WebullPaper", tickerId });
        }

        // 3. Drop PaperTrades table (broker API is sole source of truth)
        migrationBuilder.DropTable(name: "PaperTrades");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Recreate PaperTrades table
        migrationBuilder.CreateTable(
            name: "PaperTrades",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                SymbolId = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                TickerId = table.Column<long>(type: "bigint", nullable: false),
                Timestamp = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                Action = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                Quantity = table.Column<int>(type: "integer", nullable: false),
                SignalPrice = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                FilledPrice = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                Score = table.Column<decimal>(type: "numeric(8,6)", precision: 8, scale: 6, nullable: false),
                Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                WebullOrderId = table.Column<long>(type: "bigint", nullable: true),
                OrderStatus = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                SignalId = table.Column<Guid>(type: "uuid", nullable: true),
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

        migrationBuilder.CreateIndex(name: "IX_PaperTrades_SymbolId_Timestamp", table: "PaperTrades", columns: new[] { "SymbolId", "Timestamp" });
        migrationBuilder.CreateIndex(name: "IX_PaperTrades_Timestamp", table: "PaperTrades", column: "Timestamp");
        migrationBuilder.CreateIndex(name: "IX_PaperTrades_WebullOrderId", table: "PaperTrades", column: "WebullOrderId");

        // Drop BrokerSymbolMappings
        migrationBuilder.DropTable(name: "BrokerSymbolMappings");
    }
}
