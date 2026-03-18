using System.Text;
using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingPilot.EntityFrameworkCore;
using TradingPilot.Trading;

namespace TradingPilot.Trading;

/// <summary>
/// Nightly AI-powered strategy optimization using AWS Bedrock Sonnet 4.6.
/// Replaces NightlyModelTrainer's hill-climbing approach with LLM pattern discovery.
///
/// Two-stage architecture:
///   Stage 1: Pre-compute rich features (L2-derived) stored in TickSnapshots (done in real-time)
///   Stage 2: Query DB for per-symbol stats, call Bedrock once per symbol, output strategy_rules.json
/// </summary>
[DisableConcurrentExecution(600)]
[AutomaticRetry(Attempts = 0)]
public class NightlyStrategyOptimizer
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NightlyStrategyOptimizer> _logger;

    private const string StrategyConfigPath = @"D:\Third-Parties\WebullHook\strategy_rules.json";

    public NightlyStrategyOptimizer(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<NightlyStrategyOptimizer> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task OptimizeAsync(int lookbackDays = 20)
    {
        _logger.LogWarning("=== NIGHTLY STRATEGY OPTIMIZATION START (lookback={LookbackDays}d) ===", lookbackDays);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // 1. Get all active symbols
            var symbols = await GetActiveSymbolsAsync();
            if (symbols.Count == 0)
            {
                _logger.LogWarning("No active symbols found. Skipping optimization.");
                return;
            }

            _logger.LogWarning("Optimizing {Count} symbols: {Symbols}",
                symbols.Count, string.Join(", ", symbols.Select(s => s.Ticker)));

            // 2. Backfill any missing TradingSignals from bar data
            //    This fills gaps from periods where MQTT/app was down during the day.
            //    Safe to run here — market is closed, no race with live data.
            foreach (var symbol in symbols)
            {
                try
                {
                    await BackfillTradingSignalsFromBarsAsync(symbol.TickerId, symbol.Ticker, symbol.Ticker, lookbackDays);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Backfill failed for {Ticker}", symbol.Ticker);
                }
            }

            // 3. For each symbol, prepare analysis and call Bedrock
            var config = new StrategyConfig
            {
                GeneratedAt = DateTime.UtcNow,
                LookbackDays = lookbackDays,
            };

            foreach (var symbol in symbols)
            {
                try
                {
                    var data = await PrepareSymbolDataAsync(symbol.TickerId, symbol.Ticker, symbol.Ticker, lookbackDays);
                    if (data == null)
                    {
                        _logger.LogWarning("Insufficient data for {Ticker}, skipping", symbol.Ticker);
                        continue;
                    }

                    var symbolStrategy = await CallBedrockAsync(symbol.TickerId, symbol.Ticker, data);
                    if (symbolStrategy != null)
                    {
                        config.Symbols[symbol.Ticker] = symbolStrategy;
                        _logger.LogWarning("Generated {RuleCount} rules for {Ticker}",
                            symbolStrategy.Rules.Count, symbol.Ticker);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to optimize {Ticker}", symbol.Ticker);
                }
            }

            if (config.Symbols.Count == 0)
            {
                _logger.LogWarning("No symbols produced valid rules. Skipping file output.");
                return;
            }

            // 3. Save strategy_rules.json
            await SaveStrategyRulesAsync(config);

            sw.Stop();
            _logger.LogWarning(
                "=== NIGHTLY STRATEGY OPTIMIZATION COMPLETE in {Elapsed}s — {SymbolCount} symbols, {TotalRules} rules ===",
                sw.Elapsed.TotalSeconds, config.Symbols.Count,
                config.Symbols.Values.Sum(s => s.Rules.Count));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nightly strategy optimization failed");
            throw;
        }
    }

    #region Symbol Analysis Preparation

    /// <summary>
    private record SymbolData(List<string> Rows, string ContextSection, string CsvHeader, int TotalWins);

    /// <summary>
    /// Load raw trade data + context for Bedrock. Returns rows (CSV), context, and header separately
    /// so CallBedrockAsync can chunk the rows into multiple API calls.
    /// </summary>
    private async Task<SymbolData?> PrepareSymbolDataAsync(long tickerId, string ticker, string symbolId, int lookbackDays)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TradingPilotDbContext>();

        var cutoff = DateTime.UtcNow.AddDays(-lookbackDays);

        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync();

        try
        {
        // ═══════════════════════════════════════════════════════════
        // 1. Raw trade data: TradingSignals JOIN TickSnapshots
        //    Every signal with all indicator values + outcome
        // ═══════════════════════════════════════════════════════════
        // Raw trade data + L2 order book shape (top 5 bid/ask sizes from nearest snapshot)
        var rawDataSql = @"
            SELECT
                EXTRACT(HOUR FROM (ts.""Timestamp"" AT TIME ZONE 'UTC') AT TIME ZONE 'America/New_York')::int AS hour_et,
                CASE ts.""Type"" WHEN 1 THEN 'BUY' ELSE 'SELL' END AS direction,
                ROUND(ts.""Price""::numeric, 2) AS price,
                ROUND(ts.""Score""::numeric, 4) AS score,
                ROUND(ts.""ObiSmoothed""::numeric, 4) AS obi,
                ROUND(ts.""Wobi""::numeric, 4) AS wobi,
                ROUND(ts.""PressureRoc""::numeric, 4) AS pressure_roc,
                ROUND(ts.""SpreadSignal""::numeric, 4) AS spread_signal,
                ROUND(ts.""LargeOrderSignal""::numeric, 4) AS large_order,
                ROUND(ts.""Spread""::numeric, 6) AS spread,
                ROUND(ts.""Imbalance""::numeric, 4) AS imbalance,
                ROUND(ts.""Ema9""::numeric, 2) AS ema9,
                ROUND(ts.""Ema20""::numeric, 2) AS ema20,
                ROUND(ts.""Rsi14""::numeric, 1) AS rsi14,
                ROUND(ts.""Vwap""::numeric, 2) AS vwap,
                ROUND(ts.""VolumeRatio""::numeric, 2) AS vol_ratio,
                ROUND(ts.""TickMomentum""::numeric, 4) AS tick_mom,
                ROUND(ts.""BookDepthRatio""::numeric, 4) AS book_depth,
                ROUND(ts.""BidWallSize""::numeric, 2) AS bid_wall,
                ROUND(ts.""AskWallSize""::numeric, 2) AS ask_wall,
                ROUND(ts.""BidSweepCost""::numeric, 0) AS bid_sweep,
                ROUND(ts.""AskSweepCost""::numeric, 0) AS ask_sweep,
                ROUND(ts.""ImbalanceVelocity""::numeric, 6) AS imb_vel,
                ROUND(ts.""SpreadPercentile""::numeric, 4) AS spread_pctl,
                -- L2 order book shape: top 5 bid/ask sizes at signal time
                COALESCE(l2.b1, 0) AS b1, COALESCE(l2.b2, 0) AS b2, COALESCE(l2.b3, 0) AS b3,
                COALESCE(l2.b4, 0) AS b4, COALESCE(l2.b5, 0) AS b5,
                COALESCE(l2.a1, 0) AS a1, COALESCE(l2.a2, 0) AS a2, COALESCE(l2.a3, 0) AS a3,
                COALESCE(l2.a4, 0) AS a4, COALESCE(l2.a5, 0) AS a5,
                -- Outcomes
                ROUND(ts.""PriceAfter1Min""::numeric, 4) AS price_1m,
                ROUND(ts.""PriceAfter5Min""::numeric, 4) AS price_5m,
                CASE WHEN ts.""Type"" = 1 AND ts.""PriceAfter1Min"" > ts.""Price"" THEN 1
                     WHEN ts.""Type"" = 2 AND ts.""PriceAfter1Min"" < ts.""Price"" THEN 1
                     ELSE 0 END AS win_1m
            FROM ""TradingSignals"" ts
            LEFT JOIN LATERAL (
                SELECT
                    ROUND((bs.""BidSizes""::jsonb->>0)::numeric, 0) AS b1,
                    ROUND((bs.""BidSizes""::jsonb->>1)::numeric, 0) AS b2,
                    ROUND((bs.""BidSizes""::jsonb->>2)::numeric, 0) AS b3,
                    ROUND((bs.""BidSizes""::jsonb->>3)::numeric, 0) AS b4,
                    ROUND((bs.""BidSizes""::jsonb->>4)::numeric, 0) AS b5,
                    ROUND((bs.""AskSizes""::jsonb->>0)::numeric, 0) AS a1,
                    ROUND((bs.""AskSizes""::jsonb->>1)::numeric, 0) AS a2,
                    ROUND((bs.""AskSizes""::jsonb->>2)::numeric, 0) AS a3,
                    ROUND((bs.""AskSizes""::jsonb->>3)::numeric, 0) AS a4,
                    ROUND((bs.""AskSizes""::jsonb->>4)::numeric, 0) AS a5
                FROM ""SymbolBookSnapshots"" bs
                WHERE bs.""SymbolId"" = ts.""SymbolId""
                  AND bs.""Timestamp"" BETWEEN ts.""Timestamp"" - INTERVAL '3 seconds' AND ts.""Timestamp"" + INTERVAL '3 seconds'
                ORDER BY ABS(EXTRACT(EPOCH FROM bs.""Timestamp"" - ts.""Timestamp""))
                LIMIT 1
            ) l2 ON true
            WHERE ts.""SymbolId"" = @p0 AND ts.""Timestamp"" > @p1 AND ts.""PriceAfter1Min"" IS NOT NULL
            ORDER BY ts.""Timestamp"" DESC
            LIMIT 3000";

        var rows = new List<string>();
        int totalRows = 0, totalWins = 0;

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = rawDataSql;
            AddParameter(cmd, "@p0", symbolId);
            AddParameter(cmd, "@p1", cutoff);
            cmd.CommandTimeout = 120;

            // Helper: read decimal or 0 if NULL
            decimal D(System.Data.Common.DbDataReader r, int i) => r.IsDBNull(i) ? 0m : r.GetDecimal(i);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                totalRows++;
                int win = reader.IsDBNull(36) ? 0 : reader.GetInt32(36);
                totalWins += win;

                // CSV row — compact format to maximize data within token budget
                rows.Add(string.Join(",",
                    reader.GetInt32(0),   // hour_et
                    reader.GetString(1),  // direction
                    D(reader, 2),         // price
                    D(reader, 3),         // score
                    D(reader, 4),         // obi
                    D(reader, 5),         // wobi
                    D(reader, 6),         // pressure_roc
                    D(reader, 7),         // spread_signal
                    D(reader, 8),         // large_order
                    D(reader, 9),         // spread
                    D(reader, 10),        // imbalance
                    D(reader, 11),        // ema9
                    D(reader, 12),        // ema20
                    D(reader, 13),        // rsi14
                    D(reader, 14),        // vwap
                    D(reader, 15),        // vol_ratio
                    D(reader, 16),        // tick_mom
                    D(reader, 17),        // book_depth
                    D(reader, 18),        // bid_wall
                    D(reader, 19),        // ask_wall
                    D(reader, 20),        // bid_sweep
                    D(reader, 21),        // ask_sweep
                    D(reader, 22),        // imb_vel
                    D(reader, 23),        // spread_pctl
                    D(reader, 24),        // b1
                    D(reader, 25),        // b2
                    D(reader, 26),        // b3
                    D(reader, 27),        // b4
                    D(reader, 28),        // b5
                    D(reader, 29),        // a1
                    D(reader, 30),        // a2
                    D(reader, 31),        // a3
                    D(reader, 32),        // a4
                    D(reader, 33),        // a5
                    D(reader, 34),        // price_1m
                    D(reader, 35),        // price_5m
                    win                   // win_1m
                ));
            }
        }

        if (totalRows < 100)
        {
            _logger.LogInformation("{Ticker}: only {Count} verified signals, insufficient for AI optimization", ticker, totalRows);
            return null;
        }

        _logger.LogInformation("{Ticker}: loaded {Rows} raw trades (win rate {WinRate:P1})",
            ticker, totalRows, totalRows > 0 ? (decimal)totalWins / totalRows : 0);

        // ═══════════════════════════════════════════════════════════
        // 2. Context section (fundamentals, capital flows, news — shared across all chunks)
        // ═══════════════════════════════════════════════════════════
        var fundamentals = await GetLatestFinancialsAsync(connection, symbolId);
        var capitalFlows = await GetRecentCapitalFlowsAsync(connection, symbolId, lookbackDays);
        var recentNews = await GetRecentNewsAsync(connection, symbolId, lookbackDays);

        var ctx = new StringBuilder();
        ctx.AppendLine($"## {ticker} (tickerId={tickerId}) — {totalRows} total trades, last {lookbackDays} days, win rate {(totalRows > 0 ? (decimal)totalWins / totalRows * 100 : 0):F1}%");

        if (fundamentals != null)
        {
            ctx.AppendLine($"P/E={fundamentals.Pe?.ToString("F1") ?? "?"} Beta={fundamentals.Beta?.ToString("F2") ?? "?"} ShortFloat={fundamentals.ShortFloat?.ToString("P1") ?? "?"} MarketCap={FormatLargeNumber(fundamentals.MarketCap)} 52wH=${fundamentals.High52w?.ToString("F2") ?? "?"} 52wL=${fundamentals.Low52w?.ToString("F2") ?? "?"}");
            if (fundamentals.NextEarningsDate != null)
                ctx.AppendLine($"NextEarnings={fundamentals.NextEarningsDate}");
        }

        if (capitalFlows.Count > 0)
        {
            ctx.Append("CapitalFlow: ");
            foreach (var flow in capitalFlows.Take(5))
            {
                decimal totalNet = (flow.LargeInflow - flow.LargeOutflow) + (flow.SuperLargeInflow - flow.SuperLargeOutflow) + (flow.MediumInflow - flow.MediumOutflow);
                ctx.Append($"{flow.Date}={FormatLargeNumber(totalNet)} ");
            }
            ctx.AppendLine();
        }

        if (recentNews.Count > 0)
        {
            ctx.AppendLine("RecentNews:");
            foreach (var news in recentNews.Take(5))
                ctx.AppendLine($"  [{news.PublishedAt:MMM dd}] {news.Title}");
        }

        string csvHeader = "hour_et,dir,price,score,obi,wobi,pressure_roc,spread_signal,large_order,spread,imbalance,ema9,ema20,rsi14,vwap,vol_ratio,tick_mom,book_depth,bid_wall,ask_wall,bid_sweep,ask_sweep,imb_vel,spread_pctl,b1,b2,b3,b4,b5,a1,a2,a3,a4,a5,price_1m,price_5m,win_1m";

        return new SymbolData(rows, ctx.ToString(), csvHeader, totalWins);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private static string FormatLargeNumber(decimal? value)
    {
        if (!value.HasValue) return "N/A";
        var v = value.Value;
        if (Math.Abs(v) >= 1_000_000_000) return $"${v / 1_000_000_000:F1}B";
        if (Math.Abs(v) >= 1_000_000) return $"${v / 1_000_000:F1}M";
        if (Math.Abs(v) >= 1_000) return $"${v / 1_000:F0}K";
        return $"${v:F0}";
    }

    private async Task<List<HourlyStat>> GetHourlyStatsAsync(
        System.Data.Common.DbConnection connection, string symbolId, long tickerId, DateTime cutoff)
    {
        var sql = @"
            SELECT
                EXTRACT(HOUR FROM (ts.""Timestamp"" AT TIME ZONE 'UTC') AT TIME ZONE 'America/New_York')::int AS ""Hour"",
                COUNT(*) AS ""TotalSignals"",
                COUNT(*) FILTER (WHERE ts.""Type"" = 1) AS ""BuySignals"",
                COUNT(*) FILTER (WHERE ts.""Type"" = 2) AS ""SellSignals"",
                COUNT(*) FILTER (WHERE ts.""Type"" = 1 AND ts.""PriceAfter1Min"" > ts.""Price"") AS ""BuyWins"",
                COUNT(*) FILTER (WHERE ts.""Type"" = 2 AND ts.""PriceAfter1Min"" < ts.""Price"") AS ""SellWins"",
                COALESCE(AVG(CASE WHEN ts.""Type"" = 1 AND ts.""PriceAfter1Min"" IS NOT NULL
                    THEN (ts.""PriceAfter1Min"" - ts.""Price"") * 500 END), 0) AS ""BuyAvgPnl"",
                COALESCE(AVG(CASE WHEN ts.""Type"" = 2 AND ts.""PriceAfter1Min"" IS NOT NULL
                    THEN (ts.""Price"" - ts.""PriceAfter1Min"") * 500 END), 0) AS ""SellAvgPnl""
            FROM ""TradingSignals"" ts
            WHERE ts.""SymbolId"" = @p0 AND ts.""Timestamp"" > @p1 AND ts.""PriceAfter1Min"" IS NOT NULL
            GROUP BY EXTRACT(HOUR FROM (ts.""Timestamp"" AT TIME ZONE 'UTC') AT TIME ZONE 'America/New_York')::int
            ORDER BY ""Hour""";

        var results = new List<HourlyStat>();
        // connection is already open (passed from PrepareSymbolAnalysisAsync)

        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        AddParameter(cmd, "@p0", symbolId);
        AddParameter(cmd, "@p1", cutoff);
        cmd.CommandTimeout = 60;

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new HourlyStat
            {
                Hour = reader.GetInt32(0),
                TotalSignals = reader.GetInt32(1),
                BuySignals = reader.GetInt32(2),
                SellSignals = reader.GetInt32(3),
                BuyWins = reader.GetInt32(4),
                SellWins = reader.GetInt32(5),
                BuyAvgPnl = reader.GetDecimal(6),
                SellAvgPnl = reader.GetDecimal(7),
            });
        }
        return results;
    }

    private async Task<List<IndicatorStat>> GetIndicatorEffectivenessAsync(
        System.Data.Common.DbConnection connection, string symbolId, long tickerId, DateTime cutoff)
    {
        // Query indicator effectiveness from TickSnapshots joined with TradingSignals
        var indicators = new[] { "ObiSmoothed", "Wobi", "PressureRoc", "LargeOrderSignal", "SpreadSignal" };
        var results = new List<IndicatorStat>();

        // connection is already open (passed from PrepareSymbolAnalysisAsync)

        foreach (var indName in indicators)
        {
            var sql = $@"
                SELECT
                    COUNT(*) FILTER (WHERE ts.""{indName}"" > 0.3 AND ts.""PriceAfter1Min"" IS NOT NULL
                        AND ((ts.""Type"" = 1 AND ts.""PriceAfter1Min"" > ts.""Price"")
                          OR (ts.""Type"" = 2 AND ts.""PriceAfter1Min"" < ts.""Price""))) AS ""HighWins"",
                    COUNT(*) FILTER (WHERE ts.""{indName}"" > 0.3 AND ts.""PriceAfter1Min"" IS NOT NULL) AS ""HighTotal"",
                    COUNT(*) FILTER (WHERE ts.""{indName}"" < -0.3 AND ts.""PriceAfter1Min"" IS NOT NULL
                        AND ((ts.""Type"" = 1 AND ts.""PriceAfter1Min"" > ts.""Price"")
                          OR (ts.""Type"" = 2 AND ts.""PriceAfter1Min"" < ts.""Price""))) AS ""LowWins"",
                    COUNT(*) FILTER (WHERE ts.""{indName}"" < -0.3 AND ts.""PriceAfter1Min"" IS NOT NULL) AS ""LowTotal"",
                    COUNT(*) FILTER (WHERE ts.""{indName}"" BETWEEN -0.3 AND 0.3 AND ts.""PriceAfter1Min"" IS NOT NULL
                        AND ((ts.""Type"" = 1 AND ts.""PriceAfter1Min"" > ts.""Price"")
                          OR (ts.""Type"" = 2 AND ts.""PriceAfter1Min"" < ts.""Price""))) AS ""NeutralWins"",
                    COUNT(*) FILTER (WHERE ts.""{indName}"" BETWEEN -0.3 AND 0.3 AND ts.""PriceAfter1Min"" IS NOT NULL) AS ""NeutralTotal""
                FROM ""TradingSignals"" ts
                WHERE ts.""SymbolId"" = @p0 AND ts.""Timestamp"" > @p1";

            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            AddParameter(cmd, "@p0", symbolId);
            AddParameter(cmd, "@p1", cutoff);
            cmd.CommandTimeout = 60;

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                int highWins = reader.GetInt32(0), highTotal = reader.GetInt32(1);
                int lowWins = reader.GetInt32(2), lowTotal = reader.GetInt32(3);
                int neutralWins = reader.GetInt32(4), neutralTotal = reader.GetInt32(5);

                results.Add(new IndicatorStat
                {
                    Name = indName,
                    HighWinRate = highTotal > 0 ? (decimal)highWins / highTotal * 100 : 50,
                    LowWinRate = lowTotal > 0 ? (decimal)lowWins / lowTotal * 100 : 50,
                    NeutralWinRate = neutralTotal > 0 ? (decimal)neutralWins / neutralTotal * 100 : 50,
                });
            }
        }

        // Also add L2 + tick + technical features from TickSnapshots
        // (name, unused, highThreshold, lowThreshold)
        var l2Indicators = new[] {
            ("BookDepthRatio", 0.5m, 0.8m, 0.2m),
            ("BidWallSize", 2.0m, 3.0m, 1.0m),
            ("AskWallSize", 2.0m, 3.0m, 1.0m),
            ("ImbalanceVelocity", 0.05m, 0.1m, -0.05m),
            ("BidSweepCost", 0m, 5000m, 500m),        // High liquidity vs thin
            ("AskSweepCost", 0m, 5000m, 500m),
            ("SpreadPercentile", 0.5m, 0.8m, 0.2m),   // Wide vs tight spread
            ("TickMomentum", 0m, 0.3m, -0.3m),         // Uptick vs downtick bias
            ("VolumeRatio", 1m, 1.5m, 0.5m),           // High vs low volume
        };

        foreach (var (name, _, highThresh, lowThresh) in l2Indicators)
        {
            try
            {
                var sql = $@"
                    SELECT
                        COUNT(*) FILTER (WHERE tk.""{name}"" > @p2
                            AND ((ts.""Type"" = 1 AND ts.""PriceAfter1Min"" > ts.""Price"")
                              OR (ts.""Type"" = 2 AND ts.""PriceAfter1Min"" < ts.""Price""))) AS ""HighWins"",
                        COUNT(*) FILTER (WHERE tk.""{name}"" > @p2 AND ts.""PriceAfter1Min"" IS NOT NULL) AS ""HighTotal"",
                        COUNT(*) FILTER (WHERE tk.""{name}"" < @p3
                            AND ((ts.""Type"" = 1 AND ts.""PriceAfter1Min"" > ts.""Price"")
                              OR (ts.""Type"" = 2 AND ts.""PriceAfter1Min"" < ts.""Price""))) AS ""LowWins"",
                        COUNT(*) FILTER (WHERE tk.""{name}"" < @p3 AND ts.""PriceAfter1Min"" IS NOT NULL) AS ""LowTotal""
                    FROM ""TradingSignals"" ts
                    JOIN ""TickSnapshots"" tk ON tk.""TickerId"" = ts.""TickerId""
                        AND tk.""Timestamp"" BETWEEN ts.""Timestamp"" - INTERVAL '5 seconds' AND ts.""Timestamp"" + INTERVAL '5 seconds'
                    WHERE ts.""SymbolId"" = @p0 AND ts.""Timestamp"" > @p1 AND ts.""PriceAfter1Min"" IS NOT NULL";

                using var cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                AddParameter(cmd, "@p0", symbolId);
                AddParameter(cmd, "@p1", cutoff);
                AddParameter(cmd, "@p2", highThresh);
                AddParameter(cmd, "@p3", lowThresh);
                cmd.CommandTimeout = 60;

                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    int highWins = reader.GetInt32(0), highTotal = reader.GetInt32(1);
                    int lowWins = reader.GetInt32(2), lowTotal = reader.GetInt32(3);

                    if (highTotal > 10 || lowTotal > 10)
                    {
                        results.Add(new IndicatorStat
                        {
                            Name = name,
                            HighWinRate = highTotal > 0 ? (decimal)highWins / highTotal * 100 : 50,
                            LowWinRate = lowTotal > 0 ? (decimal)lowWins / lowTotal * 100 : 50,
                            NeutralWinRate = 50,
                        });
                    }
                }
            }
            catch
            {
                // L2 columns may not exist yet (migration pending) — skip silently
            }
        }

        // Technical indicators: RSI zones and EMA trend from TickSnapshots
        try
        {
            var rsiSql = @"
                SELECT
                    COUNT(*) FILTER (WHERE tk.""Rsi14"" > 70
                        AND ((ts.""Type"" = 1 AND ts.""PriceAfter1Min"" > ts.""Price"")
                          OR (ts.""Type"" = 2 AND ts.""PriceAfter1Min"" < ts.""Price""))) AS ""OverboughtWins"",
                    COUNT(*) FILTER (WHERE tk.""Rsi14"" > 70 AND ts.""PriceAfter1Min"" IS NOT NULL) AS ""OverboughtTotal"",
                    COUNT(*) FILTER (WHERE tk.""Rsi14"" < 30
                        AND ((ts.""Type"" = 1 AND ts.""PriceAfter1Min"" > ts.""Price"")
                          OR (ts.""Type"" = 2 AND ts.""PriceAfter1Min"" < ts.""Price""))) AS ""OversoldWins"",
                    COUNT(*) FILTER (WHERE tk.""Rsi14"" < 30 AND ts.""PriceAfter1Min"" IS NOT NULL) AS ""OversoldTotal"",
                    COUNT(*) FILTER (WHERE tk.""Ema9"" > tk.""Ema20"" AND tk.""Ema9"" > 0
                        AND ((ts.""Type"" = 1 AND ts.""PriceAfter1Min"" > ts.""Price"")
                          OR (ts.""Type"" = 2 AND ts.""PriceAfter1Min"" < ts.""Price""))) AS ""UptrendWins"",
                    COUNT(*) FILTER (WHERE tk.""Ema9"" > tk.""Ema20"" AND tk.""Ema9"" > 0 AND ts.""PriceAfter1Min"" IS NOT NULL) AS ""UptrendTotal"",
                    COUNT(*) FILTER (WHERE tk.""Ema9"" < tk.""Ema20"" AND tk.""Ema9"" > 0
                        AND ((ts.""Type"" = 1 AND ts.""PriceAfter1Min"" > ts.""Price"")
                          OR (ts.""Type"" = 2 AND ts.""PriceAfter1Min"" < ts.""Price""))) AS ""DowntrendWins"",
                    COUNT(*) FILTER (WHERE tk.""Ema9"" < tk.""Ema20"" AND tk.""Ema9"" > 0 AND ts.""PriceAfter1Min"" IS NOT NULL) AS ""DowntrendTotal"",
                    COUNT(*) FILTER (WHERE tk.""Vwap"" > 0 AND tk.""Price"" > tk.""Vwap""
                        AND ((ts.""Type"" = 1 AND ts.""PriceAfter1Min"" > ts.""Price"")
                          OR (ts.""Type"" = 2 AND ts.""PriceAfter1Min"" < ts.""Price""))) AS ""AboveVwapWins"",
                    COUNT(*) FILTER (WHERE tk.""Vwap"" > 0 AND tk.""Price"" > tk.""Vwap"" AND ts.""PriceAfter1Min"" IS NOT NULL) AS ""AboveVwapTotal"",
                    COUNT(*) FILTER (WHERE tk.""Vwap"" > 0 AND tk.""Price"" <= tk.""Vwap""
                        AND ((ts.""Type"" = 1 AND ts.""PriceAfter1Min"" > ts.""Price"")
                          OR (ts.""Type"" = 2 AND ts.""PriceAfter1Min"" < ts.""Price""))) AS ""BelowVwapWins"",
                    COUNT(*) FILTER (WHERE tk.""Vwap"" > 0 AND tk.""Price"" <= tk.""Vwap"" AND ts.""PriceAfter1Min"" IS NOT NULL) AS ""BelowVwapTotal""
                FROM ""TradingSignals"" ts
                JOIN ""TickSnapshots"" tk ON tk.""TickerId"" = ts.""TickerId""
                    AND tk.""Timestamp"" BETWEEN ts.""Timestamp"" - INTERVAL '5 seconds' AND ts.""Timestamp"" + INTERVAL '5 seconds'
                WHERE ts.""SymbolId"" = @p0 AND ts.""Timestamp"" > @p1 AND ts.""PriceAfter1Min"" IS NOT NULL";

            using var rsiCmd = connection.CreateCommand();
            rsiCmd.CommandText = rsiSql;
            AddParameter(rsiCmd, "@p0", symbolId);
            AddParameter(rsiCmd, "@p1", cutoff);
            rsiCmd.CommandTimeout = 60;

            using var rsiReader = await rsiCmd.ExecuteReaderAsync();
            if (await rsiReader.ReadAsync())
            {
                int obWins = rsiReader.GetInt32(0), obTotal = rsiReader.GetInt32(1);
                int osWins = rsiReader.GetInt32(2), osTotal = rsiReader.GetInt32(3);
                if (obTotal > 10 || osTotal > 10)
                {
                    results.Add(new IndicatorStat
                    {
                        Name = "RSI14 (overbought>70 / oversold<30)",
                        HighWinRate = obTotal > 0 ? (decimal)obWins / obTotal * 100 : 50,
                        LowWinRate = osTotal > 0 ? (decimal)osWins / osTotal * 100 : 50,
                        NeutralWinRate = 50,
                    });
                }

                int upWins = rsiReader.GetInt32(4), upTotal = rsiReader.GetInt32(5);
                int dnWins = rsiReader.GetInt32(6), dnTotal = rsiReader.GetInt32(7);
                if (upTotal > 10 || dnTotal > 10)
                {
                    results.Add(new IndicatorStat
                    {
                        Name = "EMA Trend (EMA9>EMA20=uptrend / EMA9<EMA20=downtrend)",
                        HighWinRate = upTotal > 0 ? (decimal)upWins / upTotal * 100 : 50,
                        LowWinRate = dnTotal > 0 ? (decimal)dnWins / dnTotal * 100 : 50,
                        NeutralWinRate = 50,
                    });
                }

                int aboveWins = rsiReader.GetInt32(8), aboveTotal = rsiReader.GetInt32(9);
                int belowWins = rsiReader.GetInt32(10), belowTotal = rsiReader.GetInt32(11);
                if (aboveTotal > 10 || belowTotal > 10)
                {
                    results.Add(new IndicatorStat
                    {
                        Name = "VWAP Position (above=bullish / below=bearish)",
                        HighWinRate = aboveTotal > 0 ? (decimal)aboveWins / aboveTotal * 100 : 50,
                        LowWinRate = belowTotal > 0 ? (decimal)belowWins / belowTotal * 100 : 50,
                        NeutralWinRate = 50,
                    });
                }
            }
        }
        catch
        {
            // Technical indicator columns may not exist yet — skip silently
        }

        return results;
    }

    private async Task<List<ComboStat>> GetIndicatorCombinationsAsync(
        System.Data.Common.DbConnection connection, string symbolId, long tickerId, DateTime cutoff)
    {
        var results = new List<ComboStat>();
        var combos = new[]
        {
            ("OBI + PressureRoc", @"""ObiSmoothed""", @"""PressureRoc"""),
            ("OBI + LargeOrder", @"""ObiSmoothed""", @"""LargeOrderSignal"""),
            ("WOBI + SpreadSignal", @"""Wobi""", @"""SpreadSignal"""),
        };

        // connection is already open (passed from PrepareSymbolAnalysisAsync)

        foreach (var (name, col1, col2) in combos)
        {
            try
            {
                var sql = $@"
                    SELECT
                        COUNT(*) FILTER (WHERE {col1} > 0.2 AND {col2} > 0.2
                            AND ((ts.""Type"" = 1 AND ts.""PriceAfter1Min"" > ts.""Price"")
                              OR (ts.""Type"" = 2 AND ts.""PriceAfter1Min"" < ts.""Price""))) AS ""BothHighWins"",
                        COUNT(*) FILTER (WHERE {col1} > 0.2 AND {col2} > 0.2 AND ts.""PriceAfter1Min"" IS NOT NULL) AS ""BothHighTotal"",
                        COUNT(*) FILTER (WHERE {col1} < -0.2 AND {col2} < -0.2
                            AND ((ts.""Type"" = 1 AND ts.""PriceAfter1Min"" > ts.""Price"")
                              OR (ts.""Type"" = 2 AND ts.""PriceAfter1Min"" < ts.""Price""))) AS ""BothLowWins"",
                        COUNT(*) FILTER (WHERE {col1} < -0.2 AND {col2} < -0.2 AND ts.""PriceAfter1Min"" IS NOT NULL) AS ""BothLowTotal""
                    FROM ""TradingSignals"" ts
                    WHERE ts.""SymbolId"" = @p0 AND ts.""Timestamp"" > @p1 AND ts.""PriceAfter1Min"" IS NOT NULL";

                using var cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                AddParameter(cmd, "@p0", symbolId);
                AddParameter(cmd, "@p1", cutoff);
                cmd.CommandTimeout = 60;

                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    int bhWins = reader.GetInt32(0), bhTotal = reader.GetInt32(1);
                    int blWins = reader.GetInt32(2), blTotal = reader.GetInt32(3);

                    if (bhTotal + blTotal >= 10)
                    {
                        results.Add(new ComboStat
                        {
                            Name = name,
                            BothHighWinRate = bhTotal > 0 ? (decimal)bhWins / bhTotal * 100 : 50,
                            BothLowWinRate = blTotal > 0 ? (decimal)blWins / blTotal * 100 : 50,
                            Samples = bhTotal + blTotal,
                        });
                    }
                }
            }
            catch { }
        }

        return results;
    }

    private async Task<RecentVsHistorical?> GetRecentVsHistoricalAsync(
        System.Data.Common.DbConnection connection, string symbolId, long tickerId, DateTime cutoff)
    {
        try
        {
            var cutoff5d = DateTime.UtcNow.AddDays(-5);

            var sql = @"
                SELECT
                    COUNT(*) FILTER (WHERE ts.""Timestamp"" > @p2 AND ts.""Type"" = 1 AND ts.""PriceAfter1Min"" > ts.""Price"") AS ""R5BuyWins"",
                    COUNT(*) FILTER (WHERE ts.""Timestamp"" > @p2 AND ts.""Type"" = 1 AND ts.""PriceAfter1Min"" IS NOT NULL) AS ""R5BuyTotal"",
                    COUNT(*) FILTER (WHERE ts.""Timestamp"" > @p2 AND ts.""Type"" = 2 AND ts.""PriceAfter1Min"" < ts.""Price"") AS ""R5SellWins"",
                    COUNT(*) FILTER (WHERE ts.""Timestamp"" > @p2 AND ts.""Type"" = 2 AND ts.""PriceAfter1Min"" IS NOT NULL) AS ""R5SellTotal"",
                    COALESCE(AVG(ts.""Spread"") FILTER (WHERE ts.""Timestamp"" > @p2), 0) AS ""R5AvgSpread"",
                    COALESCE(AVG(ts.""Spread""), 0) AS ""FullAvgSpread""
                FROM ""TradingSignals"" ts
                WHERE ts.""SymbolId"" = @p0 AND ts.""Timestamp"" > @p1 AND ts.""PriceAfter1Min"" IS NOT NULL";

            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            AddParameter(cmd, "@p0", symbolId);
            AddParameter(cmd, "@p1", cutoff);
            AddParameter(cmd, "@p2", cutoff5d);
            cmd.CommandTimeout = 60;

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                int r5BuyWins = reader.GetInt32(0), r5BuyTotal = reader.GetInt32(1);
                int r5SellWins = reader.GetInt32(2), r5SellTotal = reader.GetInt32(3);

                return new RecentVsHistorical
                {
                    Recent5dBuyWinRate = r5BuyTotal > 0 ? (decimal)r5BuyWins / r5BuyTotal * 100 : 50,
                    Recent5dSellWinRate = r5SellTotal > 0 ? (decimal)r5SellWins / r5SellTotal * 100 : 50,
                    Recent5dAvgSpread = reader.GetDecimal(4),
                    FullAvgSpread = reader.GetDecimal(5),
                    Recent5dAvgVolume = 0, // Volume not stored in TradingSignals
                    FullAvgVolume = 0,
                };
            }
        }
        catch { }

        return null;
    }

    #endregion

    #region Bedrock API Call

    private async Task<SymbolStrategy?> CallBedrockAsync(long tickerId, string ticker, SymbolData data)
    {
        var region = _configuration["Bedrock:Region"];
        var modelId = _configuration["Bedrock:ModelId"];
        var rows = data.Rows;
        var contextSection = data.ContextSection;
        var csvHeader = data.CsvHeader;

        _logger.LogInformation("Calling Bedrock {Model} for {Ticker} ({RowCount} rows, {Chunks} chunks)...",
            modelId, ticker, rows.Count, (rows.Count + 999) / 1000);

        var systemPrompt = @"You are a quantitative trading strategy optimizer. You receive RAW TRADE DATA — every signal with all indicator values and the actual outcome (price after 1min/5min, win/loss).

Your job: analyze the raw data to discover patterns that predict profitable trades, then output conditional rules as JSON.

## How the trading system works
- Real-time L2 order book snapshots arrive every ~0.5s
- 10 weighted indicators produce a composite score each snapshot
- YOUR rules are evaluated first — if a rule matches, it fires immediately (bypasses scoring)
- A PositionMonitor checks exits every 5s (score decay, trailing stops, time gates)
- Commission: $2.99 per trade ($5.98 round-trip)

## CSV column definitions
hour_et: Eastern Time hour (9=9:30-10 AM, 15=3-4 PM)
dir: BUY or SELL
price: entry price
score: composite score at entry (-1 to +1)
obi: order book imbalance, smoothed (-1 to +1, positive=bid heavy)
wobi: weighted OBI (closer levels weighted more)
pressure_roc: rate of change of book pressure
spread_signal: tight spread=positive, wide=negative
large_order: large bid orders=positive, large asks=negative
spread: raw bid-ask spread in dollars
imbalance: raw OBI at snapshot time
ema9, ema20: exponential moving averages
rsi14: relative strength index (>70 overbought, <30 oversold)
vwap: volume-weighted average price
vol_ratio: current volume / average (>1.5 = high volume)
tick_mom: uptick-downtick ratio (-1 to +1)
book_depth: top 5 levels / total depth (concentration)
bid_wall, ask_wall: max level size / avg level size (wall detection)
bid_sweep, ask_sweep: total shares on bid/ask side
imb_vel: imbalance velocity (OBI change per second)
spread_pctl: spread percentile in last 5 min (0=tightest, 1=widest)
b1-b5: top 5 bid level sizes (shares) from order book at signal time (b1=best bid, b5=5th level)
a1-a5: top 5 ask level sizes (shares) from order book at signal time (a1=best ask, a5=5th level)
  - b1 >> a1 means buyers are front-loading → bullish pressure
  - a1 very large = ask wall (resistance), b1 very large = bid wall (support)
  - Decreasing sizes (b1>b2>b3) = normal book; b3>>b1 = hidden deep support
  - All zeros means no L2 snapshot was available at signal time
price_1m: actual price 1 minute after signal
price_5m: actual price 5 minutes after signal
win_1m: 1 if trade was profitable at 1 min, 0 if not

## Your task
1. Study the raw data. Look at winning vs losing trades — what indicator values differ?
2. Find patterns: time-of-day edges, indicator thresholds, multi-condition combos
3. Look for interactions the scoring system misses (e.g., OBI matters more when spread is tight)
4. Output 3-8 high-confidence rules per symbol
5. Each rule MUST have confidence >= 0.55, sample >= 30, positive expected P&L after $5.98 fee
6. Set holdSeconds based on whether price_1m or price_5m shows better outcomes for that pattern
7. Set stopLoss based on the typical adverse move for losing trades in that pattern
8. Set maxPositionDollars based on price and volatility (default $25K, lower for expensive stocks)";

        var jsonTemplate = $@"{{
  ""tickerId"": {tickerId},
  ""overallWinRate"": <decimal>,
  ""rules"": [
    {{
      ""id"": ""{ticker}-001"",
      ""name"": ""<descriptive name>"",
      ""hours"": [<ET hours>],
      ""direction"": ""BUY"" or ""SELL"",
      ""conditions"": {{
        ""minObi"": <number or null>, ""maxObi"": <number or null>,
        ""minImbalanceVelocity"": <number or null>, ""maxImbalanceVelocity"": <number or null>,
        ""minBidWallSize"": <number or null>, ""minAskWallSize"": <number or null>,
        ""minBookDepthRatio"": <number or null>, ""maxBookDepthRatio"": <number or null>,
        ""minBidSweepCost"": <number or null>, ""minAskSweepCost"": <number or null>,
        ""minSpreadPercentile"": <number or null>, ""maxSpreadPercentile"": <number or null>,
        ""trendDirection"": <1, -1, or null>,
        ""minTickMomentum"": <number or null>, ""maxTickMomentum"": <number or null>,
        ""rsiRange"": [<min>, <max>] or null,
        ""minVolumeRatio"": <number or null>,
        ""aboveVwap"": <true, false, or null>
      }},
      ""confidence"": <0.55-1.0>,
      ""expectedPnlPer100Shares"": <positive after $5.98 commission>,
      ""sampleSize"": <int, >= 30>,
      ""holdSeconds"": <30-300>,
      ""stopLoss"": <0.10-2.00>
    }}
  ],
  ""disabledHours"": [<hours with win rate < 48%>],
  ""maxDailyTrades"": <5-30>,
  ""maxPositionShares"": 500,
  ""maxPositionDollars"": <default 25000, lower for expensive/volatile>
}}";

        try
        {
            using var client = new AmazonBedrockRuntimeClient(Amazon.RegionEndpoint.GetBySystemName(region));

            // Split data into chunks of 1000 rows, call Bedrock for each chunk
            const int chunkSize = 1000;
            int chunkCount = (rows.Count + chunkSize - 1) / chunkSize;
            var allRules = new List<StrategyRule>();
            decimal overallWinRate = 0;
            var disabledHours = new HashSet<int>();
            int maxDailyTrades = 20;
            decimal maxPositionDollars = 25000m;

            for (int chunk = 0; chunk < chunkCount; chunk++)
            {
                var chunkRows = rows.Skip(chunk * chunkSize).Take(chunkSize).ToList();
                int chunkStart = chunk * chunkSize;
                string chunkLabel = chunkCount == 1
                    ? $"all {rows.Count} trades"
                    : $"batch {chunk + 1}/{chunkCount} (trades {chunkStart + 1}-{chunkStart + chunkRows.Count} of {rows.Count}, most recent first)";

                var chunkPrompt = new StringBuilder();
                chunkPrompt.AppendLine(contextSection); // Fundamentals, flows, news (same for all chunks)
                chunkPrompt.AppendLine();
                chunkPrompt.AppendLine($"## Raw Trade Data for {ticker} — {chunkLabel}");
                chunkPrompt.AppendLine(csvHeader);
                foreach (var row in chunkRows)
                    chunkPrompt.AppendLine(row);
                chunkPrompt.AppendLine();
                chunkPrompt.AppendLine($"## Instructions");
                chunkPrompt.AppendLine($"Analyze this batch of {ticker} trades. Output a JSON object with trading rules you discover.");
                chunkPrompt.AppendLine($"Output ONLY the JSON object in this format:");
                chunkPrompt.AppendLine(jsonTemplate);

                var request = new ConverseRequest
                {
                    ModelId = modelId,
                    System = [new SystemContentBlock { Text = systemPrompt }],
                    Messages =
                    [
                        new Message
                        {
                            Role = ConversationRole.User,
                            Content = [new ContentBlock { Text = chunkPrompt.ToString() }]
                        }
                    ],
                    InferenceConfig = new InferenceConfiguration
                    {
                        MaxTokens = 4096,
                        Temperature = 0.3f,
                    },
                };

                var response = await client.ConverseAsync(request);
                var responseText = response.Output?.Message?.Content
                    ?.FirstOrDefault(c => c.Text != null)?.Text;

                _logger.LogInformation(
                    "Bedrock {Ticker} chunk {Chunk}/{Total}: {Length} chars, input={Input} output={Output}",
                    ticker, chunk + 1, chunkCount, responseText?.Length ?? 0,
                    response.Usage?.InputTokens ?? 0, response.Usage?.OutputTokens ?? 0);

                if (string.IsNullOrWhiteSpace(responseText)) continue;

                var chunkStrategy = ParseAndValidateRules(ticker, responseText);
                if (chunkStrategy != null)
                {
                    // Collect rules from this chunk, prefix IDs to avoid collisions
                    foreach (var rule in chunkStrategy.Rules)
                    {
                        rule.Id = $"{ticker}-c{chunk + 1}-{rule.Id.Split('-').Last()}";
                        allRules.Add(rule);
                    }
                    overallWinRate = chunkStrategy.OverallWinRate; // Last chunk wins
                    foreach (var h in chunkStrategy.DisabledHours)
                        disabledHours.Add(h);
                    if (chunkStrategy.MaxDailyTrades > 0)
                        maxDailyTrades = chunkStrategy.MaxDailyTrades;
                    if (chunkStrategy.MaxPositionDollars > 0)
                        maxPositionDollars = chunkStrategy.MaxPositionDollars;
                }
            }

            if (allRules.Count == 0)
            {
                _logger.LogWarning("No rules generated for {Ticker} across {Chunks} chunks", ticker, chunkCount);
                return null;
            }

            // Deduplicate: if two chunks found similar rules (same direction + overlapping hours),
            // keep the one with higher confidence
            var deduped = allRules
                .GroupBy(r => $"{r.Direction}_{string.Join(",", r.Hours.OrderBy(h => h))}")
                .Select(g => g.OrderByDescending(r => r.Confidence).First())
                .ToList();

            _logger.LogWarning("{Ticker}: {Total} rules from {Chunks} chunks, {Deduped} after dedup",
                ticker, allRules.Count, chunkCount, deduped.Count);

            return new SymbolStrategy
            {
                TickerId = tickerId,
                OverallWinRate = overallWinRate,
                Rules = deduped,
                DisabledHours = disabledHours.OrderBy(h => h).ToList(),
                MaxDailyTrades = maxDailyTrades,
                MaxPositionDollars = maxPositionDollars,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bedrock API call failed for {Ticker}", ticker);
            return null;
        }
    }

    private SymbolStrategy? ParseAndValidateRules(string ticker, string responseText)
    {
        try
        {
            // Extract JSON from response — handle markdown fences, preamble text, etc.
            var json = responseText.Trim();

            // Strip markdown code fences if present
            if (json.Contains("```"))
            {
                int fenceStart = json.IndexOf("```");
                int firstNewline = json.IndexOf('\n', fenceStart);
                int lastFence = json.LastIndexOf("```");
                if (firstNewline >= 0 && lastFence > firstNewline)
                    json = json[(firstNewline + 1)..lastFence].Trim();
            }

            // Find the first '{' — skip any preamble text the model outputs
            int braceStart = json.IndexOf('{');
            int braceEnd = json.LastIndexOf('}');
            if (braceStart >= 0 && braceEnd > braceStart)
                json = json[braceStart..(braceEnd + 1)];

            var strategy = JsonSerializer.Deserialize<SymbolStrategy>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

            if (strategy == null || strategy.Rules.Count == 0)
            {
                _logger.LogWarning("No valid rules parsed for {Ticker}", ticker);
                return null;
            }

            // Validate and filter rules (quality gate)
            var validRules = strategy.Rules
                .Where(r => r.SampleSize >= 30 && r.Confidence >= 0.55m)
                .Where(r => r.ExpectedPnlPer100Shares > 0) // Must be profitable after fees
                .Where(r => r.Direction is "BUY" or "SELL")
                .Where(r => r.HoldSeconds is >= 10 and <= 600)
                .Where(r => r.StopLoss is >= 0.05m and <= 5.0m)
                .ToList();

            strategy.Rules = validRules;

            _logger.LogInformation("{Ticker}: {Valid}/{Total} rules passed validation",
                ticker, validRules.Count, strategy.Rules.Count);

            return validRules.Count > 0 ? strategy : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Bedrock response for {Ticker}: {Response}",
                ticker, responseText[..Math.Min(200, responseText.Length)]);
            return null;
        }
    }

    #endregion

    #region Save & Load

    private async Task SaveStrategyRulesAsync(StrategyConfig config)
    {
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        string json = JsonSerializer.Serialize(config, jsonOptions);

        var dir = Path.GetDirectoryName(StrategyConfigPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(StrategyConfigPath, json);
        _logger.LogWarning("Strategy rules saved: {Path} ({SymbolCount} symbols, {RuleCount} total rules)",
            StrategyConfigPath, config.Symbols.Count,
            config.Symbols.Values.Sum(s => s.Rules.Count));

        // Also persist as backup in database
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TradingPilotDbContext>();
            await dbContext.Database.ExecuteSqlRawAsync(@"
                INSERT INTO ""ModelConfigs"" (""Key"", ""Value"", ""UpdatedAt"")
                VALUES ('strategy_rules', {0}, {1})
                ON CONFLICT (""Key"") DO UPDATE SET ""Value"" = {0}, ""UpdatedAt"" = {1}",
                json, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist strategy rules to database (non-fatal)");
        }
    }

    #endregion

    #region Data Cleanup (moved from NightlyModelTrainer)

    /// <summary>
    /// Data retention cleanup. Called nightly at 9:30 PM ET.
    /// Retention: SymbolBookSnapshots=3 days, rest=forever
    /// </summary>
    public async Task CleanupOldDataAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TradingPilotDbContext>();

            // Delete in batches of 5000 to avoid long table locks
            var bookCutoff = DateTime.UtcNow.AddDays(-3);
            int bookDeleted = 0;
            int batch;
            do
            {
                batch = await dbContext.Database.ExecuteSqlRawAsync(
                    @"DELETE FROM ""SymbolBookSnapshots"" WHERE ""Id"" IN (
                        SELECT ""Id"" FROM ""SymbolBookSnapshots"" WHERE ""Timestamp"" < {0} LIMIT 5000
                    )", bookCutoff);
                bookDeleted += batch;
            } while (batch >= 5000);

            _logger.LogWarning("Cleanup: deleted {BookCount} SymbolBookSnapshots (>3d)", bookDeleted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cleanup failed (non-fatal)");
        }
    }

    #endregion

    #region Fundamental + Sentiment Queries

    private async Task<FinancialData?> GetLatestFinancialsAsync(
        System.Data.Common.DbConnection connection, string symbolId)
    {
        try
        {
            var sql = @"
                SELECT ""Pe"", ""ForwardPe"", ""Eps"", ""EstEps"", ""MarketCap"",
                       ""Beta"", ""ShortFloat"", ""High52w"", ""Low52w"",
                       ""DividendYield"", ""NextEarningsDate""
                FROM ""SymbolFinancialSnapshots""
                WHERE ""SymbolId"" = @p0
                ORDER BY ""Date"" DESC LIMIT 1";

            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            AddParameter(cmd, "@p0", symbolId);

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new FinancialData
                {
                    Pe = reader.IsDBNull(0) ? null : reader.GetDecimal(0),
                    ForwardPe = reader.IsDBNull(1) ? null : reader.GetDecimal(1),
                    Eps = reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                    EstEps = reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                    MarketCap = reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                    Beta = reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                    ShortFloat = reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                    High52w = reader.IsDBNull(7) ? null : reader.GetDecimal(7),
                    Low52w = reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                    DividendYield = reader.IsDBNull(9) ? null : reader.GetDecimal(9),
                    NextEarningsDate = reader.IsDBNull(10) ? null : reader.GetString(10),
                };
            }
        }
        catch { }
        return null;
    }

    private async Task<List<CapitalFlowData>> GetRecentCapitalFlowsAsync(
        System.Data.Common.DbConnection connection, string symbolId, int lookbackDays)
    {
        var results = new List<CapitalFlowData>();
        try
        {
            var sql = @"
                SELECT ""Date"", ""SuperLargeInflow"", ""SuperLargeOutflow"",
                       ""LargeInflow"", ""LargeOutflow"", ""MediumInflow"", ""MediumOutflow""
                FROM ""SymbolCapitalFlows""
                WHERE ""SymbolId"" = @p0 AND ""Date"" >= @p1
                ORDER BY ""Date"" DESC LIMIT 10";

            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            AddParameter(cmd, "@p0", symbolId);
            AddParameter(cmd, "@p1", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-lookbackDays)));

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new CapitalFlowData
                {
                    Date = reader.GetFieldValue<DateOnly>(0),
                    SuperLargeInflow = reader.GetDecimal(1),
                    SuperLargeOutflow = reader.GetDecimal(2),
                    LargeInflow = reader.GetDecimal(3),
                    LargeOutflow = reader.GetDecimal(4),
                    MediumInflow = reader.GetDecimal(5),
                    MediumOutflow = reader.GetDecimal(6),
                });
            }
        }
        catch { }
        return results;
    }

    private async Task<List<NewsData>> GetRecentNewsAsync(
        System.Data.Common.DbConnection connection, string symbolId, int lookbackDays)
    {
        var results = new List<NewsData>();
        try
        {
            // Only last 5 days of news to keep prompt concise
            var sql = @"
                SELECT ""Title"", ""PublishedAt""
                FROM ""SymbolNews""
                WHERE ""SymbolId"" = @p0 AND ""PublishedAt"" >= @p1
                ORDER BY ""PublishedAt"" DESC LIMIT 10";

            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            AddParameter(cmd, "@p0", symbolId);
            AddParameter(cmd, "@p1", DateTime.UtcNow.AddDays(-5));

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new NewsData
                {
                    Title = reader.GetString(0),
                    PublishedAt = reader.GetDateTime(1),
                });
            }
        }
        catch { }
        return results;
    }

    #endregion

    #region Gap Backfill (TradingSignals directly from bars + L2 snapshots)

    /// <summary>
    /// Backfill TradingSignals directly from 1-minute bars + nearest SymbolBookSnapshots.
    /// Computes technical indicators from bars and L2 features from book snapshots.
    /// No intermediate TickSnapshots table needed.
    /// </summary>
    private async Task BackfillTradingSignalsFromBarsAsync(long tickerId, string ticker, string symbolId, int lookbackDays)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TradingPilotDbContext>();

        var cutoff = DateTime.UtcNow.AddDays(-lookbackDays);

        // Step 1: Insert signals from bars with technical indicators (fast, no JSONB)
        dbContext.Database.SetCommandTimeout(TimeSpan.FromMinutes(2));
        int inserted = await dbContext.Database.ExecuteSqlRawAsync(@"
            WITH bars AS (
                SELECT sb.""SymbolId"", sb.""Timestamp"", sb.""Close"", sb.""Volume"",
                       COALESCE(sb.""Vwap"", 0) AS vwap,
                       sb.""Close"" - LAG(sb.""Close"") OVER (ORDER BY sb.""Timestamp"") AS price_change,
                       AVG(sb.""Volume"") OVER (ORDER BY sb.""Timestamp"" ROWS BETWEEN 20 PRECEDING AND 1 PRECEDING) AS avg_vol,
                       AVG(sb.""Close"") OVER (ORDER BY sb.""Timestamp"" ROWS BETWEEN 8 PRECEDING AND CURRENT ROW) AS sma9,
                       AVG(sb.""Close"") OVER (ORDER BY sb.""Timestamp"" ROWS BETWEEN 19 PRECEDING AND CURRENT ROW) AS sma20,
                       LEAD(sb.""Close"", 1) OVER (ORDER BY sb.""Timestamp"") AS price_1after,
                       LEAD(sb.""Close"", 5) OVER (ORDER BY sb.""Timestamp"") AS price_5after,
                       LAG(sb.""Close"", 5) OVER (ORDER BY sb.""Timestamp"") AS price_5ago,
                       ROW_NUMBER() OVER (ORDER BY sb.""Timestamp"") AS rn
                FROM ""SymbolBars"" sb
                WHERE sb.""SymbolId"" = {1} AND sb.""Timeframe"" = 2 AND sb.""Timestamp"" > {2}
            ),
            with_rsi AS (
                SELECT b.*,
                       COALESCE(AVG(GREATEST(b.price_change, 0)) OVER (ORDER BY b.""Timestamp"" ROWS BETWEEN 13 PRECEDING AND CURRENT ROW), 0) AS avg_gain,
                       COALESCE(AVG(GREATEST(-b.price_change, 0)) OVER (ORDER BY b.""Timestamp"" ROWS BETWEEN 13 PRECEDING AND CURRENT ROW), 0) AS avg_loss
                FROM bars b
            ),
            scored AS (
                SELECT r.*,
                    COALESCE(r.sma9, r.""Close"") AS ema9,
                    COALESCE(r.sma20, r.""Close"") AS ema20,
                    CASE WHEN r.avg_loss = 0 THEN 100.0 WHEN r.avg_gain = 0 THEN 0.0
                         ELSE ROUND((100.0 - 100.0 / (1.0 + r.avg_gain / r.avg_loss))::numeric, 2) END AS rsi14,
                    CASE WHEN COALESCE(r.avg_vol, 0) = 0 THEN 1.0
                         ELSE ROUND((r.""Volume""::numeric / r.avg_vol)::numeric, 4) END AS vol_ratio,
                    CASE WHEN COALESCE(r.price_change, 0) > 0 THEN 1.0
                         WHEN COALESCE(r.price_change, 0) < 0 THEN -1.0 ELSE 0.0 END AS tick_momentum,
                    CASE WHEN COALESCE(r.sma9,0) > 0 AND COALESCE(r.sma20,0) > 0 AND r.sma9 > r.sma20 THEN 0.15
                         WHEN COALESCE(r.sma9,0) > 0 AND COALESCE(r.sma20,0) > 0 AND r.sma9 < r.sma20 THEN -0.15 ELSE 0 END
                    + CASE WHEN r.avg_gain + r.avg_loss > 0 THEN
                        ((100.0 - 100.0 / (1.0 + CASE WHEN r.avg_loss = 0 THEN 100 ELSE r.avg_gain / r.avg_loss END)) - 50.0) / 200.0
                        ELSE 0 END
                    + CASE WHEN r.vwap > 0 AND r.""Close"" > 0 THEN LEAST(GREATEST((r.""Close"" - r.vwap) / r.""Close"" * 50, -0.15), 0.15) ELSE 0 END
                    + CASE WHEN r.price_5ago > 0 THEN LEAST(GREATEST((r.""Close"" - r.price_5ago) / r.price_5ago * 100, -0.20), 0.20) ELSE 0 END
                    + CASE WHEN COALESCE(r.avg_vol,0) > 0 AND r.""Volume"" / r.avg_vol > 1.5 THEN 0.10
                           WHEN COALESCE(r.avg_vol,0) > 0 AND r.""Volume"" / r.avg_vol < 0.5 THEN -0.10 ELSE 0 END
                    AS score
                FROM with_rsi r WHERE r.rn > 20 AND r.rn % 6 = 0
            )
            INSERT INTO ""TradingSignals"" (
                ""Id"", ""SymbolId"", ""TickerId"", ""Timestamp"",
                ""Type"", ""Strength"", ""Price"", ""Score"", ""Reason"",
                ""ObiSmoothed"", ""Wobi"", ""PressureRoc"", ""SpreadSignal"", ""LargeOrderSignal"",
                ""Spread"", ""Imbalance"", ""BidLevels"", ""AskLevels"",
                ""Ema9"", ""Ema20"", ""Rsi14"", ""Vwap"", ""VolumeRatio"", ""TickMomentum"",
                ""BookDepthRatio"", ""BidWallSize"", ""AskWallSize"",
                ""BidSweepCost"", ""AskSweepCost"", ""ImbalanceVelocity"", ""SpreadPercentile"",
                ""PriceAfter1Min"", ""PriceAfter5Min"",
                ""WasCorrect1Min"", ""VerifiedAt""
            )
            SELECT
                gen_random_uuid(), s.""SymbolId"", {0}, s.""Timestamp"",
                CASE WHEN s.score > 0 THEN 1 ELSE 2 END,
                CASE WHEN ABS(s.score) >= 0.40 THEN 2 WHEN ABS(s.score) >= 0.20 THEN 1 ELSE 0 END,
                s.""Close"", s.score, '[BACKFILL]',
                0, 0, 0, 0, 0,
                0, 0, 0, 0,
                s.ema9, s.ema20, s.rsi14, s.vwap, s.vol_ratio, s.tick_momentum,
                0, 0, 0, 0, 0, 0, 0.5,
                CASE WHEN s.price_1after > 0 THEN s.price_1after ELSE NULL END,
                CASE WHEN s.price_5after > 0 THEN s.price_5after ELSE NULL END,
                CASE WHEN s.price_1after > 0 THEN
                    CASE WHEN s.score > 0 THEN s.price_1after > s.""Close""
                         ELSE s.price_1after < s.""Close"" END
                    ELSE NULL END,
                NOW()
            FROM scored s
            WHERE ABS(s.score) >= 0.10
              AND NOT EXISTS (
                  SELECT 1 FROM ""TradingSignals"" ts
                  WHERE ts.""SymbolId"" = s.""SymbolId""
                    AND ts.""Timestamp"" BETWEEN s.""Timestamp"" - INTERVAL '30 seconds'
                                              AND s.""Timestamp"" + INTERVAL '30 seconds'
              )",
            tickerId, symbolId, cutoff);

        if (inserted > 0)
        {
            _logger.LogWarning("Backfilled {Inserted} TradingSignals from bars for {Ticker}, enriching with L2...",
                inserted, ticker);

            // Step 2: Enrich newly backfilled signals with L2 features from SymbolBookSnapshots
            // Process in batches of 500 to avoid timeout on heavy JSONB parsing
            dbContext.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
            int enriched = await dbContext.Database.ExecuteSqlRawAsync(@"
                UPDATE ""TradingSignals"" ts
                SET
                    ""Spread"" = COALESCE(l2.spread, ts.""Spread""),
                    ""Imbalance"" = COALESCE(l2.imbalance, ts.""Imbalance""),
                    ""BidLevels"" = COALESCE(l2.bid_levels, ts.""BidLevels""),
                    ""AskLevels"" = COALESCE(l2.ask_levels, ts.""AskLevels""),
                    ""BookDepthRatio"" = COALESCE(l2.book_depth_ratio, 0),
                    ""BidWallSize"" = COALESCE(l2.bid_wall_size, 0),
                    ""AskWallSize"" = COALESCE(l2.ask_wall_size, 0),
                    ""BidSweepCost"" = COALESCE(l2.bid_sweep_cost, 0),
                    ""AskSweepCost"" = COALESCE(l2.ask_sweep_cost, 0),
                    ""ImbalanceVelocity"" = COALESCE(l2.imbalance_velocity, 0),
                    ""SpreadPercentile"" = COALESCE(l2.spread_percentile, 0.5)
                FROM (
                    SELECT DISTINCT ON (sig.""Id"") sig.""Id"" AS signal_id,
                        bs.""Spread"" AS spread,
                        bs.""Imbalance"" AS imbalance,
                        jsonb_array_length(bs.""BidPrices""::jsonb) AS bid_levels,
                        jsonb_array_length(bs.""AskPrices""::jsonb) AS ask_levels,
                        CASE WHEN (SELECT SUM(v::numeric) FROM jsonb_array_elements_text(bs.""BidSizes""::jsonb) v) +
                                  (SELECT SUM(v::numeric) FROM jsonb_array_elements_text(bs.""AskSizes""::jsonb) v) > 0
                             THEN ((SELECT COALESCE(SUM(v::numeric),0) FROM (SELECT v FROM jsonb_array_elements_text(bs.""BidSizes""::jsonb) v LIMIT 5) t) +
                                   (SELECT COALESCE(SUM(v::numeric),0) FROM (SELECT v FROM jsonb_array_elements_text(bs.""AskSizes""::jsonb) v LIMIT 5) t2)) /
                                  ((SELECT SUM(v::numeric) FROM jsonb_array_elements_text(bs.""BidSizes""::jsonb) v) +
                                   (SELECT SUM(v::numeric) FROM jsonb_array_elements_text(bs.""AskSizes""::jsonb) v))
                             ELSE 0 END AS book_depth_ratio,
                        CASE WHEN (SELECT AVG(v::numeric) FROM jsonb_array_elements_text(bs.""BidSizes""::jsonb) v) > 0
                             THEN (SELECT MAX(v::numeric) FROM jsonb_array_elements_text(bs.""BidSizes""::jsonb) v) /
                                  (SELECT AVG(v::numeric) FROM jsonb_array_elements_text(bs.""BidSizes""::jsonb) v)
                             ELSE 0 END AS bid_wall_size,
                        CASE WHEN (SELECT AVG(v::numeric) FROM jsonb_array_elements_text(bs.""AskSizes""::jsonb) v) > 0
                             THEN (SELECT MAX(v::numeric) FROM jsonb_array_elements_text(bs.""AskSizes""::jsonb) v) /
                                  (SELECT AVG(v::numeric) FROM jsonb_array_elements_text(bs.""AskSizes""::jsonb) v)
                             ELSE 0 END AS ask_wall_size,
                        COALESCE((SELECT SUM(v::numeric) FROM jsonb_array_elements_text(bs.""BidSizes""::jsonb) v), 0) AS bid_sweep_cost,
                        COALESCE((SELECT SUM(v::numeric) FROM jsonb_array_elements_text(bs.""AskSizes""::jsonb) v), 0) AS ask_sweep_cost,
                        CASE WHEN prev_bs.""Imbalance"" IS NOT NULL
                             THEN (bs.""Imbalance"" - prev_bs.""Imbalance"") / GREATEST(EXTRACT(EPOCH FROM bs.""Timestamp"" - prev_bs.""Timestamp""), 1)
                             ELSE 0 END AS imbalance_velocity,
                        0.5 AS spread_percentile
                    FROM ""TradingSignals"" sig
                    JOIN ""SymbolBookSnapshots"" bs ON bs.""SymbolId"" = sig.""SymbolId""
                        AND bs.""Timestamp"" BETWEEN sig.""Timestamp"" - INTERVAL '30 seconds' AND sig.""Timestamp"" + INTERVAL '30 seconds'
                    LEFT JOIN LATERAL (
                        SELECT ""Imbalance"", ""Timestamp"" FROM ""SymbolBookSnapshots"" prev
                        WHERE prev.""SymbolId"" = bs.""SymbolId"" AND prev.""Timestamp"" < bs.""Timestamp""
                          AND prev.""Timestamp"" > bs.""Timestamp"" - INTERVAL '60 seconds'
                        ORDER BY prev.""Timestamp"" DESC LIMIT 1
                    ) prev_bs ON true
                    WHERE sig.""Reason"" = '[BACKFILL]'
                      AND sig.""SymbolId"" = {1}
                      AND sig.""BookDepthRatio"" = 0
                    ORDER BY sig.""Id"", ABS(EXTRACT(EPOCH FROM bs.""Timestamp"" - sig.""Timestamp""))
                ) l2
                WHERE ts.""Id"" = l2.signal_id",
                tickerId, symbolId);

            _logger.LogWarning("Enriched {Enriched}/{Inserted} backfilled signals with L2 features for {Ticker}",
                enriched, inserted, ticker);
        }
    }

    #endregion

    #region Helpers

    private async Task<List<(long TickerId, string Ticker)>> GetActiveSymbolsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TradingPilotDbContext>();

        using var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync();

        var sql = @"SELECT ""Id"", ""WebullTickerId"" FROM ""Symbols"" WHERE ""IsWatched"" = true AND ""Status"" = 1";
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;

        var results = new List<(long, string)>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add((
                reader.GetInt64(reader.GetOrdinal("WebullTickerId")),
                reader.GetString(reader.GetOrdinal("Id"))
            ));
        }
        return results;
    }

    private static void AddParameter(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var param = cmd.CreateParameter();
        param.ParameterName = name;
        param.Value = value;
        cmd.Parameters.Add(param);
    }

    #endregion
}

#region Analysis DTOs

internal class HourlyStat
{
    public int Hour { get; set; }
    public int TotalSignals { get; set; }
    public int BuySignals { get; set; }
    public int SellSignals { get; set; }
    public int BuyWins { get; set; }
    public int SellWins { get; set; }
    public decimal BuyAvgPnl { get; set; }
    public decimal SellAvgPnl { get; set; }
}

internal class IndicatorStat
{
    public string Name { get; set; } = "";
    public decimal HighWinRate { get; set; }
    public decimal LowWinRate { get; set; }
    public decimal NeutralWinRate { get; set; }
}

internal class ComboStat
{
    public string Name { get; set; } = "";
    public decimal BothHighWinRate { get; set; }
    public decimal BothLowWinRate { get; set; }
    public int Samples { get; set; }
}

internal class RecentVsHistorical
{
    public decimal Recent5dBuyWinRate { get; set; }
    public decimal Recent5dSellWinRate { get; set; }
    public decimal Recent5dAvgSpread { get; set; }
    public decimal Recent5dAvgVolume { get; set; }
    public decimal FullAvgSpread { get; set; }
    public decimal FullAvgVolume { get; set; }
}

internal class FinancialData
{
    public decimal? Pe { get; set; }
    public decimal? ForwardPe { get; set; }
    public decimal? Eps { get; set; }
    public decimal? EstEps { get; set; }
    public decimal? MarketCap { get; set; }
    public decimal? Beta { get; set; }
    public decimal? ShortFloat { get; set; }
    public decimal? High52w { get; set; }
    public decimal? Low52w { get; set; }
    public decimal? DividendYield { get; set; }
    public string? NextEarningsDate { get; set; }
}

internal class CapitalFlowData
{
    public DateOnly Date { get; set; }
    public decimal SuperLargeInflow { get; set; }
    public decimal SuperLargeOutflow { get; set; }
    public decimal LargeInflow { get; set; }
    public decimal LargeOutflow { get; set; }
    public decimal MediumInflow { get; set; }
    public decimal MediumOutflow { get; set; }
}

internal class NewsData
{
    public string Title { get; set; } = "";
    public DateTime PublishedAt { get; set; }
}

#endregion
