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

            // 2. Backfill any missing TickSnapshots + TradingSignals from bar data
            //    This fills gaps from periods where MQTT/app was down during the day.
            //    Safe to run here — market is closed, no race with live data.
            foreach (var symbol in symbols)
            {
                try
                {
                    await BackfillTickSnapshotsFromBarsAsync(symbol.TickerId, symbol.Ticker, symbol.Ticker, lookbackDays);
                    await BackfillTradingSignalsFromSnapshotsAsync(symbol.TickerId, symbol.Ticker, symbol.Ticker, lookbackDays);
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
                    var analysis = await PrepareSymbolAnalysisAsync(symbol.TickerId, symbol.Ticker, symbol.Ticker, lookbackDays);
                    if (analysis == null)
                    {
                        _logger.LogWarning("Insufficient data for {Ticker}, skipping", symbol.Ticker);
                        continue;
                    }

                    var symbolStrategy = await CallBedrockAsync(symbol.TickerId, symbol.Ticker, analysis);
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

    private async Task<string?> PrepareSymbolAnalysisAsync(long tickerId, string ticker, string symbolId, int lookbackDays)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TradingPilotDbContext>();

        var cutoff = DateTime.UtcNow.AddDays(-lookbackDays);

        // Open connection once and keep it alive for all queries
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync();

        try
        {
        // Query: per-hour performance
        var hourlyStats = await GetHourlyStatsAsync(connection, symbolId, tickerId, cutoff);

        // Query: indicator effectiveness
        var indicatorStats = await GetIndicatorEffectivenessAsync(connection, symbolId, tickerId, cutoff);

        // Query: recent vs historical
        var recentStats = await GetRecentVsHistoricalAsync(connection, symbolId, tickerId, cutoff);

        // Query: combination stats
        var comboStats = await GetIndicatorCombinationsAsync(connection, symbolId, tickerId, cutoff);

        // Query: fundamental + sentiment context
        var fundamentals = await GetLatestFinancialsAsync(connection, symbolId);
        var capitalFlows = await GetRecentCapitalFlowsAsync(connection, symbolId, lookbackDays);
        var recentNews = await GetRecentNewsAsync(connection, symbolId, lookbackDays);

        int totalSignals = hourlyStats.Sum(h => h.TotalSignals);
        if (totalSignals < 100)
        {
            _logger.LogInformation("{Ticker}: only {Count} signals, insufficient for AI optimization", ticker, totalSignals);
            return null;
        }

        // Build prompt
        var sb = new StringBuilder();
        sb.AppendLine($"## Performance Summary for {ticker} (tickerId={tickerId}, last {lookbackDays} trading days)");
        sb.AppendLine($"- Total signals: {totalSignals:N0}");

        int buySignals = hourlyStats.Sum(h => h.BuySignals);
        int sellSignals = hourlyStats.Sum(h => h.SellSignals);
        int buyWins = hourlyStats.Sum(h => h.BuyWins);
        int sellWins = hourlyStats.Sum(h => h.SellWins);
        decimal buyWinRate = buySignals > 0 ? (decimal)buyWins / buySignals : 0;
        decimal sellWinRate = sellSignals > 0 ? (decimal)sellWins / sellSignals : 0;

        sb.AppendLine($"- BUY signals: {buySignals:N0} (win rate: {buyWinRate:P1})");
        sb.AppendLine($"- SELL signals: {sellSignals:N0} (win rate: {sellWinRate:P1})");
        sb.AppendLine();

        // Per-hour
        sb.AppendLine("## Per-Hour Performance");
        sb.AppendLine("| Hour (ET) | BUY Win% | BUY Avg P&L | SELL Win% | SELL Avg P&L | Signals |");
        sb.AppendLine("|-----------|----------|-------------|-----------|--------------|---------|");
        foreach (var h in hourlyStats.OrderBy(h => h.Hour))
        {
            decimal bwr = h.BuySignals > 0 ? (decimal)h.BuyWins / h.BuySignals * 100 : 0;
            decimal swr = h.SellSignals > 0 ? (decimal)h.SellWins / h.SellSignals * 100 : 0;
            sb.AppendLine($"| {h.Hour}:00-{h.Hour + 1}:00 | {bwr:F1}% | ${h.BuyAvgPnl:F2} | {swr:F1}% | ${h.SellAvgPnl:F2} | {h.TotalSignals} |");
        }
        sb.AppendLine();

        // Indicator effectiveness
        if (indicatorStats.Count > 0)
        {
            sb.AppendLine("## Indicator Effectiveness (which indicators predict wins?)");
            sb.AppendLine("| Indicator | When High (>0.3) Win% | When Low (<-0.3) Win% | Neutral Win% |");
            sb.AppendLine("|-----------|----------------------|----------------------|--------------|");
            foreach (var ind in indicatorStats)
            {
                sb.AppendLine($"| {ind.Name} | {ind.HighWinRate:F1}% | {ind.LowWinRate:F1}% | {ind.NeutralWinRate:F1}% |");
            }
            sb.AppendLine();
        }

        // Combinations
        if (comboStats.Count > 0)
        {
            sb.AppendLine("## Indicator Combinations (2-way interactions)");
            sb.AppendLine("| Combo | Both High Win% | Both Low Win% | Samples |");
            sb.AppendLine("|-------|---------------|---------------|---------|");
            foreach (var combo in comboStats.Take(10))
            {
                sb.AppendLine($"| {combo.Name} | {combo.BothHighWinRate:F1}% | {combo.BothLowWinRate:F1}% | {combo.Samples} |");
            }
            sb.AppendLine();
        }

        // Recent vs historical
        if (recentStats != null)
        {
            sb.AppendLine("## Recent vs Historical (regime detection)");
            sb.AppendLine("| Period | BUY Win% | SELL Win% | Avg Spread | Avg Volume |");
            sb.AppendLine("|--------|----------|-----------|------------|------------|");
            sb.AppendLine($"| Last 5 days | {recentStats.Recent5dBuyWinRate:F1}% | {recentStats.Recent5dSellWinRate:F1}% | ${recentStats.Recent5dAvgSpread:F4} | {recentStats.Recent5dAvgVolume:N0} |");
            sb.AppendLine($"| Last {lookbackDays} days | {buyWinRate * 100:F1}% | {sellWinRate * 100:F1}% | ${recentStats.FullAvgSpread:F4} | {recentStats.FullAvgVolume:N0} |");
        }

        // Fundamentals
        if (fundamentals != null)
        {
            sb.AppendLine();
            sb.AppendLine("## Fundamentals");
            sb.AppendLine($"- P/E: {fundamentals.Pe?.ToString("F1") ?? "N/A"} | Forward P/E: {fundamentals.ForwardPe?.ToString("F1") ?? "N/A"}");
            sb.AppendLine($"- EPS: {fundamentals.Eps?.ToString("F2") ?? "N/A"} | Est EPS: {fundamentals.EstEps?.ToString("F2") ?? "N/A"}");
            sb.AppendLine($"- Market Cap: {FormatLargeNumber(fundamentals.MarketCap)}");
            sb.AppendLine($"- Beta: {fundamentals.Beta?.ToString("F2") ?? "N/A"} | Short Float: {fundamentals.ShortFloat?.ToString("P1") ?? "N/A"}");
            sb.AppendLine($"- 52W High: ${fundamentals.High52w?.ToString("F2") ?? "N/A"} | 52W Low: ${fundamentals.Low52w?.ToString("F2") ?? "N/A"}");
            if (fundamentals.NextEarningsDate != null)
                sb.AppendLine($"- Next Earnings: {fundamentals.NextEarningsDate}");
        }

        // Capital flows (institutional money movement)
        if (capitalFlows.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Institutional Capital Flow (recent days)");
            sb.AppendLine("| Date | Large Net | SuperLarge Net | Total Net | Signal |");
            sb.AppendLine("|------|-----------|---------------|-----------|--------|");
            foreach (var flow in capitalFlows)
            {
                decimal largeNet = flow.LargeInflow - flow.LargeOutflow;
                decimal superLargeNet = flow.SuperLargeInflow - flow.SuperLargeOutflow;
                decimal totalNet = largeNet + superLargeNet + (flow.MediumInflow - flow.MediumOutflow);
                string signal = totalNet > 0 ? "INFLOW" : "OUTFLOW";
                sb.AppendLine($"| {flow.Date} | {FormatLargeNumber(largeNet)} | {FormatLargeNumber(superLargeNet)} | {FormatLargeNumber(totalNet)} | {signal} |");
            }
        }

        // Recent news headlines
        if (recentNews.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Recent News (last 5 days)");
            foreach (var news in recentNews.Take(10))
            {
                sb.AppendLine($"- [{news.PublishedAt:MMM dd}] {news.Title}");
            }
        }

        return sb.ToString();
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

        // Also add L2 features from TickSnapshots if available
        var l2Indicators = new[] {
            ("BookDepthRatio", 0.5m, 0.8m, 0.2m),
            ("BidWallSize", 2.0m, 3.0m, 1.0m),
            ("AskWallSize", 2.0m, 3.0m, 1.0m),
            ("ImbalanceVelocity", 0.05m, 0.1m, -0.05m),
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

    private async Task<SymbolStrategy?> CallBedrockAsync(long tickerId, string ticker, string analysisPrompt)
    {
        var region = _configuration["Bedrock:Region"];
        var modelId = _configuration["Bedrock:ModelId"];

        _logger.LogInformation("Calling Bedrock {Model} for {Ticker}...", modelId, ticker);

        var systemPrompt = @"You are a quantitative trading strategy optimizer. Analyze the provided trading signal performance data and generate conditional trading rules as JSON.

Your rules should:
1. Identify time-of-day patterns (which hours are profitable for BUY vs SELL)
2. Identify indicator thresholds that predict profitable trades
3. Identify indicator combinations that have high win rates
4. Disable trading during historically unprofitable hours
5. Set per-rule hold times and stop losses based on the data
6. Consider fundamental context: high short float = more volatile, near earnings = widen stops
7. Consider institutional capital flows: sustained large inflows = bullish bias, outflows = bearish
8. Consider recent news sentiment: major catalysts may shift patterns

Be conservative: only generate rules where:
- Sample size >= 30
- Confidence >= 0.55
- Expected P&L per 100 shares is POSITIVE after accounting for $5.98 round-trip commission ($2.99 entry + $2.99 exit)
- Rules with negative expected P&L should be excluded entirely
Focus on the strongest patterns in the data. Quality over quantity — fewer high-confidence rules are better than many marginal ones.";

        var userPrompt = $@"{analysisPrompt}

## Instructions

Based on the data above, generate a JSON object for {ticker} with this exact structure:
{{
  ""tickerId"": {tickerId},
  ""overallWinRate"": <number>,
  ""rules"": [
    {{
      ""id"": ""{ticker}-001"",
      ""name"": ""<descriptive name>"",
      ""hours"": [<list of ET hours this rule applies to>],
      ""direction"": ""BUY"" or ""SELL"",
      ""conditions"": {{
        ""minObi"": <number or null>,
        ""maxObi"": <number or null>,
        ""minImbalanceVelocity"": <number or null>,
        ""maxImbalanceVelocity"": <number or null>,
        ""minBidWallSize"": <number or null>,
        ""minAskWallSize"": <number or null>,
        ""trendDirection"": <1, -1, or null>,
        ""rsiRange"": [<min>, <max>] or null,
        ""minVolumeRatio"": <number or null>,
        ""aboveVwap"": <true, false, or null>
      }},
      ""confidence"": <0.55-1.0>,
      ""expectedPnlPer100Shares"": <number>,
      ""sampleSize"": <int>,
      ""holdSeconds"": <30-300>,
      ""stopLoss"": <0.10-1.00>
    }}
  ],
  ""disabledHours"": [<hours with win rate < 48%>],
  ""maxDailyTrades"": <5-30>,
  ""maxPositionShares"": 500,
  ""maxPositionDollars"": <25000 default, adjust based on ticker volatility>
}}

Return ONLY the JSON object, no markdown fences or explanation.";

        try
        {
            using var client = new AmazonBedrockRuntimeClient(Amazon.RegionEndpoint.GetBySystemName(region));

            var request = new ConverseRequest
            {
                ModelId = modelId,
                System = [new SystemContentBlock { Text = systemPrompt }],
                Messages =
                [
                    new Message
                    {
                        Role = ConversationRole.User,
                        Content = [new ContentBlock { Text = userPrompt }]
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

            if (string.IsNullOrWhiteSpace(responseText))
            {
                _logger.LogWarning("Empty Bedrock response for {Ticker}", ticker);
                return null;
            }

            _logger.LogInformation("Bedrock response for {Ticker}: {Length} chars, usage: input={InputTokens} output={OutputTokens}",
                ticker, responseText.Length,
                response.Usage?.InputTokens ?? 0,
                response.Usage?.OutputTokens ?? 0);

            return ParseAndValidateRules(ticker, responseText);
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
            // Strip markdown code fences if present
            var json = responseText.Trim();
            if (json.StartsWith("```"))
            {
                int firstNewline = json.IndexOf('\n');
                int lastFence = json.LastIndexOf("```");
                if (firstNewline >= 0 && lastFence > firstNewline)
                    json = json[(firstNewline + 1)..lastFence].Trim();
            }

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
    /// Retention: SymbolBookSnapshots=3 days, TickSnapshots=30 days, rest=forever
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

            var tickCutoff = DateTime.UtcNow.AddDays(-30);
            int tickDeleted = 0;
            do
            {
                batch = await dbContext.Database.ExecuteSqlRawAsync(
                    @"DELETE FROM ""TickSnapshots"" WHERE ""Id"" IN (
                        SELECT ""Id"" FROM ""TickSnapshots"" WHERE ""Timestamp"" < {0} LIMIT 5000
                    )", tickCutoff);
                tickDeleted += batch;
            } while (batch >= 5000);

            _logger.LogWarning(
                "Cleanup: deleted {BookCount} SymbolBookSnapshots (>3d), {TickCount} TickSnapshots (>30d)",
                bookDeleted, tickDeleted);
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

    #region Gap Backfill (TickSnapshots + TradingSignals from bars)

    /// <summary>
    /// For each day in the lookback period, create TickSnapshots from 1-minute bars
    /// where no real TickSnapshot exists (gaps from app downtime).
    /// </summary>
    private async Task BackfillTickSnapshotsFromBarsAsync(long tickerId, string ticker, string symbolId, int lookbackDays)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TradingPilotDbContext>();

        var cutoff = DateTime.UtcNow.AddDays(-lookbackDays);

        // Use raw SQL with window functions to compute EMA9, EMA20, RSI14, and VolumeRatio from 1-min bars.
        // EMA is approximated using exponential smoothing weights: alpha_9 = 2/(9+1) = 0.2, alpha_20 = 2/(20+1) ≈ 0.095
        // RSI uses standard 14-period gain/loss averages.
        int inserted = await dbContext.Database.ExecuteSqlRawAsync(@"
            WITH bars AS (
                SELECT sb.""SymbolId"", sb.""Timestamp"", sb.""Open"", sb.""High"", sb.""Low"",
                       sb.""Close"", sb.""Volume"", COALESCE(sb.""Vwap"", 0) AS vwap,
                       sb.""Close"" - LAG(sb.""Close"") OVER (ORDER BY sb.""Timestamp"") AS price_change,
                       AVG(sb.""Volume"") OVER (ORDER BY sb.""Timestamp"" ROWS BETWEEN 20 PRECEDING AND 1 PRECEDING) AS avg_vol,
                       AVG(sb.""Close"") OVER (ORDER BY sb.""Timestamp"" ROWS BETWEEN 8 PRECEDING AND CURRENT ROW) AS sma9,
                       AVG(sb.""Close"") OVER (ORDER BY sb.""Timestamp"" ROWS BETWEEN 19 PRECEDING AND CURRENT ROW) AS sma20,
                       ROW_NUMBER() OVER (ORDER BY sb.""Timestamp"") AS rn
                FROM ""SymbolBars"" sb
                WHERE sb.""SymbolId"" = {1}
                  AND sb.""Timeframe"" = 2
                  AND sb.""Timestamp"" > {2}
            ),
            with_rsi AS (
                SELECT b.*,
                       COALESCE(AVG(GREATEST(b.price_change, 0)) OVER (ORDER BY b.""Timestamp"" ROWS BETWEEN 13 PRECEDING AND CURRENT ROW), 0) AS avg_gain,
                       COALESCE(AVG(GREATEST(-b.price_change, 0)) OVER (ORDER BY b.""Timestamp"" ROWS BETWEEN 13 PRECEDING AND CURRENT ROW), 0) AS avg_loss
                FROM bars b
            )
            INSERT INTO ""TickSnapshots"" (
                ""Id"", ""SymbolId"", ""TickerId"", ""Timestamp"",
                ""Price"", ""Open"", ""High"", ""Low"", ""Volume"",
                ""Vwap"", ""Ema9"", ""Ema20"", ""Rsi14"", ""VolumeRatio"",
                ""UptickCount"", ""DowntickCount"", ""TickMomentum"",
                ""BookDepthRatio"", ""BidWallSize"", ""AskWallSize"",
                ""BidSweepCost"", ""AskSweepCost"", ""ImbalanceVelocity"", ""SpreadPercentile""
            )
            SELECT
                gen_random_uuid(), r.""SymbolId"", {0}, r.""Timestamp"",
                r.""Close"", r.""Open"", r.""High"", r.""Low"", r.""Volume"",
                r.vwap,
                COALESCE(r.sma9, r.""Close""),
                COALESCE(r.sma20, r.""Close""),
                CASE WHEN r.avg_loss = 0 THEN 100.0
                     WHEN r.avg_gain = 0 THEN 0.0
                     ELSE ROUND((100.0 - 100.0 / (1.0 + r.avg_gain / r.avg_loss))::numeric, 2)
                END,
                CASE WHEN COALESCE(r.avg_vol, 0) = 0 THEN 1.0
                     ELSE ROUND((r.""Volume""::numeric / r.avg_vol)::numeric, 4)
                END,
                -- Tick data not available from bars — derive momentum from price change direction
                CASE WHEN COALESCE(r.price_change, 0) > 0 THEN 1 ELSE 0 END,
                CASE WHEN COALESCE(r.price_change, 0) < 0 THEN 1 ELSE 0 END,
                CASE WHEN COALESCE(r.price_change, 0) > 0 THEN 1.0
                     WHEN COALESCE(r.price_change, 0) < 0 THEN -1.0
                     ELSE 0.0 END,
                -- L2 features not available from bars — leave as 0
                0, 0, 0, 0, 0, 0, 0.5
            FROM with_rsi r
            WHERE r.rn > 20
              AND NOT EXISTS (
                  SELECT 1 FROM ""TickSnapshots"" ts
                  WHERE ts.""SymbolId"" = r.""SymbolId""
                    AND ABS(EXTRACT(EPOCH FROM ts.""Timestamp"" - r.""Timestamp"")) < 30
              )",
            tickerId, symbolId, cutoff);

        if (inserted > 0)
            _logger.LogWarning("Backfilled {Inserted} TickSnapshots from bars for {Ticker} ({Days}d lookback)",
                inserted, ticker, lookbackDays);
    }

    /// <summary>
    /// Generate approximate TradingSignals from TickSnapshots for gaps.
    /// Uses price momentum between bars and pre-computes PriceAfter1Min/5Min verification.
    /// </summary>
    private async Task BackfillTradingSignalsFromSnapshotsAsync(long tickerId, string ticker, string symbolId, int lookbackDays)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TradingPilotDbContext>();

        var cutoff = DateTime.UtcNow.AddDays(-lookbackDays);

        // Use raw SQL — compute score from price momentum between bars,
        // generate signals every ~6 bars (~6 minutes), with pre-filled verification
        int inserted = await dbContext.Database.ExecuteSqlRawAsync(@"
            WITH numbered AS (
                SELECT ts.""SymbolId"", ts.""TickerId"", ts.""Timestamp"", ts.""Price"",
                       ts.""Ema9"", ts.""Ema20"", ts.""Rsi14"", ts.""Vwap"", ts.""VolumeRatio"",
                       LAG(ts.""Price"", 5) OVER (PARTITION BY ts.""SymbolId"" ORDER BY ts.""Timestamp"") AS price_5ago,
                       LEAD(ts.""Price"", 1) OVER (PARTITION BY ts.""SymbolId"" ORDER BY ts.""Timestamp"") AS price_1after,
                       LEAD(ts.""Price"", 5) OVER (PARTITION BY ts.""SymbolId"" ORDER BY ts.""Timestamp"") AS price_5after,
                       ROW_NUMBER() OVER (PARTITION BY ts.""SymbolId"" ORDER BY ts.""Timestamp"") AS rn
                FROM ""TickSnapshots"" ts
                WHERE ts.""SymbolId"" = {0} AND ts.""Timestamp"" > {1}
            ),
            scored AS (
                SELECT *,
                    CASE WHEN ""Ema9"" > 0 AND ""Ema20"" > 0 AND ""Ema9"" > ""Ema20"" THEN 0.15
                         WHEN ""Ema9"" > 0 AND ""Ema20"" > 0 AND ""Ema9"" < ""Ema20"" THEN -0.15
                         ELSE 0 END
                    + CASE WHEN ""Rsi14"" > 0 THEN (""Rsi14"" - 50.0) / 200.0 ELSE 0 END
                    + CASE WHEN ""Vwap"" > 0 AND ""Price"" > 0
                          THEN LEAST(GREATEST((""Price"" - ""Vwap"") / ""Price"" * 50, -0.15), 0.15)
                          ELSE 0 END
                    + CASE WHEN price_5ago > 0 AND ""Price"" > 0
                          THEN LEAST(GREATEST((""Price"" - price_5ago) / price_5ago * 100, -0.20), 0.20)
                          ELSE 0 END
                    + CASE WHEN ""VolumeRatio"" > 1.5 THEN 0.10
                           WHEN ""VolumeRatio"" < 0.5 THEN -0.10
                           ELSE 0 END
                    AS score
                FROM numbered
                WHERE rn > 5 AND rn % 6 = 0
            )
            INSERT INTO ""TradingSignals"" (
                ""Id"", ""SymbolId"", ""TickerId"", ""Timestamp"",
                ""Type"", ""Strength"", ""Price"", ""Score"", ""Reason"",
                ""ObiSmoothed"", ""Wobi"", ""PressureRoc"", ""SpreadSignal"", ""LargeOrderSignal"",
                ""Spread"", ""Imbalance"", ""BidLevels"", ""AskLevels"",
                ""PriceAfter1Min"", ""PriceAfter5Min"",
                ""WasCorrect1Min"", ""VerifiedAt""
            )
            SELECT
                gen_random_uuid(), s.""SymbolId"", s.""TickerId"", s.""Timestamp"",
                CASE WHEN s.score > 0 THEN 1 ELSE 2 END,
                CASE WHEN ABS(s.score) >= 0.40 THEN 2
                     WHEN ABS(s.score) >= 0.20 THEN 1
                     ELSE 0 END,
                s.""Price"", s.score,
                '[BACKFILL]',
                0, 0, 0, 0, 0,
                0, 0, 0, 0,
                CASE WHEN s.price_1after > 0 THEN s.price_1after ELSE NULL END,
                CASE WHEN s.price_5after > 0 THEN s.price_5after ELSE NULL END,
                CASE WHEN s.price_1after > 0 THEN
                    CASE WHEN s.score > 0 THEN s.price_1after > s.""Price""
                         ELSE s.price_1after < s.""Price"" END
                    ELSE NULL END,
                NOW()
            FROM scored s
            WHERE ABS(s.score) >= 0.10
              AND NOT EXISTS (
                  SELECT 1 FROM ""TradingSignals"" ts
                  WHERE ts.""SymbolId"" = s.""SymbolId""
                    AND ABS(EXTRACT(EPOCH FROM ts.""Timestamp"" - s.""Timestamp"")) < 30
              )",
            symbolId, cutoff);

        if (inserted > 0)
            _logger.LogWarning("Backfilled {Inserted} TradingSignals for {Ticker} ({Days}d lookback)",
                inserted, ticker, lookbackDays);
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
