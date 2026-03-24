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
///   Stage 1: Real-time signals with all indicators stored directly in TradingSignals
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
            // 0. Skip if rules were already generated today (non-empty file)
            if (File.Exists(StrategyConfigPath))
            {
                var fileInfo = new FileInfo(StrategyConfigPath);
                if (fileInfo.LastWriteTimeUtc.Date == DateTime.UtcNow.Date && fileInfo.Length > 100)
                {
                    _logger.LogWarning("Strategy rules already generated today ({LastWrite:HH:mm} UTC, {Size}B). Skipping Bedrock calls.",
                        fileInfo.LastWriteTimeUtc, fileInfo.Length);
                    return;
                }
            }

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

            // 3. Verify signal outcomes (PriceAfter1Min/5Min) from L2 snapshots
            //    Previously ran every 5 min as SignalVerificationJob — now consolidated here
            //    so all signals have outcomes before Bedrock analysis.
            await VerifySignalOutcomesAsync(lookbackDays);

            // 4. For each symbol, prepare analysis and call Bedrock
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

            // 5. Save strategy_rules.json
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
    private record SymbolData(List<string> Rows, string ContextSection, string CsvHeader, int TotalWins, int TotalRows);

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
        // 1. Raw trade data from TradingSignals + L2 book shape from SymbolBookSnapshots
        // ═══════════════════════════════════════════════════════════
        var rawDataSql = @"
            SELECT
                EXTRACT(DOW FROM (ts.""Timestamp"" AT TIME ZONE 'UTC') AT TIME ZONE 'America/New_York')::int AS dow,
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
                CASE WHEN ts.""Price"" > 0 THEN ROUND((ts.""Spread"" / ts.""Price"" * 100)::numeric, 4) ELSE 0 END AS spread_pct,
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
                -- Outcomes: price after signal
                ROUND(ts.""PriceAfter1Min""::numeric, 4) AS price_1m,
                ROUND(ts.""PriceAfter5Min""::numeric, 4) AS price_5m,
                ROUND(ts.""PriceAfter15Min""::numeric, 4) AS price_15m,
                ROUND(ts.""PriceAfter30Min""::numeric, 4) AS price_30m
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
              AND (ts.""Reason"" IS NULL OR ts.""Reason"" NOT LIKE '%BACKFILL%')
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
                // Compute win at 5min for aggregate stats (direction-aware, spread-adjusted)
                var direction = reader.GetString(2); // "BUY" or "SELL"
                var price = D(reader, 3);
                var price5m = D(reader, 37); // price_5m
                var spread = D(reader, 10);
                bool win5m = direction == "BUY"
                    ? (price5m - price) > spread
                    : (price - price5m) > spread;
                if (win5m && price5m > 0) totalWins++;

                // CSV row — compact format to maximize data within token budget
                rows.Add(string.Join(",",
                    reader.GetInt32(0),   // dow (0=Sun, 1=Mon, ..., 5=Fri)
                    reader.GetInt32(1),   // hour_et
                    reader.GetString(2),  // direction
                    D(reader, 3),         // price
                    D(reader, 4),         // score
                    D(reader, 5),         // obi
                    D(reader, 6),         // wobi
                    D(reader, 7),         // pressure_roc
                    D(reader, 8),         // spread_signal
                    D(reader, 9),         // large_order
                    D(reader, 10),        // spread
                    D(reader, 11),        // spread_pct
                    D(reader, 12),        // imbalance
                    D(reader, 13),        // ema9
                    D(reader, 14),        // ema20
                    D(reader, 15),        // rsi14
                    D(reader, 16),        // vwap
                    D(reader, 17),        // vol_ratio
                    D(reader, 18),        // tick_mom
                    D(reader, 19),        // book_depth
                    D(reader, 20),        // bid_wall
                    D(reader, 21),        // ask_wall
                    D(reader, 22),        // bid_sweep
                    D(reader, 23),        // ask_sweep
                    D(reader, 24),        // imb_vel
                    D(reader, 25),        // spread_pctl
                    D(reader, 26),        // b1
                    D(reader, 27),        // b2
                    D(reader, 28),        // b3
                    D(reader, 29),        // b4
                    D(reader, 30),        // b5
                    D(reader, 31),        // a1
                    D(reader, 32),        // a2
                    D(reader, 33),        // a3
                    D(reader, 34),        // a4
                    D(reader, 35),        // a5
                    D(reader, 36),        // price_1m
                    D(reader, 37),        // price_5m
                    D(reader, 38),        // price_15m
                    D(reader, 39)         // price_30m
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
        // 2. Context section (stats, fundamentals, capital flows, news)
        // ═══════════════════════════════════════════════════════════
        var fundamentals = await GetLatestFinancialsAsync(connection, symbolId);
        var capitalFlows = await GetRecentCapitalFlowsAsync(connection, symbolId, lookbackDays);
        var recentNews = await GetRecentNewsAsync(connection, symbolId, lookbackDays);
        var hourlyStats = await GetHourlyStatsAsync(connection, symbolId, cutoff);
        var indicatorStats = await GetIndicatorEffectivenessAsync(connection, symbolId, cutoff);
        var comboStats = await GetIndicatorCombinationsAsync(connection, symbolId, cutoff);
        var recentVsHist = await GetRecentVsHistoricalAsync(connection, symbolId, cutoff);
        var priceAction = await GetRecentPriceActionAsync(connection, symbolId);

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

        if (hourlyStats.Count > 0)
        {
            ctx.AppendLine("HourlyPerformance (ET hour | total | buyWin% | sellWin% | buyAvgPnl | sellAvgPnl):");
            foreach (var h in hourlyStats)
            {
                decimal buyWr = h.BuySignals > 0 ? (decimal)h.BuyWins / h.BuySignals * 100 : 0;
                decimal sellWr = h.SellSignals > 0 ? (decimal)h.SellWins / h.SellSignals * 100 : 0;
                ctx.AppendLine($"  {h.Hour}:00 | {h.TotalSignals} | {buyWr:F0}% | {sellWr:F0}% | ${h.BuyAvgPnl:F2} | ${h.SellAvgPnl:F2}");
            }
        }

        if (indicatorStats.Count > 0)
        {
            ctx.AppendLine("IndicatorEffectiveness (name | highWin% | lowWin% | neutralWin%):");
            foreach (var ind in indicatorStats)
                ctx.AppendLine($"  {ind.Name} | {ind.HighWinRate:F1}% | {ind.LowWinRate:F1}% | {ind.NeutralWinRate:F1}%");
        }

        if (comboStats.Count > 0)
        {
            ctx.AppendLine("IndicatorCombos (name | bothHighWin% | bothLowWin% | samples):");
            foreach (var c in comboStats)
                ctx.AppendLine($"  {c.Name} | {c.BothHighWinRate:F1}% | {c.BothLowWinRate:F1}% | {c.Samples}");
        }

        if (recentVsHist != null)
        {
            ctx.AppendLine($"Recent5d vs Full: buyWin={recentVsHist.Recent5dBuyWinRate:F1}% sellWin={recentVsHist.Recent5dSellWinRate:F1}% spread={recentVsHist.Recent5dAvgSpread:F4} (full avg={recentVsHist.FullAvgSpread:F4})");
        }

        if (priceAction != null)
        {
            ctx.AppendLine($"PriceAction: last=${priceAction.LastClose:F2} 1dChg={priceAction.Change1d:F2}% 5dChg={priceAction.Change5d:F2}% 20dChg={priceAction.Change20d:F2}% avgVol={priceAction.AvgDailyVolume:F0} range20d=${priceAction.Low20d:F2}-${priceAction.High20d:F2}");
        }

        // ═══════════════════════════════════════════════════════════
        // 2b. Aggregate performance summary (computed from rows, not sent as individual outcomes)
        // ═══════════════════════════════════════════════════════════
        {
            // Parse rows to compute aggregate stats — rows are CSV with columns indexed by csvHeader
            // dir=index 2, price=3, hour_et=1, price_5m=37 (but in CSV string, index positions may differ)
            // CSV column indices: 0=dow, 1=hour_et, 2=dir, 3=price, 10=spread, 37=price_5m
            int totalSignals = 0, wins5m = 0;
            decimal totalPnl5m = 0;
            var hourPnl = new Dictionary<int, (int count, decimal pnl)>();

            foreach (var row in rows)
            {
                var cols = row.Split(',');
                if (cols.Length < 38) continue; // need at least through price_5m

                if (!int.TryParse(cols[1], out int hourEt)) continue;
                var dir = cols[2];
                if (!decimal.TryParse(cols[3], out decimal rowPrice) || rowPrice <= 0) continue;
                if (!decimal.TryParse(cols[37], out decimal rowPrice5m) || rowPrice5m <= 0) continue;
                if (!decimal.TryParse(cols[10], out decimal rowSpread)) rowSpread = 0;

                totalSignals++;
                decimal pnl5m = dir == "BUY"
                    ? (rowPrice5m - rowPrice) * 100
                    : (rowPrice - rowPrice5m) * 100;

                totalPnl5m += pnl5m;
                bool isWin = dir == "BUY"
                    ? (rowPrice5m - rowPrice) > rowSpread
                    : (rowPrice - rowPrice5m) > rowSpread;
                if (isWin) wins5m++;

                if (!hourPnl.ContainsKey(hourEt))
                    hourPnl[hourEt] = (0, 0);
                var (c, p) = hourPnl[hourEt];
                hourPnl[hourEt] = (c + 1, p + pnl5m);
            }

            if (totalSignals > 0)
            {
                decimal winRate5m = (decimal)wins5m / totalSignals;
                decimal avgPnl5m = totalPnl5m / totalSignals;
                var bestHour = hourPnl.Count > 0 ? hourPnl.OrderByDescending(h => h.Value.pnl / Math.Max(h.Value.count, 1)).First() : default;
                var worstHour = hourPnl.Count > 0 ? hourPnl.OrderBy(h => h.Value.pnl / Math.Max(h.Value.count, 1)).First() : default;

                ctx.AppendLine();
                ctx.AppendLine("PERFORMANCE SUMMARY (aggregate, not per-signal):");
                ctx.AppendLine($"- Overall signal count: {totalSignals}");
                ctx.AppendLine($"- Win rate at 5min: {winRate5m:P1}");
                ctx.AppendLine($"- Average P&L at 5min: {avgPnl5m:F2}");
                if (hourPnl.Count > 0)
                {
                    ctx.AppendLine($"- Best performing hour (ET): {bestHour.Key}:00 (avg P&L ${bestHour.Value.pnl / Math.Max(bestHour.Value.count, 1):F2}, n={bestHour.Value.count})");
                    ctx.AppendLine($"- Worst performing hour (ET): {worstHour.Key}:00 (avg P&L ${worstHour.Value.pnl / Math.Max(worstHour.Value.count, 1):F2}, n={worstHour.Value.count})");
                }
            }
        }

        // ═══════════════════════════════════════════════════════════
        // 3. Previous rules feedback — help Bedrock iterate instead of starting from scratch
        // ═══════════════════════════════════════════════════════════
        try
        {
            if (File.Exists(StrategyConfigPath))
            {
                var prevJson = await File.ReadAllTextAsync(StrategyConfigPath);
                var prevConfig = System.Text.Json.JsonSerializer.Deserialize<StrategyConfig>(prevJson);
                if (prevConfig != null && prevConfig.Symbols.TryGetValue(ticker, out var prevSymbol) && prevSymbol.Rules.Count > 0)
                {
                    ctx.AppendLine($"PreviousRules (generated {prevConfig.GeneratedAt:yyyy-MM-dd HH:mm} UTC, {prevSymbol.Rules.Count} rules):");
                    foreach (var rule in prevSymbol.Rules.Take(8))
                    {
                        ctx.AppendLine($"  {rule.Id}: {rule.Name} | {rule.Direction} hours=[{string.Join(",", rule.Hours)}] " +
                                       $"conf={rule.Confidence:F2} samples={rule.SampleSize} hold={rule.HoldSeconds}s stop={rule.StopLoss:F2} " +
                                       $"expectedPnl=${rule.ExpectedPnlPer100Shares:F2}/100sh");
                    }
                    ctx.AppendLine("Use these as a starting point. Keep rules that still show positive P&L in the new data. Drop or modify rules that no longer work. Add new rules for patterns the previous set missed.");
                }
            }
        }
        catch { /* non-fatal — first run or bad file */ }

        string csvHeader = "dow,hour_et,dir,price,score,obi,wobi,pressure_roc,spread_signal,large_order,spread,spread_pct,imbalance,ema9,ema20,rsi14,vwap,vol_ratio,tick_mom,book_depth,bid_wall,ask_wall,bid_sweep,ask_sweep,imb_vel,spread_pctl,b1,b2,b3,b4,b5,a1,a2,a3,a4,a5,price_1m,price_5m,price_15m,price_30m";

        return new SymbolData(rows, ctx.ToString(), csvHeader, totalWins, totalRows);
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
        System.Data.Common.DbConnection connection, string symbolId, DateTime cutoff)
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
        System.Data.Common.DbConnection connection, string symbolId, DateTime cutoff)
    {
        // All indicators are now stored directly on TradingSignals — no TickSnapshots JOIN needed
        var results = new List<IndicatorStat>();

        // Core L2 indicators (range -1 to +1)
        var coreIndicators = new (string Column, decimal HighThresh, decimal LowThresh)[] {
            ("ObiSmoothed", 0.3m, -0.3m),
            ("Wobi", 0.3m, -0.3m),
            ("PressureRoc", 0.3m, -0.3m),
            ("LargeOrderSignal", 0.3m, -0.3m),
            ("SpreadSignal", 0.3m, -0.3m),
            ("BookDepthRatio", 0.8m, 0.2m),
            ("BidWallSize", 3.0m, 1.0m),
            ("AskWallSize", 3.0m, 1.0m),
            ("ImbalanceVelocity", 0.1m, -0.05m),
            ("SpreadPercentile", 0.8m, 0.2m),
            ("TickMomentum", 0.3m, -0.3m),
            ("VolumeRatio", 1.5m, 0.5m),
        };

        foreach (var (col, highThresh, lowThresh) in coreIndicators)
        {
            var sql = $@"
                SELECT
                    COUNT(*) FILTER (WHERE ts.""{col}"" > @p2 AND ts.""PriceAfter1Min"" IS NOT NULL
                        AND ((ts.""Type"" = 1 AND ts.""PriceAfter1Min"" > ts.""Price"")
                          OR (ts.""Type"" = 2 AND ts.""PriceAfter1Min"" < ts.""Price""))) AS ""HighWins"",
                    COUNT(*) FILTER (WHERE ts.""{col}"" > @p2 AND ts.""PriceAfter1Min"" IS NOT NULL) AS ""HighTotal"",
                    COUNT(*) FILTER (WHERE ts.""{col}"" < @p3 AND ts.""PriceAfter1Min"" IS NOT NULL
                        AND ((ts.""Type"" = 1 AND ts.""PriceAfter1Min"" > ts.""Price"")
                          OR (ts.""Type"" = 2 AND ts.""PriceAfter1Min"" < ts.""Price""))) AS ""LowWins"",
                    COUNT(*) FILTER (WHERE ts.""{col}"" < @p3 AND ts.""PriceAfter1Min"" IS NOT NULL) AS ""LowTotal"",
                    COUNT(*) FILTER (WHERE ts.""{col}"" BETWEEN @p3 AND @p2 AND ts.""PriceAfter1Min"" IS NOT NULL
                        AND ((ts.""Type"" = 1 AND ts.""PriceAfter1Min"" > ts.""Price"")
                          OR (ts.""Type"" = 2 AND ts.""PriceAfter1Min"" < ts.""Price""))) AS ""NeutralWins"",
                    COUNT(*) FILTER (WHERE ts.""{col}"" BETWEEN @p3 AND @p2 AND ts.""PriceAfter1Min"" IS NOT NULL) AS ""NeutralTotal""
                FROM ""TradingSignals"" ts
                WHERE ts.""SymbolId"" = @p0 AND ts.""Timestamp"" > @p1";

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
                int neutralWins = reader.GetInt32(4), neutralTotal = reader.GetInt32(5);

                if (highTotal + lowTotal >= 10)
                {
                    results.Add(new IndicatorStat
                    {
                        Name = col,
                        HighWinRate = highTotal > 0 ? (decimal)highWins / highTotal * 100 : 50,
                        LowWinRate = lowTotal > 0 ? (decimal)lowWins / lowTotal * 100 : 50,
                        NeutralWinRate = neutralTotal > 0 ? (decimal)neutralWins / neutralTotal * 100 : 50,
                    });
                }
            }
        }

        // RSI zones + EMA trend + VWAP position — all from TradingSignals directly
        var rsiSql = @"
            SELECT
                COUNT(*) FILTER (WHERE ts.""Rsi14"" > 70
                    AND ((ts.""Type"" = 1 AND ts.""PriceAfter1Min"" > ts.""Price"")
                      OR (ts.""Type"" = 2 AND ts.""PriceAfter1Min"" < ts.""Price""))) AS ""OverboughtWins"",
                COUNT(*) FILTER (WHERE ts.""Rsi14"" > 70 AND ts.""PriceAfter1Min"" IS NOT NULL) AS ""OverboughtTotal"",
                COUNT(*) FILTER (WHERE ts.""Rsi14"" < 30
                    AND ((ts.""Type"" = 1 AND ts.""PriceAfter1Min"" > ts.""Price"")
                      OR (ts.""Type"" = 2 AND ts.""PriceAfter1Min"" < ts.""Price""))) AS ""OversoldWins"",
                COUNT(*) FILTER (WHERE ts.""Rsi14"" < 30 AND ts.""PriceAfter1Min"" IS NOT NULL) AS ""OversoldTotal"",
                COUNT(*) FILTER (WHERE ts.""Ema9"" > ts.""Ema20"" AND ts.""Ema9"" > 0
                    AND ((ts.""Type"" = 1 AND ts.""PriceAfter1Min"" > ts.""Price"")
                      OR (ts.""Type"" = 2 AND ts.""PriceAfter1Min"" < ts.""Price""))) AS ""UptrendWins"",
                COUNT(*) FILTER (WHERE ts.""Ema9"" > ts.""Ema20"" AND ts.""Ema9"" > 0 AND ts.""PriceAfter1Min"" IS NOT NULL) AS ""UptrendTotal"",
                COUNT(*) FILTER (WHERE ts.""Ema9"" < ts.""Ema20"" AND ts.""Ema9"" > 0
                    AND ((ts.""Type"" = 1 AND ts.""PriceAfter1Min"" > ts.""Price"")
                      OR (ts.""Type"" = 2 AND ts.""PriceAfter1Min"" < ts.""Price""))) AS ""DowntrendWins"",
                COUNT(*) FILTER (WHERE ts.""Ema9"" < ts.""Ema20"" AND ts.""Ema9"" > 0 AND ts.""PriceAfter1Min"" IS NOT NULL) AS ""DowntrendTotal"",
                COUNT(*) FILTER (WHERE ts.""Vwap"" > 0 AND ts.""Price"" > ts.""Vwap""
                    AND ((ts.""Type"" = 1 AND ts.""PriceAfter1Min"" > ts.""Price"")
                      OR (ts.""Type"" = 2 AND ts.""PriceAfter1Min"" < ts.""Price""))) AS ""AboveVwapWins"",
                COUNT(*) FILTER (WHERE ts.""Vwap"" > 0 AND ts.""Price"" > ts.""Vwap"" AND ts.""PriceAfter1Min"" IS NOT NULL) AS ""AboveVwapTotal"",
                COUNT(*) FILTER (WHERE ts.""Vwap"" > 0 AND ts.""Price"" <= ts.""Vwap""
                    AND ((ts.""Type"" = 1 AND ts.""PriceAfter1Min"" > ts.""Price"")
                      OR (ts.""Type"" = 2 AND ts.""PriceAfter1Min"" < ts.""Price""))) AS ""BelowVwapWins"",
                COUNT(*) FILTER (WHERE ts.""Vwap"" > 0 AND ts.""Price"" <= ts.""Vwap"" AND ts.""PriceAfter1Min"" IS NOT NULL) AS ""BelowVwapTotal""
            FROM ""TradingSignals"" ts
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

        return results;
    }

    private async Task<List<ComboStat>> GetIndicatorCombinationsAsync(
        System.Data.Common.DbConnection connection, string symbolId, DateTime cutoff)
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
        System.Data.Common.DbConnection connection, string symbolId, DateTime cutoff)
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

        var systemPrompt = @"You are a quantitative trading strategy optimizer for 1-10 minute L2-based trades. You receive RAW TRADE DATA — every signal with all indicator values and the actual outcome (P&L in dollars, spread-adjusted win/loss).

Your job: analyze the raw data to discover patterns that predict profitable short-duration trades (1-10 min), then output conditional rules as JSON.

## How the trading system works
- Signals are generated from L2 order book microstructure analysis
- 6 weighted L2/tick indicators produce a composite score each snapshot
- YOUR rules are evaluated first — if a rule matches, it fires immediately (bypasses scoring)
- A PositionMonitor checks exits every 5s (score decay, trailing stops, time gates)
- Positions can be held up to 2x the holdSeconds you specify (e.g., holdSeconds=300 → max 600s)
- Commission: $0 (both Webull and Questrade have zero-commission US equity trades).
- The real cost per trade is the SPREAD — you must cross it on entry AND exit. Check the spread_pct column.

## CSV column definitions
dow: day of week (1=Mon, 2=Tue, ..., 5=Fri) — Mon open and Fri close trade differently
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
spread_pct: spread as % of price (0.01 = 1 basis point). THIS IS YOUR COST — trades need to move MORE than this to profit.
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
b1-b5: top 5 bid level sizes (shares) from order book at signal time
a1-a5: top 5 ask level sizes (shares) from order book at signal time
  - b1 >> a1 means buyers are front-loading → bullish pressure
  - a1 very large = ask wall (resistance), b1 very large = bid wall (support)
  - All zeros means no L2 snapshot was available at signal time
price_1m: actual midprice 1 minute after signal (early momentum check)
price_5m: actual midprice 5 minutes after signal (PRIMARY outcome — compute P&L as (price_5m - price) * 100 for BUY, (price - price_5m) * 100 for SELL)
price_15m: actual midprice 15 minutes after signal
price_30m: actual midprice 30 minutes after signal

## IMPORTANT: Reading the outcome columns
- price_5m is the PRIMARY outcome for 1-10 minute trade evaluation. Use it to compute P&L and set expectedPnlPer100Shares and stopLoss.
- Compare price_1m vs price_5m to decide holdSeconds: if 5m is more favorable, use longer holds (300-600s). If 1m captures most of the move, use shorter holds (60-180s).

## IMPORTANT: Primary vs optional conditions
- L2 conditions (OBI, WOBI, etc.) should only be used as OPTIONAL refinements, not primary conditions. Primary conditions should be trend (trendDirection), VWAP position (aboveVwap), RSI (rsiRange), and volume (minVolumeRatio).

## Your task — WALK-FORWARD VALIDATION REQUIRED
The data is sorted most-recent-first. You MUST use walk-forward validation:
1. Treat the OLDER 75% of rows as your TRAINING set — discover patterns here
2. Treat the NEWER 25% of rows as your VALIDATION set — verify patterns hold here
3. A rule is only valid if it is profitable in BOTH the training AND validation sets

Steps:
1. Study winning vs losing trades in the training set — what indicator values differ? Use pnl columns for magnitude.
2. Find patterns: day-of-week + time-of-day edges, indicator thresholds, multi-condition combos
3. CRITICAL: Check spread_pct for each pattern. Rules that trade when spread_pct > 0.05% need MUCH stronger signals.
4. Look for interactions (e.g., OBI matters more when spread is tight, momentum matters more with high volume)
5. VALIDATE each candidate rule on the newer 25% — reject rules that don't generalize
6. Output 3-8 high-confidence rules that pass validation
7. Each rule MUST have confidence >= 0.55, sample >= 30, positive average pnl_5m IN BOTH train and validation sets
8. Set holdSeconds: 60-600 (1-10 minute holds matching L2 signal decay)
9. Set stopLoss as ATR multiplier (0.5-3.0) based on typical adverse pnl for losers in that pattern
10. Set maxPositionDollars based on price and spread_pct (lower for wide-spread stocks)
11. Set maxDailyTrades to 2-4 per symbol — quality over quantity

CRITICAL: Rules that only work on historical data but fail on recent data are WORSE than no rules — they waste commission. Be conservative. A 55% spread-adjusted win rate that holds out-of-sample beats a 70% win rate that only works in-sample.";

        var jsonTemplate = $@"{{
  ""tickerId"": {tickerId},
  ""overallWinRate"": 0.52,
  ""rules"": [
    {{
      ""id"": ""{ticker}-001"",
      ""name"": ""descriptive name of the pattern"",
      ""hours"": [10, 11, 14],
      ""direction"": ""BUY"",
      ""conditions"": {{
        ""minObi"": 0.2, ""maxObi"": null,
        ""minImbalanceVelocity"": null, ""maxImbalanceVelocity"": null,
        ""minBidWallSize"": null, ""minAskWallSize"": null,
        ""minBookDepthRatio"": null, ""maxBookDepthRatio"": null,
        ""minBidSweepCost"": null, ""minAskSweepCost"": null,
        ""minSpreadPercentile"": null, ""maxSpreadPercentile"": 0.7,
        ""trendDirection"": 1,
        ""minTickMomentum"": 0.1, ""maxTickMomentum"": null,
        ""rsiRange"": [30, 70],
        ""minVolumeRatio"": 1.0,
        ""aboveVwap"": true
      }},
      ""confidence"": 0.60,
      ""expectedPnlPer100Shares"": 8.50,
      ""sampleSize"": 45,
      ""holdSeconds"": 300,
      ""stopLoss"": 2.0
    }}
  ],
  ""disabledHours"": [12, 13],
  ""maxDailyTrades"": 3,
  ""maxPositionShares"": 500,
  ""maxPositionDollars"": 25000
}}

IMPORTANT: The example above is just a template showing the schema. Replace ALL values with your actual analysis results. Use null for conditions you don't want to constrain. direction must be exactly ""BUY"" or ""SELL"". holdSeconds range: 60-600 (1-10 minute holds). stopLoss range: 0.5-5.0 (ATR multiplier). maxDailyTrades: 2-4 per symbol.";

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

            // Walk-forward split info for Bedrock: data is sorted DESC (most recent first)
            // First 25% of rows = VALIDATION set (newest), remaining 75% = TRAINING set (older)
            int valSplitRow = (int)(rows.Count * 0.25);

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
                chunkPrompt.AppendLine($"## Walk-forward split: rows 1-{valSplitRow} are VALIDATION (newest 25%), rows {valSplitRow + 1}-{rows.Count} are TRAINING (oldest 75%)");
                chunkPrompt.AppendLine(csvHeader);
                foreach (var row in chunkRows)
                    chunkPrompt.AppendLine(row);
                chunkPrompt.AppendLine();
                chunkPrompt.AppendLine($"## Instructions");
                chunkPrompt.AppendLine($"Analyze this batch of {ticker} trades. The data is sorted newest-first.");
                chunkPrompt.AppendLine($"Use price_5m as the primary outcome (compute P&L from direction and price). price_1m is an early momentum check.");
                chunkPrompt.AppendLine($"Discover patterns in TRAINING rows (older 75%), verify they hold in VALIDATION rows (newest 25%).");
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
                        MaxTokens = 8192,
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

            // Local backtesting: verify rules on validation data (newest 25%) before deployment
            var backtested = BacktestRulesLocally(ticker, deduped, data.Rows, valSplitRow);

            if (backtested.Count == 0)
            {
                _logger.LogWarning("{Ticker}: all {Count} rules failed local backtesting — no rules deployed", ticker, deduped.Count);
                return null;
            }

            return new SymbolStrategy
            {
                TickerId = tickerId,
                OverallWinRate = overallWinRate,
                Rules = backtested,
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

            SymbolStrategy? strategy;
            try
            {
                strategy = JsonSerializer.Deserialize<SymbolStrategy>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
            }
            catch (JsonException truncEx) when (json.Contains("\"rules\""))
            {
                // Bedrock response was likely truncated (hit max_tokens).
                // Salvage valid rules by closing the truncated JSON.
                _logger.LogWarning("JSON truncated for {Ticker}, attempting salvage: {Msg}",
                    ticker, truncEx.Message);

                // Find the last complete rule object (last '},' or '}]')
                int lastComplete = json.LastIndexOf("},");
                if (lastComplete < 0) lastComplete = json.LastIndexOf("}]");
                if (lastComplete > 0)
                {
                    var salvaged = json[..(lastComplete + 1)] + "]}";
                    strategy = JsonSerializer.Deserialize<SymbolStrategy>(salvaged, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                    });
                    _logger.LogInformation("Salvaged {Count} rules from truncated response for {Ticker}",
                        strategy?.Rules.Count ?? 0, ticker);
                }
                else
                {
                    throw; // Can't salvage, re-throw original
                }
            }

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
                .Where(r => r.HoldSeconds is >= 60 and <= 600)
                .Where(r => r.StopLoss is >= 0.05m and <= 5.0m)
                .ToList();

            strategy.Rules = validRules;
            strategy.MaxDailyTrades = Math.Min(strategy.MaxDailyTrades, 5);

            _logger.LogInformation("{Ticker}: {Valid}/{Total} rules passed validation (maxDailyTrades capped to {MaxDaily})",
                ticker, validRules.Count, strategy.Rules.Count, strategy.MaxDailyTrades);

            return validRules.Count > 0 ? strategy : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Bedrock response for {Ticker}: {Response}",
                ticker, responseText[..Math.Min(200, responseText.Length)]);
            return null;
        }
    }

    /// <summary>
    /// Local backtesting of Bedrock-generated rules against validation data (newest 25%).
    /// Checks direction + hour + confidence filtering on the validation rows.
    /// Rules must have matchCount >= 5 AND positive total P&L at 5min to be kept.
    /// </summary>
    private List<StrategyRule> BacktestRulesLocally(string ticker, List<StrategyRule> candidateRules, List<string> allRows, int valSplitRow)
    {
        // Validation rows are the first valSplitRow rows (data is sorted newest-first)
        var validationRows = allRows.Take(valSplitRow).ToList();
        if (validationRows.Count < 10)
        {
            _logger.LogWarning("{Ticker}: only {Count} validation rows, skipping local backtest (keeping all rules)",
                ticker, validationRows.Count);
            return candidateRules;
        }

        var kept = new List<StrategyRule>();

        foreach (var rule in candidateRules)
        {
            int matchCount = 0;
            decimal totalPnl = 0;

            foreach (var row in validationRows)
            {
                var cols = row.Split(',');
                if (cols.Length < 38) continue; // need at least through price_5m

                // Parse key fields for matching
                if (!int.TryParse(cols[1], out int hourEt)) continue;
                var dir = cols[2]; // "BUY" or "SELL"
                if (!decimal.TryParse(cols[3], out decimal price) || price <= 0) continue;
                if (!decimal.TryParse(cols[4], out decimal score)) continue;
                if (!decimal.TryParse(cols[37], out decimal price5m) || price5m <= 0) continue;

                // Direction must match
                if (!string.Equals(dir, rule.Direction, StringComparison.OrdinalIgnoreCase)) continue;

                // Hour must be in rule's allowed hours (if specified)
                if (rule.Hours.Count > 0 && !rule.Hours.Contains(hourEt)) continue;

                // Score must meet rule's confidence threshold
                if (Math.Abs(score) < rule.Confidence) continue;

                // Additional condition checks where data is available
                bool conditionsMet = true;

                // RSI range check
                if (rule.Conditions.RsiRange is { Length: >= 2 })
                {
                    if (decimal.TryParse(cols[15], out decimal rsi))
                    {
                        if (rule.Conditions.RsiRange![0].HasValue && rsi < rule.Conditions.RsiRange[0]!.Value) conditionsMet = false;
                        if (rule.Conditions.RsiRange![1].HasValue && rsi > rule.Conditions.RsiRange[1]!.Value) conditionsMet = false;
                    }
                }

                // Volume ratio check
                if (rule.Conditions.MinVolumeRatio.HasValue)
                {
                    if (decimal.TryParse(cols[17], out decimal volRatio))
                    {
                        if (volRatio < rule.Conditions.MinVolumeRatio.Value) conditionsMet = false;
                    }
                }

                // VWAP position check
                if (rule.Conditions.AboveVwap.HasValue)
                {
                    if (decimal.TryParse(cols[16], out decimal vwap) && vwap > 0)
                    {
                        bool isAbove = price > vwap;
                        if (rule.Conditions.AboveVwap.Value != isAbove) conditionsMet = false;
                    }
                }

                // Trend direction check (EMA9 vs EMA20)
                if (rule.Conditions.TrendDirection.HasValue)
                {
                    if (decimal.TryParse(cols[13], out decimal ema9) && decimal.TryParse(cols[14], out decimal ema20)
                        && ema9 > 0 && ema20 > 0)
                    {
                        int trend = ema9 > ema20 ? 1 : -1;
                        if (trend != rule.Conditions.TrendDirection.Value) conditionsMet = false;
                    }
                }

                // OBI check
                if (rule.Conditions.MinObi.HasValue || rule.Conditions.MaxObi.HasValue)
                {
                    if (decimal.TryParse(cols[5], out decimal obi))
                    {
                        if (rule.Conditions.MinObi.HasValue && obi < rule.Conditions.MinObi.Value) conditionsMet = false;
                        if (rule.Conditions.MaxObi.HasValue && obi > rule.Conditions.MaxObi.Value) conditionsMet = false;
                    }
                }

                if (!conditionsMet) continue;

                matchCount++;

                // Compute P&L at 5min (matching hold range 60-600s)
                decimal pnl = dir == "BUY"
                    ? (price5m - price) * 100
                    : (price - price5m) * 100;
                totalPnl += pnl;
            }

            if (matchCount >= 5 && totalPnl > 0)
            {
                _logger.LogInformation("{Ticker}: rule {RuleId} passed backtest: {Matches} matches, P&L=${Pnl:F2}",
                    ticker, rule.Id, matchCount, totalPnl);
                kept.Add(rule);
            }
            else
            {
                _logger.LogWarning("{Ticker}: rule {RuleId} ({Name}) REJECTED by backtest: {Matches} matches, P&L=${Pnl:F2} (need >=5 matches and positive P&L)",
                    ticker, rule.Id, rule.Name, matchCount, totalPnl);
            }
        }

        _logger.LogWarning("{Ticker}: local backtest kept {Kept}/{Total} rules",
            ticker, kept.Count, candidateRules.Count);

        return kept;
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
    /// Retention: SymbolBookSnapshots=20 days (for Swin model training), rest=forever
    /// </summary>
    public async Task CleanupOldDataAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TradingPilotDbContext>();

            // Delete in batches of 5000 to avoid long table locks
            // Retention: 20 days (for Swin vision model training — needs ~340K samples)
            var bookCutoff = DateTime.UtcNow.AddDays(-20);
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

            _logger.LogWarning("Cleanup: deleted {BookCount} SymbolBookSnapshots (>20d)", bookDeleted);
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

    private async Task<PriceActionData?> GetRecentPriceActionAsync(
        System.Data.Common.DbConnection connection, string symbolId)
    {
        try
        {
            var sql = @"
                WITH daily AS (
                    SELECT ""Close"", ""Volume"", ""Timestamp"",
                           ROW_NUMBER() OVER (ORDER BY ""Timestamp"" DESC) AS rn
                    FROM ""SymbolBars""
                    WHERE ""SymbolId"" = @p0 AND ""Timeframe"" = 0
                    ORDER BY ""Timestamp"" DESC LIMIT 20
                )
                SELECT
                    (SELECT ""Close"" FROM daily WHERE rn = 1) AS last_close,
                    (SELECT ""Close"" FROM daily WHERE rn = 2) AS prev_close,
                    (SELECT ""Close"" FROM daily WHERE rn = 5) AS close_5d,
                    (SELECT ""Close"" FROM daily WHERE rn = 20) AS close_20d,
                    (SELECT AVG(""Volume"") FROM daily) AS avg_vol,
                    (SELECT MIN(""Close"") FROM daily) AS low_20d,
                    (SELECT MAX(""Close"") FROM daily) AS high_20d";

            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            AddParameter(cmd, "@p0", symbolId);
            cmd.CommandTimeout = 30;

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync() && !reader.IsDBNull(0))
            {
                decimal last = reader.GetDecimal(0);
                decimal prev = reader.IsDBNull(1) ? last : reader.GetDecimal(1);
                decimal close5d = reader.IsDBNull(2) ? last : reader.GetDecimal(2);
                decimal close20d = reader.IsDBNull(3) ? last : reader.GetDecimal(3);
                return new PriceActionData
                {
                    LastClose = last,
                    Change1d = prev > 0 ? (last - prev) / prev * 100 : 0,
                    Change5d = close5d > 0 ? (last - close5d) / close5d * 100 : 0,
                    Change20d = close20d > 0 ? (last - close20d) / close20d * 100 : 0,
                    AvgDailyVolume = reader.IsDBNull(4) ? 0 : reader.GetDecimal(4),
                    Low20d = reader.IsDBNull(5) ? 0 : reader.GetDecimal(5),
                    High20d = reader.IsDBNull(6) ? 0 : reader.GetDecimal(6),
                };
            }
        }
        catch { }
        return null;
    }

    #endregion

    #region Signal Outcome Verification

    /// <summary>
    /// Fills in PriceAfter1Min/5Min for unverified TradingSignals by looking up
    /// the nearest SymbolBookSnapshot at the target time offset.
    /// Absorbed from the former SignalVerificationJob — now runs once nightly
    /// before Bedrock analysis instead of every 5 minutes.
    /// </summary>
    private async Task VerifySignalOutcomesAsync(int lookbackDays)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TradingPilotDbContext>();

        var lookbackCutoff = DateTime.UtcNow.AddDays(-lookbackDays);
        int updated = await dbContext.Database.ExecuteSqlRawAsync(@"
            UPDATE ""TradingSignals"" ts
            SET
                ""PriceAfter1Min"" = COALESCE(ts.""PriceAfter1Min"", (
                    SELECT bs.""MidPrice"" FROM ""SymbolBookSnapshots"" bs
                    WHERE bs.""SymbolId"" = ts.""SymbolId""
                      AND bs.""Timestamp"" BETWEEN ts.""Timestamp"" + INTERVAL '55 seconds'
                                                AND ts.""Timestamp"" + INTERVAL '65 seconds'
                    ORDER BY ABS(EXTRACT(EPOCH FROM bs.""Timestamp"" - (ts.""Timestamp"" + INTERVAL '60 seconds')))
                    LIMIT 1
                )),
                ""PriceAfter5Min"" = COALESCE(ts.""PriceAfter5Min"", (
                    SELECT bs.""MidPrice"" FROM ""SymbolBookSnapshots"" bs
                    WHERE bs.""SymbolId"" = ts.""SymbolId""
                      AND bs.""Timestamp"" BETWEEN ts.""Timestamp"" + INTERVAL '295 seconds'
                                                AND ts.""Timestamp"" + INTERVAL '305 seconds'
                    ORDER BY ABS(EXTRACT(EPOCH FROM bs.""Timestamp"" - (ts.""Timestamp"" + INTERVAL '300 seconds')))
                    LIMIT 1
                )),
                ""PriceAfter15Min"" = COALESCE(ts.""PriceAfter15Min"", (
                    SELECT bs.""MidPrice"" FROM ""SymbolBookSnapshots"" bs
                    WHERE bs.""SymbolId"" = ts.""SymbolId""
                      AND bs.""Timestamp"" BETWEEN ts.""Timestamp"" + INTERVAL '895 seconds'
                                                AND ts.""Timestamp"" + INTERVAL '905 seconds'
                    ORDER BY ABS(EXTRACT(EPOCH FROM bs.""Timestamp"" - (ts.""Timestamp"" + INTERVAL '900 seconds')))
                    LIMIT 1
                ), (
                    SELECT sb.""Close"" FROM ""SymbolBars"" sb
                    WHERE sb.""SymbolId"" = ts.""SymbolId""
                      AND sb.""Timestamp"" BETWEEN ts.""Timestamp"" + INTERVAL '870 seconds'
                                                AND ts.""Timestamp"" + INTERVAL '930 seconds'
                    ORDER BY ABS(EXTRACT(EPOCH FROM sb.""Timestamp"" - (ts.""Timestamp"" + INTERVAL '900 seconds')))
                    LIMIT 1
                )),
                ""PriceAfter30Min"" = COALESCE(ts.""PriceAfter30Min"", (
                    SELECT bs.""MidPrice"" FROM ""SymbolBookSnapshots"" bs
                    WHERE bs.""SymbolId"" = ts.""SymbolId""
                      AND bs.""Timestamp"" BETWEEN ts.""Timestamp"" + INTERVAL '1795 seconds'
                                                AND ts.""Timestamp"" + INTERVAL '1805 seconds'
                    ORDER BY ABS(EXTRACT(EPOCH FROM bs.""Timestamp"" - (ts.""Timestamp"" + INTERVAL '1800 seconds')))
                    LIMIT 1
                ), (
                    SELECT sb.""Close"" FROM ""SymbolBars"" sb
                    WHERE sb.""SymbolId"" = ts.""SymbolId""
                      AND sb.""Timestamp"" BETWEEN ts.""Timestamp"" + INTERVAL '1770 seconds'
                                                AND ts.""Timestamp"" + INTERVAL '1830 seconds'
                    ORDER BY ABS(EXTRACT(EPOCH FROM sb.""Timestamp"" - (ts.""Timestamp"" + INTERVAL '1800 seconds')))
                    LIMIT 1
                )),
                ""VerifiedAt"" = NOW()
            WHERE ts.""VerifiedAt"" IS NULL
              AND ts.""Timestamp"" < NOW() - INTERVAL '31 minutes'
              AND ts.""Timestamp"" > {0}", lookbackCutoff);

        if (updated > 0)
        {
            await dbContext.Database.ExecuteSqlRawAsync(@"
                UPDATE ""TradingSignals""
                SET ""WasCorrect1Min"" = CASE
                    WHEN ""PriceAfter1Min"" IS NOT NULL AND ""Type"" = 1 THEN ""PriceAfter1Min"" > ""Price""
                    WHEN ""PriceAfter1Min"" IS NOT NULL AND ""Type"" = 2 THEN ""PriceAfter1Min"" < ""Price""
                    ELSE NULL END,
                    ""WasCorrect5Min"" = CASE
                    WHEN ""PriceAfter5Min"" IS NOT NULL AND ""Type"" = 1 THEN ""PriceAfter5Min"" > ""Price""
                    WHEN ""PriceAfter5Min"" IS NOT NULL AND ""Type"" = 2 THEN ""PriceAfter5Min"" < ""Price""
                    ELSE NULL END,
                    ""WasCorrect15Min"" = CASE
                    WHEN ""PriceAfter15Min"" IS NOT NULL AND ""Type"" = 1 THEN ""PriceAfter15Min"" > ""Price""
                    WHEN ""PriceAfter15Min"" IS NOT NULL AND ""Type"" = 2 THEN ""PriceAfter15Min"" < ""Price""
                    ELSE NULL END,
                    ""WasCorrect30Min"" = CASE
                    WHEN ""PriceAfter30Min"" IS NOT NULL AND ""Type"" = 1 THEN ""PriceAfter30Min"" > ""Price""
                    WHEN ""PriceAfter30Min"" IS NOT NULL AND ""Type"" = 2 THEN ""PriceAfter30Min"" < ""Price""
                    ELSE NULL END
                WHERE ""VerifiedAt"" IS NOT NULL
                  AND ""WasCorrect1Min"" IS NULL
                  AND ""PriceAfter1Min"" IS NOT NULL");

            _logger.LogWarning("Signal verification: updated {Count} signals with price outcomes", updated);
        }
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

internal class PriceActionData
{
    public decimal LastClose { get; set; }
    public decimal Change1d { get; set; }
    public decimal Change5d { get; set; }
    public decimal Change20d { get; set; }
    public decimal AvgDailyVolume { get; set; }
    public decimal Low20d { get; set; }
    public decimal High20d { get; set; }
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
