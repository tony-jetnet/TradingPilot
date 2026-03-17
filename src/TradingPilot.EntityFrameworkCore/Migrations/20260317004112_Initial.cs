using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingPilot.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Symbols",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    WebullTickerId = table.Column<long>(type: "bigint", nullable: false),
                    WebullExchangeId = table.Column<int>(type: "integer", nullable: true),
                    Exchange = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    SecurityType = table.Column<byte>(type: "smallint", nullable: false, defaultValue: (byte)1),
                    Sector = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Industry = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Status = table.Column<byte>(type: "smallint", nullable: false, defaultValue: (byte)1),
                    IsShortable = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    IsMarginable = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    IsWatched = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreationTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Symbols", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaperTrades",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SymbolId = table.Column<string>(type: "character varying(10)", nullable: false),
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

            migrationBuilder.CreateTable(
                name: "SymbolBars",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SymbolId = table.Column<string>(type: "character varying(10)", nullable: false),
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
                    SymbolId = table.Column<string>(type: "character varying(10)", nullable: false),
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

            migrationBuilder.CreateTable(
                name: "SymbolCapitalFlows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SymbolId = table.Column<string>(type: "character varying(10)", nullable: false),
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
                    SymbolId = table.Column<string>(type: "character varying(10)", nullable: false),
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

            migrationBuilder.CreateTable(
                name: "SymbolNews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SymbolId = table.Column<string>(type: "character varying(10)", nullable: false),
                    WebullNewsId = table.Column<long>(type: "bigint", nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SourceName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CollectedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SymbolNews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SymbolNews_Symbols_SymbolId",
                        column: x => x.SymbolId,
                        principalTable: "Symbols",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TickSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SymbolId = table.Column<string>(type: "character varying(10)", nullable: false),
                    TickerId = table.Column<long>(type: "bigint", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Price = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    Open = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    High = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    Low = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    Volume = table.Column<long>(type: "bigint", nullable: false),
                    Vwap = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    Ema9 = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    Ema20 = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    Rsi14 = table.Column<decimal>(type: "numeric(8,4)", precision: 8, scale: 4, nullable: false),
                    VolumeRatio = table.Column<decimal>(type: "numeric(8,4)", precision: 8, scale: 4, nullable: false),
                    UptickCount = table.Column<int>(type: "integer", nullable: false),
                    DowntickCount = table.Column<int>(type: "integer", nullable: false),
                    TickMomentum = table.Column<decimal>(type: "numeric(8,6)", precision: 8, scale: 6, nullable: false),
                    BookDepthRatio = table.Column<decimal>(type: "numeric(8,6)", precision: 8, scale: 6, nullable: false),
                    BidWallSize = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: false),
                    AskWallSize = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: false),
                    BidSweepCost = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    AskSweepCost = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    ImbalanceVelocity = table.Column<decimal>(type: "numeric(10,6)", precision: 10, scale: 6, nullable: false),
                    SpreadPercentile = table.Column<decimal>(type: "numeric(6,4)", precision: 6, scale: 4, nullable: false)
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

            migrationBuilder.CreateTable(
                name: "TradingSignals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SymbolId = table.Column<string>(type: "character varying(10)", nullable: false),
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
                name: "IX_SymbolCapitalFlows_SymbolId_Date",
                table: "SymbolCapitalFlows",
                columns: new[] { "SymbolId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SymbolFinancialSnapshots_SymbolId_Date",
                table: "SymbolFinancialSnapshots",
                columns: new[] { "SymbolId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SymbolNews_SymbolId_PublishedAt",
                table: "SymbolNews",
                columns: new[] { "SymbolId", "PublishedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SymbolNews_SymbolId_WebullNewsId",
                table: "SymbolNews",
                columns: new[] { "SymbolId", "WebullNewsId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Symbols_IsWatched",
                table: "Symbols",
                column: "IsWatched",
                filter: "\"IsWatched\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_Symbols_WebullTickerId",
                table: "Symbols",
                column: "WebullTickerId",
                unique: true);

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
                name: "PaperTrades");

            migrationBuilder.DropTable(
                name: "SymbolBars");

            migrationBuilder.DropTable(
                name: "SymbolBookSnapshots");

            migrationBuilder.DropTable(
                name: "SymbolCapitalFlows");

            migrationBuilder.DropTable(
                name: "SymbolFinancialSnapshots");

            migrationBuilder.DropTable(
                name: "SymbolNews");

            migrationBuilder.DropTable(
                name: "TickSnapshots");

            migrationBuilder.DropTable(
                name: "TradingSignals");

            migrationBuilder.DropTable(
                name: "Symbols");
        }
    }
}
