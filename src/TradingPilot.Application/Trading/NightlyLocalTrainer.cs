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

    private const decimal CommissionPerTrade = 0m; // Both Webull and Questrade have $0 commission on US equities
    private const int SimulationShares = 100;
    private const int IterationsPerRestart = 300;
    private const int NumRestarts = 3;
    private const string ModelConfigPath = @"D:\Third-Parties\WebullHook\model_config.json";

    // Default weights: only L2 microstructure + tick momentum are weighted.
    // Trend, VWAP, Volume, and RSI are handled by contextual filters only (no double penalization).
    private static readonly decimal[] DefaultWeights =
    [
        0.25m, // OBI
        0.25m, // WOBI
        0.15m, // PressureRoc
        0.10m, // Spread
        0.10m, // LargeOrder
        0.15m, // TickMomentum
        0.00m, // Trend      — contextual filter only
        0.00m, // Vwap       — contextual filter only
        0.00m, // Volume     — contextual filter only
        0.00m, // Rsi        — contextual filter only
    ];

    // Candidate thresholds for entry score optimization
    private static readonly decimal[] CandidateThresholds =
        [0.25m, 0.30m, 0.35m, 0.40m, 0.45m, 0.50m, 0.55m, 0.60m];

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
        // Load 1/5/15/30-min outcomes. Use PriceAfter30Min as primary training label
        // for swing-style hold times, with fallback chain 15Min > 5Min.
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
                ts.""TickMomentum"",
                ts.""Ema9"",
                ts.""Ema20"",
                ts.""Rsi14"",
                ts.""Vwap"",
                ts.""VolumeRatio"",
                COALESCE(ts.""Spread"", 0) AS ""Spread"",
                EXTRACT(HOUR FROM (ts.""Timestamp"" AT TIME ZONE 'UTC') AT TIME ZONE 'America/New_York')::int AS ""Hour"",
                COALESCE(ts.""PriceAfter1Min"",
                    (SELECT bs.""MidPrice"" FROM ""SymbolBookSnapshots"" bs
                     WHERE bs.""SymbolId"" = ts.""SymbolId""
                     AND bs.""Timestamp"" BETWEEN ts.""Timestamp"" + INTERVAL '55 seconds'
                                               AND ts.""Timestamp"" + INTERVAL '65 seconds'
                     ORDER BY bs.""Timestamp"" LIMIT 1)
                ) AS ""PriceAfter1Min"",
                COALESCE(ts.""PriceAfter5Min"",
                    (SELECT bs.""MidPrice"" FROM ""SymbolBookSnapshots"" bs
                     WHERE bs.""SymbolId"" = ts.""SymbolId""
                     AND bs.""Timestamp"" BETWEEN ts.""Timestamp"" + INTERVAL '295 seconds'
                                               AND ts.""Timestamp"" + INTERVAL '305 seconds'
                     ORDER BY ABS(EXTRACT(EPOCH FROM bs.""Timestamp"" - (ts.""Timestamp"" + INTERVAL '300 seconds')))
                     LIMIT 1)
                ) AS ""PriceAfter5Min"",
                COALESCE(ts.""PriceAfter15Min"",
                    (SELECT sb.""Close"" FROM ""SymbolBars"" sb
                     WHERE sb.""SymbolId"" = ts.""SymbolId""
                     AND sb.""Timestamp"" BETWEEN ts.""Timestamp"" + INTERVAL '870 seconds'
                                               AND ts.""Timestamp"" + INTERVAL '930 seconds'
                     ORDER BY ABS(EXTRACT(EPOCH FROM sb.""Timestamp"" - (ts.""Timestamp"" + INTERVAL '900 seconds')))
                     LIMIT 1)
                ) AS ""PriceAfter15Min"",
                COALESCE(ts.""PriceAfter30Min"",
                    (SELECT sb.""Close"" FROM ""SymbolBars"" sb
                     WHERE sb.""SymbolId"" = ts.""SymbolId""
                     AND sb.""Timestamp"" BETWEEN ts.""Timestamp"" + INTERVAL '1770 seconds'
                                               AND ts.""Timestamp"" + INTERVAL '1830 seconds'
                     ORDER BY ABS(EXTRACT(EPOCH FROM sb.""Timestamp"" - (ts.""Timestamp"" + INTERVAL '1800 seconds')))
                     LIMIT 1)
                ) AS ""PriceAfter30Min""
            FROM ""TradingSignals"" ts
            JOIN ""Symbols"" s ON s.""Id"" = ts.""SymbolId""
            WHERE ts.""Timestamp"" > {{0}}
            AND ABS(ts.""Score"") >= 0.20
            AND (ts.""Reason"" IS NULL OR ts.""Reason"" NOT LIKE '%BACKFILL%')
            AND (
                EXTRACT(HOUR FROM (ts.""Timestamp"" AT TIME ZONE 'UTC') AT TIME ZONE 'America/New_York') > 9
                OR (EXTRACT(HOUR FROM (ts.""Timestamp"" AT TIME ZONE 'UTC') AT TIME ZONE 'America/New_York') = 9
                    AND EXTRACT(MINUTE FROM (ts.""Timestamp"" AT TIME ZONE 'UTC') AT TIME ZONE 'America/New_York') >= 30)
            )
            AND EXTRACT(HOUR FROM (ts.""Timestamp"" AT TIME ZONE 'UTC') AT TIME ZONE 'America/New_York') < 16
            AND NOT (ts.""ObiSmoothed"" = 0 AND ts.""Wobi"" = 0 AND ts.""TickMomentum"" = 0)
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
                TickMomentum = reader.GetDecimal(reader.GetOrdinal("TickMomentum")),
                Ema9 = reader.GetDecimal(reader.GetOrdinal("Ema9")),
                Ema20 = reader.GetDecimal(reader.GetOrdinal("Ema20")),
                Rsi14 = reader.GetDecimal(reader.GetOrdinal("Rsi14")),
                Vwap = reader.GetDecimal(reader.GetOrdinal("Vwap")),
                VolumeRatio = reader.GetDecimal(reader.GetOrdinal("VolumeRatio")),
                Spread = reader.GetDecimal(reader.GetOrdinal("Spread")),
                Hour = reader.GetInt32(reader.GetOrdinal("Hour")),
            };

            var priceAfter1Ord = reader.GetOrdinal("PriceAfter1Min");
            if (!reader.IsDBNull(priceAfter1Ord))
                row.PriceAfter1Min = reader.GetDecimal(priceAfter1Ord);

            var priceAfter5Ord = reader.GetOrdinal("PriceAfter5Min");
            if (!reader.IsDBNull(priceAfter5Ord))
                row.PriceAfter5Min = reader.GetDecimal(priceAfter5Ord);

            var priceAfter15Ord = reader.GetOrdinal("PriceAfter15Min");
            if (!reader.IsDBNull(priceAfter15Ord))
                row.PriceAfter15Min = reader.GetDecimal(priceAfter15Ord);

            var priceAfter30Ord = reader.GetOrdinal("PriceAfter30Min");
            if (!reader.IsDBNull(priceAfter30Ord))
                row.PriceAfter30Min = reader.GetDecimal(priceAfter30Ord);

            // Multi-horizon weighted outcome — approximates actual exit time distribution
            decimal? outcomePrice = null;
            if (row.PriceAfter5Min.HasValue && row.PriceAfter15Min.HasValue && row.PriceAfter30Min.HasValue)
            {
                outcomePrice = row.PriceAfter5Min.Value * 0.20m
                             + row.PriceAfter15Min.Value * 0.40m
                             + row.PriceAfter30Min.Value * 0.40m;
            }
            else if (row.PriceAfter5Min.HasValue && row.PriceAfter15Min.HasValue)
            {
                outcomePrice = row.PriceAfter5Min.Value * 0.30m
                             + row.PriceAfter15Min.Value * 0.70m;
            }
            else if (row.PriceAfter5Min.HasValue)
            {
                outcomePrice = row.PriceAfter5Min.Value;
            }
            else if (row.PriceAfter15Min.HasValue)
            {
                outcomePrice = row.PriceAfter15Min.Value;
            }
            else
            {
                outcomePrice = row.PriceAfter30Min;
            }
            if (outcomePrice.HasValue)
            {
                decimal priceDelta = outcomePrice.Value - row.Price;
                // BUY signal (Type=1): win if price went up. SELL signal (Type=2): win if price went down.
                row.IsWin = row.SignalType == 1 ? priceDelta > 0 : priceDelta < 0;
                row.PnlPer100Shares = (row.SignalType == 1 ? priceDelta : -priceDelta) * SimulationShares;
            }

            rows.Add(row);
        }

        // Filter to only rows with at least a 5-min outcome (rows with only 1-min may cross data gaps)
        return rows.Where(r => r.PriceAfter5Min.HasValue).ToList();
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
            config.UsedDefaultWeights = true;
            return config;
        }

        // ═══════════════════════════════════════════════════════════
        // WALK-FORWARD SPLIT: Train on first 75%, validate on last 25%.
        // Data is already time-ordered (ORDER BY Timestamp in SQL).
        // This prevents overfitting by evaluating on unseen future data.
        // ═══════════════════════════════════════════════════════════
        int trainSize = (int)(rows.Count * 0.75m);
        var trainRows = rows.Take(trainSize).ToList();
        var valRows = rows.Skip(trainSize).ToList();

        _logger.LogInformation("  {Ticker}: walk-forward split — train={TrainCount} val={ValCount}",
            ticker, trainRows.Count, valRows.Count);

        // Step 1: Overall win rate (from all data — for reporting only)
        int wins = rows.Count(r => r.IsWin);
        config.OverallWinRate = (decimal)wins / rows.Count;

        // Step 2: Find optimal weights via hill-climbing ON TRAINING SET ONLY
        decimal[] optimizedWeights = OptimizeWeights(trainRows);

        // Step 3: Evaluate on validation set (out-of-sample)
        decimal trainPnl = ComputeTotalProfit(trainRows, optimizedWeights);
        decimal valPnl = ComputeTotalProfit(valRows, optimizedWeights);
        decimal defaultValPnl = ComputeTotalProfit(valRows, DefaultWeights);

        int valWins = valRows.Count(r =>
        {
            decimal score = ComputeWeightedScore(r, optimizedWeights);
            bool wouldTrade = (r.SignalType == 1 && score >= 0.30m)
                           || (r.SignalType == 2 && score <= -0.30m);
            return wouldTrade && r.IsWin;
        });
        int valTrades = valRows.Count(r =>
        {
            decimal score = ComputeWeightedScore(r, optimizedWeights);
            return (r.SignalType == 1 && score >= 0.30m)
                || (r.SignalType == 2 && score <= -0.30m);
        });

        config.TrainingPnl = trainPnl;
        config.ValidationPnl = valPnl;
        config.ValidationSamples = valRows.Count;
        config.ValidationWinRate = valTrades > 0 ? (decimal)valWins / valTrades : 0;

        _logger.LogWarning(
            "  {Ticker} walk-forward: TrainPnl=${TrainPnl:F2} ValPnl=${ValPnl:F2} " +
            "DefaultValPnl=${DefaultValPnl:F2} ValWinRate={ValWinRate:P1} ({ValTrades} trades)",
            ticker, trainPnl, valPnl, defaultValPnl, config.ValidationWinRate, valTrades);

        // Step 4: OVERFIT GUARD — If optimized weights lose money on validation
        // AND default weights do better, fall back to defaults.
        // This is the key anti-overfit mechanism.
        bool overfit = valPnl < 0
                    || (defaultValPnl > valPnl * 2m && defaultValPnl > 0)
                    || (trainPnl > 0 && valPnl > 0 && valPnl < trainPnl * 0.15m);
        if (overfit)
        {
            _logger.LogWarning(
                "  {Ticker}: OVERFIT DETECTED — optimized weights lose ${ValLoss:F2} on validation, " +
                "default weights earn ${DefaultPnl:F2}. Falling back to defaults.",
                ticker, -valPnl, defaultValPnl);

            ApplyDefaultWeights(config);
            config.UsedDefaultWeights = true;

            // Use default weights for threshold/hour analysis too
            FindOptimalThresholds(trainRows, DefaultWeights, config);
            AnalyzeHours(rows, config);  // hours use all data (reporting)
            AnalyzeDirections(rows, config);
        }
        else
        {
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
            config.UsedDefaultWeights = false;

            // Step 5: Find optimal score thresholds ON TRAINING SET,
            // then verify they don't overfit on validation
            FindOptimalThresholds(trainRows, optimizedWeights, config);

            // Validate selected thresholds on held-out data
            const decimal defaultBuyThreshold = 0.35m;
            const decimal defaultSellThreshold = -0.35m;
            decimal threshValPnl = ComputeTotalProfit(valRows, optimizedWeights,
                config.MinScoreToBuy, config.MinScoreToSell);
            decimal threshDefaultPnl = ComputeTotalProfit(valRows, optimizedWeights,
                defaultBuyThreshold, defaultSellThreshold);
            if (threshValPnl <= 0 && threshDefaultPnl > threshValPnl)
            {
                config.MinScoreToBuy = defaultBuyThreshold;
                config.MinScoreToSell = defaultSellThreshold;
                config.MinScoreToExit = Math.Min(defaultBuyThreshold, Math.Abs(defaultSellThreshold)) * 0.6m;
                _logger.LogWarning("{Ticker}: threshold overfit detected, reverting to defaults", ticker);
            }

            // Step 6: Per-hour analysis (uses all data — this is reporting/filtering, not prediction)
            AnalyzeHours(rows, config);

            // Step 7: Direction analysis
            AnalyzeDirections(rows, config);

            // Step 8: Optimize hold time from available outcome horizons
            OptimizeHoldTime(ticker, trainRows, valRows, config);
        }

        _logger.LogWarning(
            "Ticker {Ticker}: WinRate={WinRate:P1} MinBuy={MinBuy:F2} MinSell={MinSell:F2} " +
            "Hold={Hold}s EnableBuy={EnableBuy} EnableSell={EnableSell} " +
            "UsedDefaults={Defaults} ValPnl=${ValPnl:F2} DisabledHours=[{DisabledHours}]",
            ticker, config.OverallWinRate, config.MinScoreToBuy, config.MinScoreToSell,
            config.OptimalHoldSeconds, config.EnableBuy, config.EnableSell,
            config.UsedDefaultWeights, config.ValidationPnl,
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
    /// Only optimizes the 6 L2/tick weights (indices 0-5). Trend, VWAP, Volume,
    /// and RSI (indices 6-9) are fixed at 0 since contextual filters handle them.
    /// </summary>
    private decimal[] OptimizeWeights(List<TrainingRow> rows)
    {
        decimal[] globalBestWeights = (decimal[])DefaultWeights.Clone();
        decimal globalBestProfit = ComputeTotalProfit(rows, globalBestWeights);

        for (int restart = 0; restart < NumRestarts; restart++)
        {
            var weights = (decimal[])DefaultWeights.Clone();
            decimal bestProfit = ComputeTotalProfit(rows, weights);
            var rng = new Random((int)(DateTime.UtcNow.Ticks % int.MaxValue) + restart * 1000);

            for (int iter = 0; iter < IterationsPerRestart; iter++)
            {
                // Only optimize L2/tick indicators (0-5), not macro indicators (6-9)
                int idx = rng.Next(6);

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

            // Track the best result across all restarts
            if (bestProfit > globalBestProfit)
            {
                globalBestProfit = bestProfit;
                globalBestWeights = (decimal[])weights.Clone();
            }
        }

        return globalBestWeights;
    }

    /// <summary>
    /// Normalize the 6 active L2/tick weights (indices 0-5) to sum to 1.0.
    /// Indices 6-9 (Trend, VWAP, Volume, RSI) are kept at 0 — contextual filters handle them.
    /// </summary>
    private static void NormalizeWeights(decimal[] weights)
    {
        // Only normalize the 6 active weights (0-5); indices 6-9 stay at 0
        const int activeCount = 6;
        decimal absSum = 0;
        for (int i = 0; i < activeCount; i++)
            absSum += Math.Abs(weights[i]);

        if (absSum > 0 && absSum != 1.0m)
        {
            for (int i = 0; i < activeCount; i++)
                weights[i] = weights[i] / absSum;
        }
    }

    /// <summary>
    /// Compute the composite score for a training row using given weights.
    /// All 10 indicators are now available from the DB. Weights 6-9 (Trend, VWAP,
    /// Volume, RSI) are fixed at 0 since contextual filters handle them,
    /// but included for correctness if the optimizer ever assigns weight.
    /// </summary>
    private static decimal ComputeCompositeScore(TrainingRow row, decimal[] weights)
    {
        // Compute derived scores matching MarketMicrostructureAnalyzer formulas
        decimal trendScore = row.Ema9 > row.Ema20 ? 1m : row.Ema9 < row.Ema20 ? -1m : 0m;
        decimal vwapScore = row.Price > 0 && row.Vwap > 0
            ? Math.Clamp((row.Price - row.Vwap) / row.Vwap * 10m, -1m, 1m) : 0;
        decimal volumeScore = Math.Clamp(row.VolumeRatio - 1.0m, -1m, 1m);
        decimal rsiScore = Math.Clamp((row.Rsi14 - 50m) / 50m, -1m, 1m);

        return row.ObiSmoothed * weights[0]
             + row.Wobi * weights[1]
             + row.PressureRoc * weights[2]
             + row.SpreadSignal * weights[3]
             + row.LargeOrderSignal * weights[4]
             + row.TickMomentum * weights[5]
             + trendScore * weights[6]
             + vwapScore * weights[7]
             + volumeScore * weights[8]
             + rsiScore * weights[9];
    }

    /// <summary>
    /// Alias for ComputeCompositeScore — all 10 indicators are now available in TrainingRow.
    /// </summary>
    private static decimal ComputeWeightedScore(TrainingRow row, decimal[] weights)
    {
        return ComputeCompositeScore(row, weights);
    }

    /// <summary>
    /// Multi-horizon weighted outcome — approximates actual exit time distribution.
    /// Weights: 20% 5-min + 40% 15-min + 40% 30-min when all available,
    /// graceful fallback when fewer horizons exist.
    /// </summary>
    private static decimal? ComputeOutcomePrice(TrainingRow row)
    {
        if (row.PriceAfter5Min.HasValue && row.PriceAfter15Min.HasValue && row.PriceAfter30Min.HasValue)
        {
            return row.PriceAfter5Min.Value * 0.20m
                 + row.PriceAfter15Min.Value * 0.40m
                 + row.PriceAfter30Min.Value * 0.40m;
        }
        else if (row.PriceAfter5Min.HasValue && row.PriceAfter15Min.HasValue)
        {
            return row.PriceAfter5Min.Value * 0.30m
                 + row.PriceAfter15Min.Value * 0.70m;
        }
        else if (row.PriceAfter5Min.HasValue)
        {
            return row.PriceAfter5Min.Value;
        }
        else if (row.PriceAfter15Min.HasValue)
        {
            return row.PriceAfter15Min.Value;
        }
        else
        {
            return row.PriceAfter30Min;
        }
    }

    /// <summary>
    /// Simulate trading all rows with given weights, return total P&amp;L after fees.
    /// Only trades when |score| >= 0.30 (moderate threshold for optimization phase).
    /// Uses PriceAfter30Min (primary) with fallback chain 15Min/5Min for swing-style hold times.
    ///
    /// REALISTIC COSTS:
    /// - Commission: $0 (Webull and Questrade are zero-commission for US equities)
    /// - Spread cost: full spread per round-trip (you cross the spread on entry and exit)
    ///   Verified with midprice outcomes, so deduct half-spread per side = full spread total.
    /// - Fill rate: limit orders in wide-spread conditions may not fill. Apply a penalty
    ///   based on spread relative to price (wider spread → lower fill probability).
    /// </summary>
    private static decimal ComputeTotalProfit(List<TrainingRow> rows, decimal[] weights)
    {
        decimal totalPnl = 0;
        const decimal threshold = 0.30m;

        foreach (var row in rows)
        {
            decimal? outcomePrice = ComputeOutcomePrice(row);
            if (!outcomePrice.HasValue) continue;

            decimal score = ComputeWeightedScore(row, weights);
            bool wouldTrade = (row.SignalType == 1 && score >= threshold)
                           || (row.SignalType == 2 && score <= -threshold);

            if (wouldTrade)
            {
                // Fill rate penalty: wider spreads → lower probability of limit order filling.
                // Spread as % of price: if > 0.1%, apply proportional fill-rate reduction.
                // This prevents the optimizer from favoring trades in illiquid conditions.
                decimal spreadPct = row.Price > 0 ? row.Spread / row.Price : 0;
                decimal fillProbability = spreadPct <= 0.001m ? 1.0m       // Tight spread: ~100% fill
                    : spreadPct <= 0.003m ? 0.85m                          // Medium spread: ~85% fill
                    : 0.70m;                                               // Wide spread: ~70% fill

                // P&L = price movement * shares - spread cost - commission
                decimal priceDelta = outcomePrice.Value - row.Price;
                decimal tradePnl = (row.SignalType == 1 ? priceDelta : -priceDelta) * SimulationShares;

                // Deduct spread cost: outcome is measured at midprice, but real entry/exit
                // crosses the spread. Half-spread on entry + half-spread on exit = full spread.
                decimal spreadCost = row.Spread * SimulationShares;

                // Apply fill probability as expectation adjustment
                totalPnl += (tradePnl - spreadCost - CommissionPerTrade * 2) * fillProbability;
            }
        }

        return totalPnl;
    }

    /// <summary>
    /// Overload that accepts separate buy and sell thresholds for threshold validation.
    /// </summary>
    private static decimal ComputeTotalProfit(List<TrainingRow> rows, decimal[] weights,
        decimal buyThreshold, decimal sellThreshold)
    {
        decimal totalPnl = 0;

        foreach (var row in rows)
        {
            decimal? outcomePrice = ComputeOutcomePrice(row);
            if (!outcomePrice.HasValue) continue;

            decimal score = ComputeWeightedScore(row, weights);
            bool wouldTrade = (row.SignalType == 1 && score >= buyThreshold)
                           || (row.SignalType == 2 && score <= sellThreshold);

            if (wouldTrade)
            {
                decimal spreadPct = row.Price > 0 ? row.Spread / row.Price : 0;
                decimal fillProbability = spreadPct <= 0.001m ? 1.0m
                    : spreadPct <= 0.003m ? 0.85m
                    : 0.70m;

                decimal priceDelta = outcomePrice.Value - row.Price;
                decimal tradePnl = (row.SignalType == 1 ? priceDelta : -priceDelta) * SimulationShares;
                decimal spreadCost = row.Spread * SimulationShares;
                totalPnl += (tradePnl - spreadCost - CommissionPerTrade * 2) * fillProbability;
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
            // Simulate BUY trades with realistic spread + fill-rate costs
            var buyTrades = rows.Where(r => r.SignalType == 1).ToList();
            decimal buyPnl = 0;
            int buyCount = 0;
            foreach (var row in buyTrades)
            {
                decimal? outcomePrice = ComputeOutcomePrice(row);
                decimal score = ComputeWeightedScore(row, weights);
                if (score >= threshold && outcomePrice.HasValue)
                {
                    decimal delta = outcomePrice.Value - row.Price;
                    decimal spreadCost = row.Spread * SimulationShares;
                    buyPnl += delta * SimulationShares - spreadCost - CommissionPerTrade * 2;
                    buyCount++;
                }
            }
            decimal buyPnlPerTrade = buyCount > 0 ? buyPnl / buyCount : 0;
            if (buyCount >= 5 && buyPnlPerTrade > bestBuyPnlPerTrade)
            {
                bestBuyPnlPerTrade = buyPnlPerTrade;
                bestBuyThreshold = threshold;
            }

            // Simulate SELL trades with realistic spread + fill-rate costs
            var sellTrades = rows.Where(r => r.SignalType == 2).ToList();
            decimal sellPnl = 0;
            int sellCount = 0;
            foreach (var row in sellTrades)
            {
                decimal? outcomePrice = ComputeOutcomePrice(row);
                decimal score = ComputeWeightedScore(row, weights);
                if (score <= -threshold && outcomePrice.HasValue)
                {
                    decimal delta = row.Price - outcomePrice.Value;
                    decimal spreadCost = row.Spread * SimulationShares;
                    sellPnl += delta * SimulationShares - spreadCost - CommissionPerTrade * 2;
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
            if (hourRows.Count < 10)
            {
                config.HourlyAdjustments[hour] = new HourlyAdjustment
                {
                    EnableTrading = true,
                    ScoreMultiplier = 1.0m,
                    WinRate = 0,
                };
                continue;
            }

            var hourWinRows = hourRows.Where(r => r.IsWin).ToList();
            var hourLossRows = hourRows.Where(r => !r.IsWin && r.PnlPer100Shares != 0).ToList();
            decimal winRate = (decimal)hourWinRows.Count / hourRows.Count;

            // Expected value gating: average win * P(win) - average loss * P(loss)
            decimal avgWinPnl = hourWinRows.Any() ? hourWinRows.Average(r => r.PnlPer100Shares) : 0;
            decimal avgLossPnl = hourLossRows.Any() ? hourLossRows.Average(r => Math.Abs(r.PnlPer100Shares)) : 0;
            decimal expectedValue = avgWinPnl * winRate - avgLossPnl * (1m - winRate);

            var adj = new HourlyAdjustment
            {
                WinRate = Math.Round(winRate, 4),
                EnableTrading = expectedValue > 0 && hourRows.Count >= 10,
            };

            if (winRate > 0.60m)
                adj.ScoreMultiplier = 1.2m; // Boost signals in high-win hours
            else if (winRate < 0.50m)
                adj.ScoreMultiplier = 0.7m; // Dampen signals in low-win hours
            else
                adj.ScoreMultiplier = 1.0m;

            config.HourlyAdjustments[hour] = adj;

            _logger.LogInformation(
                "  {Ticker} hour {Hour}: {Count} samples, WinRate={WinRate:P1}, EV=${EV:F2}, Multiplier={Mult:F1}, Enabled={Enabled}",
                config.Ticker, hour, hourRows.Count, winRate, expectedValue, adj.ScoreMultiplier, adj.EnableTrading);
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
            var buyWins = buyRows.Where(r => r.IsWin).ToList();
            var buyLosses = buyRows.Where(r => !r.IsWin && r.PnlPer100Shares != 0).ToList();
            decimal buyWinRate = (decimal)buyWins.Count / buyRows.Count;
            decimal avgBuyWinPnl = buyWins.Any() ? buyWins.Average(r => r.PnlPer100Shares) : 0;
            decimal avgBuyLossPnl = buyLosses.Any() ? buyLosses.Average(r => Math.Abs(r.PnlPer100Shares)) : 0;
            decimal buyEV = avgBuyWinPnl * buyWinRate - avgBuyLossPnl * (1m - buyWinRate);
            config.EnableBuy = buyEV > 0 && buyRows.Count >= 10;
            _logger.LogInformation("  {Ticker} BUY: {Count} samples, WinRate={WinRate:P1}, EV=${EV:F2}, Enabled={Enabled}",
                config.Ticker, buyRows.Count, buyWinRate, buyEV, config.EnableBuy);
        }

        if (sellRows.Count >= 10)
        {
            var sellWins = sellRows.Where(r => r.IsWin).ToList();
            var sellLosses = sellRows.Where(r => !r.IsWin && r.PnlPer100Shares != 0).ToList();
            decimal sellWinRate = (decimal)sellWins.Count / sellRows.Count;
            decimal avgSellWinPnl = sellWins.Any() ? sellWins.Average(r => r.PnlPer100Shares) : 0;
            decimal avgSellLossPnl = sellLosses.Any() ? sellLosses.Average(r => Math.Abs(r.PnlPer100Shares)) : 0;
            decimal sellEV = avgSellWinPnl * sellWinRate - avgSellLossPnl * (1m - sellWinRate);
            config.EnableSell = sellEV > 0 && sellRows.Count >= 10;
            _logger.LogInformation("  {Ticker} SELL: {Count} samples, WinRate={WinRate:P1}, EV=${EV:F2}, Enabled={Enabled}",
                config.Ticker, sellRows.Count, sellWinRate, sellEV, config.EnableSell);
        }
    }

    #endregion

    #region Step 5: Hold Time Optimization

    /// <summary>
    /// Determine optimal hold time by comparing P&L at different horizons.
    /// Candidate horizons: 300s (5min), 600s (10min), 900s (15min), 1200s (20min), 1800s (30min).
    /// Uses training data for discovery, validation data for confirmation.
    /// Falls back to 1200s default if best candidate loses on validation.
    /// </summary>
    private void OptimizeHoldTime(string ticker, List<TrainingRow> trainRows, List<TrainingRow> valRows, TickerModelConfig config)
    {
        var candidates = new[] { (300, "5min"), (600, "10min"), (900, "15min"), (1200, "20min"), (1800, "30min") };
        decimal bestTrainPnl = decimal.MinValue;
        int bestHoldSeconds = 1200; // default matches ~19 min weighted training horizon

        foreach (var (holdSec, label) in candidates)
        {
            decimal pnl = ComputeHoldPnl(trainRows, holdSec);
            _logger.LogDebug("  {Ticker} hold {Label}: trainPnl=${Pnl:F2}", ticker, label, pnl);
            if (pnl > bestTrainPnl)
            {
                bestTrainPnl = pnl;
                bestHoldSeconds = holdSec;
            }
        }

        // Validate: if best hold time loses money on validation, fall back to 1200s
        decimal valPnl = ComputeHoldPnl(valRows, bestHoldSeconds);
        decimal defaultValPnl = ComputeHoldPnl(valRows, 1200);

        if (valPnl <= 0 && defaultValPnl > valPnl)
        {
            _logger.LogWarning("  {Ticker}: hold time {Best}s overfits (valPnl=${ValPnl:F2} vs default=${DefaultPnl:F2}), using default 1200s",
                ticker, bestHoldSeconds, valPnl, defaultValPnl);
            bestHoldSeconds = 1200;
            valPnl = defaultValPnl;
        }

        config.OptimalHoldSeconds = bestHoldSeconds;
        _logger.LogWarning("  {Ticker}: optimal hold time = {Hold}s (trainPnl=${TrainPnl:F2} valPnl=${ValPnl:F2})",
            ticker, bestHoldSeconds, bestTrainPnl, valPnl);
    }

    /// <summary>
    /// Compute P&L for a given hold horizon. Maps hold seconds to the closest
    /// available outcome column (PriceAfter5Min/15Min/30Min) using interpolation.
    /// Includes spread costs and fill probability (same as ComputeTotalProfit).
    /// </summary>
    private static decimal ComputeHoldPnl(List<TrainingRow> rows, int holdSeconds)
    {
        decimal totalPnl = 0;
        const decimal threshold = 0.30m;

        foreach (var row in rows)
        {
            // Map hold duration to outcome price using closest available columns.
            // 300s → 5min, 600s → blend(5min,15min), 900s → 15min,
            // 1200s → blend(15min,30min), 1800s → 30min.
            decimal? outcomePrice = holdSeconds switch
            {
                <= 300 => row.PriceAfter5Min,
                <= 600 => row.PriceAfter5Min.HasValue && row.PriceAfter15Min.HasValue
                    ? row.PriceAfter5Min.Value * 0.50m + row.PriceAfter15Min.Value * 0.50m
                    : row.PriceAfter5Min ?? row.PriceAfter15Min,
                <= 900 => row.PriceAfter15Min ?? row.PriceAfter5Min,
                <= 1200 => row.PriceAfter15Min.HasValue && row.PriceAfter30Min.HasValue
                    ? row.PriceAfter15Min.Value * 0.50m + row.PriceAfter30Min.Value * 0.50m
                    : row.PriceAfter15Min ?? row.PriceAfter30Min,
                _ => row.PriceAfter30Min ?? row.PriceAfter15Min
            };

            if (!outcomePrice.HasValue) continue;

            decimal score = Math.Abs(row.Score);
            bool wouldTrade = score >= threshold;
            if (!wouldTrade) continue;

            // Same spread/fill logic as ComputeTotalProfit
            decimal spreadPct = row.Price > 0 ? row.Spread / row.Price : 0;
            decimal fillProbability = spreadPct <= 0.001m ? 1.0m
                : spreadPct <= 0.003m ? 0.85m
                : 0.70m;

            decimal priceDelta = outcomePrice.Value - row.Price;
            decimal tradePnl = (row.SignalType == 1 ? priceDelta : -priceDelta) * SimulationShares;
            decimal spreadCost = row.Spread * SimulationShares;
            totalPnl += (tradePnl - spreadCost - CommissionPerTrade * 2) * fillProbability;
        }
        return totalPnl;
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

    // L2 microstructure indicators (stored as computed scores, [-1, +1])
    public decimal ObiSmoothed { get; set; }
    public decimal Wobi { get; set; }
    public decimal PressureRoc { get; set; }
    public decimal SpreadSignal { get; set; }
    public decimal LargeOrderSignal { get; set; }
    public decimal Imbalance { get; set; }

    // Tick-derived ([-1, +1])
    public decimal TickMomentum { get; set; }

    // Bar-derived (raw values — converted to scores in ComputeCompositeScore)
    public decimal Ema9 { get; set; }
    public decimal Ema20 { get; set; }
    public decimal Rsi14 { get; set; }
    public decimal Vwap { get; set; }
    public decimal VolumeRatio { get; set; }

    public decimal Spread { get; set; } // Raw bid-ask spread in dollars
    public int Hour { get; set; } // Hour of day (ET)
    public decimal? PriceAfter1Min { get; set; }
    public decimal? PriceAfter5Min { get; set; }
    public decimal? PriceAfter15Min { get; set; }
    public decimal? PriceAfter30Min { get; set; }
    public bool IsWin { get; set; }
    public decimal PnlPer100Shares { get; set; }
}

#endregion
