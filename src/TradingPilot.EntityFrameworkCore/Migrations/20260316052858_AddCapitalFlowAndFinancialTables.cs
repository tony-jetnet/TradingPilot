using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingPilot.Migrations
{
    /// <inheritdoc />
    public partial class AddCapitalFlowAndFinancialTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SymbolCapitalFlows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SymbolId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    SuperLargeInflow = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    SuperLargeOutflow = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    LargeInflow = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    LargeOutflow = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    MediumInflow = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    MediumOutflow = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    SmallInflow = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    SmallOutflow = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    CollectedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SymbolCapitalFlows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SymbolCapitalFlows_Symbols_SymbolId",
                        column: x => x.SymbolId,
                        principalTable: "Symbols",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SymbolFinancialSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SymbolId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Pe = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    ForwardPe = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    Eps = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    EstEps = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    MarketCap = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Volume = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    AvgVolume = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    High52w = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    Low52w = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    Beta = table.Column<decimal>(type: "numeric(8,4)", precision: 8, scale: 4, nullable: true),
                    DividendYield = table.Column<decimal>(type: "numeric(8,4)", precision: 8, scale: 4, nullable: true),
                    ShortFloat = table.Column<decimal>(type: "numeric(8,4)", precision: 8, scale: 4, nullable: true),
                    NextEarningsDate = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    RawJson = table.Column<string>(type: "jsonb", nullable: true),
                    CollectedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SymbolFinancialSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SymbolFinancialSnapshots_Symbols_SymbolId",
                        column: x => x.SymbolId,
                        principalTable: "Symbols",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SymbolCapitalFlows_SymbolId_Date",
                table: "SymbolCapitalFlows",
                columns: new[] { "SymbolId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SymbolFinancialSnapshots_SymbolId_Date",
                table: "SymbolFinancialSnapshots",
                columns: new[] { "SymbolId", "Date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SymbolCapitalFlows");

            migrationBuilder.DropTable(
                name: "SymbolFinancialSnapshots");
        }
    }
}
