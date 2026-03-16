using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingPilot.Migrations
{
    /// <inheritdoc />
    public partial class AddSymbolTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Symbols",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Ticker = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    WebullTickerId = table.Column<long>(type: "bigint", nullable: false),
                    WebullExchangeId = table.Column<int>(type: "integer", nullable: true),
                    Exchange = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    SecurityType = table.Column<byte>(type: "smallint", nullable: false, defaultValue: (byte)1),
                    Sector = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Industry = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Status = table.Column<byte>(type: "smallint", nullable: false, defaultValue: (byte)1),
                    ListDate = table.Column<DateOnly>(type: "date", nullable: true),
                    IsShortable = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    IsMarginable = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    IsWatched = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreationTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    ExtraProperties = table.Column<string>(type: "text", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Symbols", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SymbolBars",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SymbolId = table.Column<Guid>(type: "uuid", nullable: false),
                    Timeframe = table.Column<byte>(type: "smallint", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Open = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    High = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    Low = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    Close = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    Volume = table.Column<long>(type: "bigint", nullable: false),
                    Vwap = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    ChangeRatio = table.Column<decimal>(type: "numeric(8,6)", precision: 8, scale: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SymbolBars", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SymbolBars_Symbols_SymbolId",
                        column: x => x.SymbolId,
                        principalTable: "Symbols",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SymbolBookSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SymbolId = table.Column<Guid>(type: "uuid", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    BidPrices = table.Column<string>(type: "jsonb", nullable: false),
                    BidSizes = table.Column<string>(type: "jsonb", nullable: false),
                    AskPrices = table.Column<string>(type: "jsonb", nullable: false),
                    AskSizes = table.Column<string>(type: "jsonb", nullable: false),
                    Spread = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: false),
                    MidPrice = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    Imbalance = table.Column<decimal>(type: "numeric(6,4)", precision: 6, scale: 4, nullable: false),
                    Depth = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SymbolBookSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SymbolBookSnapshots_Symbols_SymbolId",
                        column: x => x.SymbolId,
                        principalTable: "Symbols",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SymbolBars_SymbolId_Timeframe_Timestamp",
                table: "SymbolBars",
                columns: new[] { "SymbolId", "Timeframe", "Timestamp" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SymbolBars_Timestamp_Timeframe",
                table: "SymbolBars",
                columns: new[] { "Timestamp", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_SymbolBookSnapshots_SymbolId_Timestamp",
                table: "SymbolBookSnapshots",
                columns: new[] { "SymbolId", "Timestamp" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SymbolBookSnapshots_Timestamp",
                table: "SymbolBookSnapshots",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_Symbols_IsWatched",
                table: "Symbols",
                column: "IsWatched",
                filter: "\"IsWatched\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_Symbols_Ticker",
                table: "Symbols",
                column: "Ticker",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Symbols_WebullTickerId",
                table: "Symbols",
                column: "WebullTickerId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SymbolBars");

            migrationBuilder.DropTable(
                name: "SymbolBookSnapshots");

            migrationBuilder.DropTable(
                name: "Symbols");
        }
    }
}
