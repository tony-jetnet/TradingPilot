using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingPilot.EntityFrameworkCore;
using TradingPilot.Trading;

namespace TradingPilot.Trading;

/// <summary>
/// Runs after market close to train optimal indicator weights, score thresholds,
/// and per-hour adjustments for each ticker using statistical analysis and
/// hill-climbing optimization. Outputs model_config.json for live trading.
/// No ML libraries — pure C# math.
/// </summary>
[DisableConcurrentExecution(600)]
[AutomaticRetry(Attempts = 0)]
public class NightlyLocalTrainer
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NightlyLocalTrainer> _logger;

    private const decimal CommissionPerTrade = 2.99m;
    private const int SimulationShares = 500;
    private const int OptimizationIterations = 100;
    private const string ModelConfigPath = @"D:\Third-Parties\WebullHook\model_config.json";

    // Default weights (same as MarketMicrostructureAnalyzer constants)
    private static readonly decimal[] DefaultWeights =
    [
        0.15m, // OBI
        0.15m, // WOBI
        0.10m, // PressureRoc
        0.05m, // Spread
        0.05m, // LargeOrder
        0.10m, // TickMomentum
        0.15m, // Trend
        0.10m, // Vwap
        0.10m, // Volume
        0.05m, // Rsi
    ];

    // Candidate thresholds for entry score optimization
    private static readonly decimal[] CandidateThresholds =
        [0.15m, 0.20m, 0.25m, 0.30m, 0.35m, 0.40m, 0.45m, 0.50m];

    public NightlyLocalTrainer(
        IServiceScopeFactory scopeFactory,
        ILogger<NightlyLocalTrainer> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task TrainAsync(int lookbackDays = 20)
    {
        _logger.LogWarning("=== NIGHTLY MODEL TRAINING START (lookback={LookbackDays}d) ===", lookbackDays);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // 1. Load all signals with outcomes
            var data = await LoadTrainingDataAsync(lookbackDays);
            if (data.Count == 0)
            {
                _logger.LogWarning("No training data found for the last {Days} days. Skipping training.", lookbackDays);
                return;
            }

            _logger.LogWarning("Loaded {Count} training rows across {Tickers} tickers",
                data.Count, data.Select(d => d.TickerId).Distinct().Count());

            // 2. For each ticker, train optimal parameters
            var config = new ModelConfig
            {
                TrainedAt = DateTime.UtcNow,
                TrainingRows = data.Count,
                LookbackDays = lookbackDays,
            };

            foreach (var tickerGroup in data.GroupBy(d => d.TickerId))
            {
                var tickerConfig = TrainTicker(tickerGroup.Key, tickerGroup.First().Ticker, tickerGroup.ToList());
                config.Tickers[tickerGroup.Key] = tickerConfig;
            }

            // 3. Save to file
            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(config, jsonOptions);

            // Ensure directory exists
            var dir = Path.GetDirectoryName(ModelConfigPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(ModelConfigPath, json);
            _logger.LogWarning("Model config saved: {Path} ({Tickers} tickers)", ModelConfigPath, config.Tickers.Count);

            // 4. Also persist config as backup in database
            await PersistConfigToDbAsync(json);

            // NOTE: Data cleanup is handled by NightlyStrategyOptimizer at 9:30 PM ET

            sw.Stop();
            _logger.LogWarning("=== NIGHTLY MODEL TRAINING COMPLETE in {Elapsed}s ===", sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nightly model training failed");
            throw;
        }
    }

    #region Training Data Loading

    private async Task<List<TrainingRow>> LoadTrainingDataAsync(int lookbackDays)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TradingPilotDbContext>();

        var cutoff = DateTime.UtcNow.AddDays(-lookbackDays);

        // Raw SQL for the complex subquery join — EF Core can't efficiently do this
        var sql = $@"
            SELECT
                ts.""TickerId"",
                s.""Id"" AS ""Ticker"",
                ts.""Type""::int AS ""SignalType"",
                ts.""Price"",
                ts.""Score"",
                ts.""ObiSmoothed"",
                ts.""Wobi"",
                ts.""PressureRoc"",
                ts.""SpreadSignal"",
                ts.""LargeOrderSignal"",
                ts.""Imbalance"",
                EXTRACT(HOUR FROM (ts.""Timestamp"" AT TIME ZONE 'UTC') AT TIME ZONE 'America/New_York')::int AS ""Hour"",
                COALESCE(ts.""PriceAfter1Min"",
                    (SELECT bs.""MidPrice"" FROM ""SymbolBookSnapshots"" bs
                     WHERE bs.""SymbolId"" = ts.""SymbolId""
                     AND bs.""Timestamp"" BETWEEN ts.""Timestamp"" + INTERVAL '55 seconds'
                                               AND ts.""Timestamp"" + INTERVAL '65 seconds'
                     ORDER BY bs.""Timestamp"" LIMIT 1)
                ) AS ""PriceAfter1Min""
            FROM ""TradingSignals"" ts
            JOIN ""Symbols"" s ON s.""Id"" = ts.""SymbolId""
            WHERE ts.""Timestamp"" > {{0}}
            ORDER BY ts.""Timestamp""";

        var rows = new List<TrainingRow>();

        using var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var param = command.CreateParameter();
        param.ParameterName = "p0";
        param.Value = cutoff;
        // Replace {0} with @p0 for parameterized query
        command.CommandText = sql.Replace("{0}", "@p0");
        command.Parameters.Add(param);
        command.CommandTimeout = 120;

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new TrainingRow
            {
                TickerId = reader.GetInt64(reader.GetOrdinal("TickerId")),
                Ticker = reader.GetString(reader.GetOrdinal("Ticker")),
                SignalType = reader.GetInt32(reader.GetOrdinal("SignalType")),
                Price = reader.GetDecimal(reader.GetOrdinal("Price")),
                Score = reader.GetDecimal(reader.GetOrdinal("Score")),
                ObiSmoothed = reader.GetDecimal(reader.GetOrdinal("ObiSmoothed")),
                Wobi = reader.GetDecimal(reader.GetOrdinal("Wobi")),
                PressureRoc = reader.GetDecimal(reader.GetOrdinal("PressureRoc")),
                SpreadSignal = reader.GetDecimal(reader.GetOrdinal("SpreadSignal")),
                LargeOrderSignal = reader.GetDecimal(reader.GetOrdinal("LargeOrderSignal")),
                Imbalance = reader.GetDecimal(reader.GetOrdinal("Imbalance")),
                Hour = reader.GetInt32(reader.GetOrdinal("Hour")),
            };

            var priceAfterOrd = reader.GetOrdinal("PriceAfter1Min");
            if (!reader.IsDBNull(priceAfterOrd))
            {
                row.PriceAfter1Min = reader.GetDecimal(priceAfterOrd);

                // Label win/loss
                if (row.PriceAfter1Min.HasValue)
                {
                    decimal priceDelta = row.PriceAfter1Min.Value - row.Price;
                    // BUY signal (Type=1): win if price went up. SELL signal (Type=2): win if price went down.
                    row.IsWin = row.SignalType == 1 ? priceDelta > 0 : priceDelta < 0;
                    row.PnlPer100Shares = (row.SignalType == 1 ? priceDelta : -priceDelta) * SimulationShares;
                }
            }

            rows.Add(row);
        }

        // Filter to only rows with outcome data
        return rows.Where(r => r.PriceAfter1Min.HasValue).ToList();
    }

    #endregion

    #region Per-Ticker Training

    private TickerModelConfig TrainTicker(long tickerId, string ticker, List<TrainingRow> rows)
    {
        _logger.LogInformation("Training ticker {Ticker} (tickerId={TickerId}): {Count} samples",
            ticker, tickerId, rows.Count);

        var config = new TickerModelConfig
        {
            TickerId = tickerId,
            Ticker = ticker,
            TrainingSamples = rows.Count,
        };

        if (rows.Count < 20)
        {
            _logger.LogWarning("Ticker {Ticker}: only {Count} samples, using default weights", ticker, rows.Count);
            ApplyDefaultWeights(config);
            return config;
        }

        // Step 1: Overall win rate
        int wins = rows.Count(r => r.IsWin);
        config.OverallWinRate = (decimal)wins / rows.Count;

        // Step 2: Find optimal weights via hill-climbing
        decimal[] optimizedWeights = OptimizeWeights(rows);
        config.WeightObi = optimizedWeights[0];
        config.WeightWobi = optimizedWeights[1];
        config.WeightPressureRoc = optimizedWeights[2];
        config.WeightSpread = optimizedWeights[3];
        config.WeightLargeOrder = optimizedWeights[4];
        config.WeightTickMomentum = optimizedWeights[5];
        config.WeightTrend = optimizedWeights[6];
        config.WeightVwap = optimizedWeights[7];
        config.WeightVolume = optimizedWeights[8];
        config.WeightRsi = optimizedWeights[9];

        // Step 3: Find optimal score thresholds
        FindOptimalThresholds(rows, optimizedWeights, config);

        // Step 4: Per-hour analysis
        AnalyzeHours(rows, config);

        // Step 5: Direction analysis
        AnalyzeDirections(rows, config);

        _logger.LogWarning(
            "Ticker {Ticker}: WinRate={WinRate:P1} MinBuy={MinBuy:F2} MinSell={MinSell:F2} " +
            "Hold={Hold}s EnableBuy={EnableBuy} EnableSell={EnableSell} " +
            "DisabledHours=[{DisabledHours}]",
            ticker, config.OverallWinRate, config.MinScoreToBuy, config.MinScoreToSell,
            config.OptimalHoldSeconds, config.EnableBuy, config.EnableSell,
            string.Join(",", config.HourlyAdjustments
                .Where(h => !h.Value.EnableTrading)
                .Select(h => h.Key)));

        return config;
    }

    private static void ApplyDefaultWeights(TickerModelConfig config)
    {
        config.WeightObi = DefaultWeights[0];
        config.WeightWobi = DefaultWeights[1];
        config.WeightPressureRoc = DefaultWeights[2];
        config.WeightSpread = DefaultWeights[3];
        config.WeightLargeOrder = DefaultWeights[4];
        config.WeightTickMomentum = DefaultWeights[5];
        config.WeightTrend = DefaultWeights[6];
        config.WeightVwap = DefaultWeights[7];
        config.WeightVolume = DefaultWeights[8];
        config.WeightRsi = DefaultWeights[9];
    }

    #endregion

    #region Step 2: Hill-Climbing Weight Optimization

    /// <summary>
    /// Simple hill-climbing optimizer: randomly perturb one weight at a time,
    /// keep the change if it improves total P&amp;L after fees.
    /// </summary>
    private decimal[] OptimizeWeights(List<TrainingRow> rows)
    {
        var weights = (decimal[])DefaultWeights.Clone();
        decimal bestProfit = ComputeTotalProfit(rows, weights);
        var rng = new Random(42); // deterministic for reproducibility

        for (int iter = 0; iter < OptimizationIterations; iter++)
        {
            // Pick a random weight index
            int idx = rng.Next(weights.Length);

            // Perturb by ±0.05
            decimal perturbation = (rng.NextDouble() > 0.5 ? 0.05m : -0.05m);
            decimal original = weights[idx];
            weights[idx] = Math.Clamp(weights[idx] + perturbation, -0.50m, 0.50m);

            // Normalize weights so absolute values sum to ~1.0
            NormalizeWeights(weights);

            decimal newProfit = ComputeTotalProfit(rows, weights);

            if (newProfit > bestProfit)
            {
                bestProfit = newProfit;
            }
            else
            {
                // Revert
                weights[idx] = original;
                NormalizeWeights(weights);
            }
        }

        return weights;
    }

    private static void NormalizeWeights(decimal[] weights)
    {
        decimal absSum = weights.Sum(w => Math.Abs(w));
        if (absSum > 0 && absSum != 1.0m)
        {
            for (int i = 0; i < weights.Length; i++)
                weights[i] = weights[i] / absSum;
        }
    }

    /// <summary>
    /// Compute the composite score for a training row using given weights.
    /// Matches the indicator order in MarketMicrostructureAnalyzer.
    /// </summary>
    private static decimal ComputeCompositeScore(TrainingRow row, decimal[] weights)
    {
        return row.ObiSmoothed * weights[0]
             + row.Wobi * weights[1]
             + row.PressureRoc * weights[2]
             + row.SpreadSignal * weights[3]
             + row.LargeOrderSignal * weights[4]
             + row.Imbalance * weights[5]  // TickMomentum proxy — we use Imbalance from DB
             + row.Score * weights[6] * 0   // Trend not stored separately; use score direction
             + 0                             // Vwap — not stored in training row
             + 0                             // Volume — not stored in training row
             + 0;                            // RSI — not stored in training row
        // NOTE: For indicators not individually stored in TradingSignalRecord,
        // we fall back to the stored composite Score as a proxy.
    }

    /// <summary>
    /// Recompute composite score using the raw indicators available in TradingSignalRecord.
    /// Since not all 10 indicators are stored separately, we use what we have and
    /// weight the stored Score for the missing ones.
    /// </summary>
    private static decimal ComputeWeightedScore(TrainingRow row, decimal[] weights)
    {
        // Available indicators from TradingSignalRecord:
        // OBI, WOBI, PressureRoc, SpreadSignal, LargeOrderSignal, Imbalance
        // Missing: TickMomentum, Trend, Vwap, Volume, RSI
        // The original Score already incorporates all 10, so we blend:
        // Use available indicators with their weights, and scale original Score for the rest.

        decimal knownScore =
            row.ObiSmoothed * weights[0]
          + row.Wobi * weights[1]
          + row.PressureRoc * weights[2]
          + row.SpreadSignal * weights[3]
          + row.LargeOrderSignal * weights[4];

        // Weight of known indicators
        decimal knownWeight = Math.Abs(weights[0]) + Math.Abs(weights[1]) + Math.Abs(weights[2])
                            + Math.Abs(weights[3]) + Math.Abs(weights[4]);

        // Weight of unknown indicators
        decimal unknownWeight = Math.Abs(weights[5]) + Math.Abs(weights[6]) + Math.Abs(weights[7])
                              + Math.Abs(weights[8]) + Math.Abs(weights[9]);

        // For unknowns, scale the original composite score proportionally
        // The original score used default weights summing to 1.0
        decimal unknownScore = unknownWeight > 0 ? row.Score * unknownWeight : 0;

        return knownScore + unknownScore;
    }

    /// <summary>
    /// Simulate trading all rows with given weights, return total P&amp;L after fees.
    /// Only trades when |score| >= 0.30 (moderate threshold for optimization phase).
    /// </summary>
    private static decimal ComputeTotalProfit(List<TrainingRow> rows, decimal[] weights)
    {
        decimal totalPnl = 0;
        const decimal threshold = 0.30m;

        foreach (var row in rows)
        {
            if (!row.PriceAfter1Min.HasValue) continue;

            decimal score = ComputeWeightedScore(row, weights);
            bool wouldTrade = (row.SignalType == 1 && score >= threshold)
                           || (row.SignalType == 2 && score <= -threshold);

            if (wouldTrade)
            {
                // P&L = price movement * shares - commission
                decimal priceDelta = row.PriceAfter1Min.Value - row.Price;
                decimal tradePnl = (row.SignalType == 1 ? priceDelta : -priceDelta) * SimulationShares;
                totalPnl += tradePnl - CommissionPerTrade * 2; // Round-trip: entry + exit commission
            }
        }

        return totalPnl;
    }

    #endregion

    #region Step 3: Optimal Score Thresholds

    private void FindOptimalThresholds(List<TrainingRow> rows, decimal[] weights, TickerModelConfig config)
    {
        decimal bestBuyPnlPerTrade = decimal.MinValue;
        decimal bestBuyThreshold = 0.35m;
        decimal bestSellPnlPerTrade = decimal.MinValue;
        decimal bestSellThreshold = 0.35m;

        foreach (decimal threshold in CandidateThresholds)
        {
            // Simulate BUY trades
            var buyTrades = rows.Where(r => r.SignalType == 1).ToList();
            decimal buyPnl = 0;
            int buyCount = 0;
            foreach (var row in buyTrades)
            {
                decimal score = ComputeWeightedScore(row, weights);
                if (score >= threshold && row.PriceAfter1Min.HasValue)
                {
                    decimal delta = row.PriceAfter1Min.Value - row.Price;
                    buyPnl += delta * SimulationShares - CommissionPerTrade * 2; // Round-trip
                    buyCount++;
                }
            }
            decimal buyPnlPerTrade = buyCount > 0 ? buyPnl / buyCount : 0;
            if (buyCount >= 5 && buyPnlPerTrade > bestBuyPnlPerTrade)
            {
                bestBuyPnlPerTrade = buyPnlPerTrade;
                bestBuyThreshold = threshold;
            }

            // Simulate SELL trades
            var sellTrades = rows.Where(r => r.SignalType == 2).ToList();
            decimal sellPnl = 0;
            int sellCount = 0;
            foreach (var row in sellTrades)
            {
                decimal score = ComputeWeightedScore(row, weights);
                if (score <= -threshold && row.PriceAfter1Min.HasValue)
                {
                    decimal delta = row.Price - row.PriceAfter1Min.Value;
                    sellPnl += delta * SimulationShares - CommissionPerTrade * 2; // Round-trip
                    sellCount++;
                }
            }
            decimal sellPnlPerTrade = sellCount > 0 ? sellPnl / sellCount : 0;
            if (sellCount >= 5 && sellPnlPerTrade > bestSellPnlPerTrade)
            {
                bestSellPnlPerTrade = sellPnlPerTrade;
                bestSellThreshold = threshold;
            }
        }

        config.MinScoreToBuy = bestBuyThreshold;
        config.MinScoreToSell = -bestSellThreshold; // negative for sell
        config.MinScoreToExit = Math.Min(bestBuyThreshold, bestSellThreshold) * 0.6m; // exit at 60% of entry threshold

        _logger.LogInformation(
            "  {Ticker} thresholds: Buy>={BuyThresh:F2} (${BuyPnl:F2}/trade) Sell<={SellThresh:F2} (${SellPnl:F2}/trade)",
            config.Ticker, config.MinScoreToBuy, bestBuyPnlPerTrade,
            config.MinScoreToSell, bestSellPnlPerTrade);
    }

    #endregion

    #region Step 4: Per-Hour Analysis

    private void AnalyzeHours(List<TrainingRow> rows, TickerModelConfig config)
    {
        // Trading hours: 9 AM to 4 PM ET (hours 9-15)
        for (int hour = 9; hour <= 15; hour++)
        {
            var hourRows = rows.Where(r => r.Hour == hour).ToList();
            if (hourRows.Count < 5)
            {
                config.HourlyAdjustments[hour] = new HourlyAdjustment
                {
                    EnableTrading = true,
                    ScoreMultiplier = 1.0m,
                    WinRate = 0,
                };
                continue;
            }

            int hourWins = hourRows.Count(r => r.IsWin);
            decimal winRate = (decimal)hourWins / hourRows.Count;

            var adj = new HourlyAdjustment
            {
                WinRate = Math.Round(winRate, 4),
                EnableTrading = winRate >= 0.45m, // Disable if win rate < 45%
            };

            if (winRate > 0.60m)
                adj.ScoreMultiplier = 1.2m; // Boost signals in high-win hours
            else if (winRate < 0.50m)
                adj.ScoreMultiplier = 0.7m; // Dampen signals in low-win hours
            else
                adj.ScoreMultiplier = 1.0m;

            config.HourlyAdjustments[hour] = adj;

            _logger.LogInformation(
                "  {Ticker} hour {Hour}: {Count} samples, WinRate={WinRate:P1}, Multiplier={Mult:F1}, Enabled={Enabled}",
                config.Ticker, hour, hourRows.Count, winRate, adj.ScoreMultiplier, adj.EnableTrading);
        }
    }

    #endregion

    #region Step 5: Direction Analysis

    private void AnalyzeDirections(List<TrainingRow> rows, TickerModelConfig config)
    {
        var buyRows = rows.Where(r => r.SignalType == 1).ToList();
        var sellRows = rows.Where(r => r.SignalType == 2).ToList();

        if (buyRows.Count >= 10)
        {
            decimal buyWinRate = (decimal)buyRows.Count(r => r.IsWin) / buyRows.Count;
            config.EnableBuy = buyWinRate >= 0.48m;
            _logger.LogInformation("  {Ticker} BUY: {Count} samples, WinRate={WinRate:P1}, Enabled={Enabled}",
                config.Ticker, buyRows.Count, buyWinRate, config.EnableBuy);
        }

        if (sellRows.Count >= 10)
        {
            decimal sellWinRate = (decimal)sellRows.Count(r => r.IsWin) / sellRows.Count;
            config.EnableSell = sellWinRate >= 0.48m;
            _logger.LogInformation("  {Ticker} SELL: {Count} samples, WinRate={WinRate:P1}, Enabled={Enabled}",
                config.Ticker, sellRows.Count, sellWinRate, config.EnableSell);
        }
    }

    #endregion

    #region Database Persistence

    private async Task PersistConfigToDbAsync(string json)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TradingPilotDbContext>();

            await dbContext.Database.ExecuteSqlRawAsync(@"
                INSERT INTO ""ModelConfigs"" (""Key"", ""Value"", ""UpdatedAt"")
                VALUES ('nightly_model_config', {0}, {1})
                ON CONFLICT (""Key"") DO UPDATE SET ""Value"" = {0}, ""UpdatedAt"" = {1}",
                json, DateTime.UtcNow);

            _logger.LogInformation("Model config persisted to database");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist model config to database (non-fatal)");
        }
    }

    #endregion
}

#region Training Row DTO

public class TrainingRow
{
    public long TickerId { get; set; }
    public string Ticker { get; set; } = "";
    public int SignalType { get; set; } // 1=BUY, 2=SELL
    public decimal Price { get; set; }
    public decimal Score { get; set; }
    public decimal ObiSmoothed { get; set; }
    public decimal Wobi { get; set; }
    public decimal PressureRoc { get; set; }
    public decimal SpreadSignal { get; set; }
    public decimal LargeOrderSignal { get; set; }
    public decimal Imbalance { get; set; }
    public int Hour { get; set; } // Hour of day (ET)
    public decimal? PriceAfter1Min { get; set; }
    public bool IsWin { get; set; }
    public decimal PnlPer100Shares { get; set; }
}

#endregion
