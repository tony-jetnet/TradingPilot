using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingPilot.Symbols;

namespace TradingPilot.Trading;

/// <summary>
/// Analyzes L2 order book data, tick data, and bar-based indicators in real time
/// to generate buy/sell trading signals. Combines microstructure indicators
/// (order book imbalance, weighted imbalance, pressure ROC, spread, large orders)
/// with tick momentum, trend alignment, VWAP position, volume confirmation, and RSI filter.
///
/// When a model_config.json is loaded (from nightly training), uses learned weights
/// and per-hour adjustments instead of fixed defaults. Falls back to defaults if
/// no config file exists.
/// </summary>
public class MarketMicrostructureAnalyzer
{
    private readonly L2BookCache _l2Cache;
    private readonly TickDataCache _tickCache;
    private readonly BarIndicatorCache _barCache;
    private readonly StrategyRuleEvaluator _ruleEvaluator;
    private readonly SwinPredictor _swinPredictor;
    private readonly ILogger<MarketMicrostructureAnalyzer> _logger;

    private readonly ConcurrentDictionary<long, TickerAnalysisState> _state = new();

    // Model config from nightly training (null = use defaults)
    private volatile ModelConfig? _modelConfig;
    private DateTime _modelConfigLoadedAt;
    private DateTime _strategyConfigLoadedAt;
    private FileSystemWatcher? _configWatcher;
    private FileSystemWatcher? _strategyWatcher;
    private const string ModelConfigPath = @"D:\Third-Parties\WebullHook\model_config.json";
    private const string StrategyConfigPath = @"D:\Third-Parties\WebullHook\strategy_rules.json";

    // Default L2 indicator weights (total: 0.50)
    private const decimal WeightObi = 0.15m;
    private const decimal WeightWobi = 0.15m;
    private const decimal WeightPressureRoc = 0.10m;
    private const decimal WeightSpread = 0.05m;
    private const decimal WeightLargeOrder = 0.05m;

    // Default new indicator weights (total: 0.50)
    private const decimal WeightTickMomentum = 0.10m;
    private const decimal WeightTrendAlignment = 0.15m;
    private const decimal WeightVwapPosition = 0.10m;
    private const decimal WeightVolumeConfirmation = 0.10m;
    private const decimal WeightRsiFilter = 0.05m;

    // Signal thresholds
    private const decimal StrongBuyThreshold = 0.40m;
    private const decimal ModerateBuyThreshold = 0.20m;
    private const decimal WeakBuyThreshold = 0.10m;
    private const decimal StrongSellThreshold = -0.40m;
    private const decimal ModerateSellThreshold = -0.20m;
    private const decimal WeakSellThreshold = -0.10m;

    // Minimum interval between signals to avoid spam (2s to avoid suppressing reversals)
    private static readonly TimeSpan MinSignalInterval = TimeSpan.FromSeconds(2);

    // Window sizes for rolling calculations
    private const int ShortWindow = 10;
    private const int LongWindow = 30;
    private const int HistorySize = 100;
    private const int TopLevels = 5;
    private const decimal LargeOrderMultiplier = 3.0m;

    // Eastern Time zone for hourly adjustments
    private static readonly TimeZoneInfo EasternTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    public MarketMicrostructureAnalyzer(
        L2BookCache l2Cache,
        TickDataCache tickCache,
        BarIndicatorCache barCache,
        StrategyRuleEvaluator ruleEvaluator,
        SwinPredictor swinPredictor,
        ILogger<MarketMicrostructureAnalyzer> logger)
    {
        _l2Cache = l2Cache;
        _tickCache = tickCache;
        _barCache = barCache;
        _ruleEvaluator = ruleEvaluator;
        _swinPredictor = swinPredictor;
        _logger = logger;

        // Load model config on startup and watch for changes
        LoadModelConfig();
        WatchModelConfig();
        LoadStrategyConfig();
        WatchStrategyConfig();
    }

    /// <summary>
    /// The currently loaded model config (null if none loaded).
    /// Exposed for PaperTradingExecutor to read thresholds.
    /// </summary>
    public ModelConfig? CurrentModelConfig => _modelConfig;

    /// <summary>
    /// The currently loaded AI strategy config (null if none loaded).
    /// Exposed for PaperTradingExecutor to read per-rule parameters.
    /// </summary>
    public StrategyRuleEvaluator RuleEvaluator => _ruleEvaluator;

    /// <summary>
    /// Load model_config.json from disk. Called on startup and when the file changes.
    /// Thread-safe: the volatile field is atomically replaced.
    /// </summary>
    public void LoadModelConfig()
    {
        try
        {
            if (!File.Exists(ModelConfigPath))
            {
                _logger.LogInformation("No model config file at {Path}, using default weights", ModelConfigPath);
                return;
            }

            string json = File.ReadAllText(ModelConfigPath);
            var config = JsonSerializer.Deserialize<ModelConfig>(json);
            if (config == null || config.Tickers.Count == 0)
            {
                _logger.LogWarning("Model config file is empty or invalid at {Path}", ModelConfigPath);
                return;
            }

            _modelConfig = config;
            _modelConfigLoadedAt = DateTime.UtcNow;
            _logger.LogWarning(
                "Loaded model config: trained={TrainedAt}, {TickerCount} tickers, {Rows} training rows",
                config.TrainedAt, config.Tickers.Count, config.TrainingRows);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load model config from {Path}", ModelConfigPath);
        }
    }

    private void WatchModelConfig()
    {
        try
        {
            var dir = Path.GetDirectoryName(ModelConfigPath);
            var fileName = Path.GetFileName(ModelConfigPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            _configWatcher = new FileSystemWatcher(dir, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            _configWatcher.Changed += (_, _) =>
            {
                // Debounce: only reload if last load was >2s ago
                if ((DateTime.UtcNow - _modelConfigLoadedAt).TotalSeconds > 2)
                {
                    _logger.LogInformation("Model config file changed, reloading...");
                    LoadModelConfig();
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set up model config file watcher");
        }
    }

    /// <summary>
    /// Load strategy_rules.json (AI-generated conditional rules) from disk.
    /// Called at startup, on file change, and periodically to ensure freshness.
    /// </summary>
    public void LoadStrategyConfig()
    {
        try
        {
            if (!File.Exists(StrategyConfigPath))
            {
                _logger.LogInformation("No strategy rules file at {Path}, using weight-based scoring", StrategyConfigPath);
                return;
            }

            // Skip if file hasn't changed since last load
            var fileModified = File.GetLastWriteTimeUtc(StrategyConfigPath);
            if (_strategyConfigLoadedAt >= fileModified)
                return;

            string json = File.ReadAllText(StrategyConfigPath);
            var config = JsonSerializer.Deserialize<StrategyConfig>(json);
            if (config == null || config.Symbols.Count == 0)
            {
                _logger.LogWarning("Strategy rules file is empty or invalid at {Path}", StrategyConfigPath);
                return;
            }

            _ruleEvaluator.SetConfig(config);
            _strategyConfigLoadedAt = DateTime.UtcNow;
            _logger.LogWarning(
                "Loaded strategy rules: generated={GeneratedAt}, {SymbolCount} symbols, {RuleCount} total rules",
                config.GeneratedAt, config.Symbols.Count,
                config.Symbols.Values.Sum(s => s.Rules.Count));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load strategy rules from {Path}", StrategyConfigPath);
        }
    }

    /// <summary>
    /// Check if strategy rules need reloading (file changed since last load).
    /// Called periodically from AnalyzeSnapshot to ensure rules are fresh
    /// even if FileSystemWatcher misses an event.
    /// </summary>
    private void EnsureStrategyConfigFresh()
    {
        // Check at most once per minute
        if ((DateTime.UtcNow - _strategyConfigLoadedAt).TotalMinutes < 1)
            return;

        try
        {
            if (!File.Exists(StrategyConfigPath)) return;
            var fileModified = File.GetLastWriteTimeUtc(StrategyConfigPath);
            if (fileModified > _strategyConfigLoadedAt)
            {
                _logger.LogInformation("Strategy rules file is newer than loaded version, reloading...");
                LoadStrategyConfig();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check/reload strategy config from {Path}", StrategyConfigPath);
        }
    }

    private void WatchStrategyConfig()
    {
        try
        {
            var dir = Path.GetDirectoryName(StrategyConfigPath);
            var fileName = Path.GetFileName(StrategyConfigPath);
            if (string.IsNullOrEmpty(dir))  return;

            // Create directory if it doesn't exist (so watcher can start)
            Directory.CreateDirectory(dir);

            _strategyWatcher = new FileSystemWatcher(dir, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            _strategyWatcher.Changed += (_, _) =>
            {
                // Debounce: only reload if last load was >2s ago
                if ((DateTime.UtcNow - _strategyConfigLoadedAt).TotalSeconds > 2)
                {
                    _logger.LogInformation("Strategy rules file changed, reloading...");
                    LoadStrategyConfig();
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set up strategy rules file watcher");
        }
    }

    /// <summary>
    /// Analyze a new L2 book snapshot and optionally produce a trading signal.
    /// Called on every new snapshot; returns null if no actionable signal.
    /// </summary>
    public TradingSignal? AnalyzeSnapshot(long tickerId, string ticker, SymbolBookSnapshot snapshot)
    {
        var state = _state.GetOrAdd(tickerId, _ => new TickerAnalysisState());

        // Update rolling state
        UpdateState(state, snapshot);

        // Need minimum history to produce meaningful signals
        if (state.RecentImbalances.Count < ShortWindow)
            return null;

        // Periodically check if strategy rules file has been updated
        EnsureStrategyConfigFresh();

        // ═══════════════════════════════════════════════════════════
        // STAGE 1: Try AI-generated rule evaluation (strategy_rules.json)
        // ═══════════════════════════════════════════════════════════
        int etHour = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, EasternTimeZone).Hour;
        var tickData = _tickCache.GetData(tickerId);
        var barIndicatorsForRules = _barCache.GetIndicators(tickerId);

        var ruleMatch = _ruleEvaluator.FindMatchingRule(tickerId, ticker, etHour, new IndicatorSnapshot
        {
            Obi = ComputeSmoothedObi(state),
            BookDepthRatio = tickData?.BookDepthRatio ?? 0,
            BidWallSize = tickData?.BidWallSize ?? 0,
            AskWallSize = tickData?.AskWallSize ?? 0,
            BidSweepCost = tickData?.BidSweepCost ?? 0,
            AskSweepCost = tickData?.AskSweepCost ?? 0,
            ImbalanceVelocity = tickData?.ImbalanceVelocity ?? 0,
            SpreadPercentile = tickData?.SpreadPercentile ?? 0.5m,
            TickMomentum = tickData?.TickMomentum ?? 0,
            TrendDirection = barIndicatorsForRules?.TrendDirection ?? 0,
            Rsi14 = barIndicatorsForRules?.Rsi14 ?? 50,
            VolumeRatio = barIndicatorsForRules?.VolumeRatio ?? 1,
            AboveVwap = barIndicatorsForRules?.AboveVwap ?? false,
        });

        if (ruleMatch.HasValue)
        {
            var (rule, _) = ruleMatch.Value;
            var ruleNow = DateTime.UtcNow;

            // Throttle
            if (state.LastSignalTime != default && (ruleNow - state.LastSignalTime) < MinSignalInterval)
                return null;

            var ruleSignalType = rule.Direction == "BUY" ? SignalType.Buy : SignalType.Sell;
            var ruleStrength = rule.Confidence >= 0.70m ? SignalStrength.Strong
                : rule.Confidence >= 0.60m ? SignalStrength.Moderate
                : SignalStrength.Weak;

            var ruleIndicators = new Dictionary<string, decimal>
            {
                ["CompositeScore"] = rule.Confidence * (ruleSignalType == SignalType.Sell ? -1 : 1),
                ["RuleConfidence"] = rule.Confidence,
                ["RuleHoldSeconds"] = rule.HoldSeconds,
                ["RuleStopLoss"] = rule.StopLoss,
            };

            if (barIndicatorsForRules != null)
            {
                ruleIndicators["EMA9"] = Math.Round(barIndicatorsForRules.Ema9, 4);
                ruleIndicators["EMA20"] = Math.Round(barIndicatorsForRules.Ema20, 4);
                ruleIndicators["VWAP"] = Math.Round(barIndicatorsForRules.Vwap, 4);
                ruleIndicators["RSI14"] = Math.Round(barIndicatorsForRules.Rsi14, 2);
                ruleIndicators["TrendDir"] = barIndicatorsForRules.TrendDirection;
            }

            var ruleSignal = new TradingSignal
            {
                TickerId = tickerId,
                Ticker = ticker,
                Timestamp = ruleNow,
                Type = ruleSignalType,
                Strength = ruleStrength,
                Price = snapshot.MidPrice,
                Reason = $"[RULE {rule.Id}] {rule.Name} (conf={rule.Confidence:F2})",
                Indicators = ruleIndicators,
            };

            state.LastSignal = ruleSignal;
            state.LastSignalTime = ruleNow;

            _logger.LogWarning(
                "{Strength} {SignalType} signal for {Ticker} at {Price:F4} | RULE={RuleId} \"{RuleName}\" conf={Confidence:F2} hold={Hold}s",
                ruleStrength, ruleSignalType, ticker, snapshot.MidPrice,
                rule.Id, rule.Name, rule.Confidence, rule.HoldSeconds);

            return ruleSignal;
        }

        // ═══════════════════════════════════════════════════════════
        // STAGE 2: Swin vision model on L2 heatmap (or weighted scoring fallback)
        // ═══════════════════════════════════════════════════════════

        // Check if we have learned config for this ticker
        var tickerConfig = _modelConfig?.Tickers.GetValueOrDefault(tickerId);

        // Check hourly trading enablement from model config
        if (tickerConfig != null)
        {
            if (tickerConfig.HourlyAdjustments.TryGetValue(etHour, out var hourAdj) && !hourAdj.EnableTrading)
                return null; // Skip this hour — historically unprofitable
        }

        decimal compositeScore;
        var barIndicators = _barCache.GetIndicators(tickerId);
        bool usedSwin = false;

        // Try Swin model first — it learns directly from raw L2 heatmaps
        var swinSnapshots = _l2Cache.GetSnapshots(tickerId, 300);
        var swinPrediction = _swinPredictor.Predict(swinSnapshots);

        if (swinPrediction != null && swinPrediction.Confidence >= 0.40f)
        {
            // Use Swin model output as composite score
            compositeScore = (decimal)swinPrediction.Score; // [-1, +1]
            usedSwin = true;

            _logger.LogDebug(
                "Swin prediction for {Ticker}: {Class} score={Score:F3} " +
                "conf={Conf:F3} P(up)={Up:F3} P(down)={Down:F3}",
                ticker, swinPrediction.PredictedClass, swinPrediction.Score,
                swinPrediction.Confidence, swinPrediction.UpProbability,
                swinPrediction.DownProbability);
        }
        else
        {
            // Fallback to weighted scoring (original Stage 2 logic)
            decimal obiScore = ComputeSmoothedObi(state);
            decimal wobiScore = ComputeWeightedObi(snapshot);
            decimal pressureRocScore = ComputePressureRoc(state);
            decimal spreadScore = ComputeSpreadSignal(state, snapshot);
            decimal largeOrderScore = ComputeLargeOrderSignal(snapshot, state);
            decimal tickMomentumScore = ComputeTickMomentumScore(tickerId);
            decimal trendAlignmentScore = ComputeTrendAlignmentScore(barIndicators);
            decimal vwapPositionScore = ComputeVwapPositionScore(barIndicators, snapshot.MidPrice);
            decimal volumeConfirmationScore = ComputeVolumeConfirmationScore(barIndicators);
            decimal rsiFilterScore = ComputeRsiFilterScore(barIndicators);

            if (tickerConfig != null)
            {
                compositeScore =
                    obiScore * tickerConfig.WeightObi +
                    wobiScore * tickerConfig.WeightWobi +
                    pressureRocScore * tickerConfig.WeightPressureRoc +
                    spreadScore * tickerConfig.WeightSpread +
                    largeOrderScore * tickerConfig.WeightLargeOrder +
                    tickMomentumScore * tickerConfig.WeightTickMomentum +
                    trendAlignmentScore * tickerConfig.WeightTrend +
                    vwapPositionScore * tickerConfig.WeightVwap +
                    volumeConfirmationScore * tickerConfig.WeightVolume +
                    rsiFilterScore * tickerConfig.WeightRsi;
            }
            else
            {
                compositeScore =
                    obiScore * WeightObi +
                    wobiScore * WeightWobi +
                    pressureRocScore * WeightPressureRoc +
                    spreadScore * WeightSpread +
                    largeOrderScore * WeightLargeOrder +
                    tickMomentumScore * WeightTickMomentum +
                    trendAlignmentScore * WeightTrendAlignment +
                    vwapPositionScore * WeightVwapPosition +
                    volumeConfirmationScore * WeightVolumeConfirmation +
                    rsiFilterScore * WeightRsiFilter;
            }

            // Apply contextual filters (only for weighted scoring — Swin already learned these)
            if (barIndicators != null)
            {
                decimal preFilterScore = compositeScore;

                if (barIndicators.TrendDirection == -1 && compositeScore > 0)
                    compositeScore *= 0.5m;
                else if (barIndicators.TrendDirection == 1 && compositeScore < 0)
                    compositeScore *= 0.5m;

                if (!barIndicators.AboveVwap && compositeScore > 0)
                    compositeScore *= 0.7m;
                else if (barIndicators.AboveVwap && compositeScore < 0)
                    compositeScore *= 0.7m;

                if (barIndicators.HighVolume)
                    compositeScore *= 1.3m;

                if (barIndicators.OverboughtRsi && compositeScore > 0)
                    compositeScore *= 0.5m;
                else if (barIndicators.OversoldRsi && compositeScore < 0)
                    compositeScore *= 0.5m;

                if (preFilterScore != 0)
                {
                    decimal minAllowed = preFilterScore * 0.50m;
                    if (preFilterScore > 0 && compositeScore < minAllowed)
                        compositeScore = minAllowed;
                    else if (preFilterScore < 0 && compositeScore > minAllowed)
                        compositeScore = minAllowed;
                }
            }
        }

        // Apply hourly score multiplier from model config
        if (tickerConfig != null)
        {
            if (tickerConfig.HourlyAdjustments.TryGetValue(etHour, out var hourAdj2))
                compositeScore *= hourAdj2.ScoreMultiplier;
        }

        // Determine signal type and strength
        var (signalType, strength) = ClassifySignal(compositeScore);

        // Hold signals are not emitted
        if (signalType == SignalType.Hold)
            return null;

        // Check direction enablement from model config
        if (tickerConfig != null)
        {
            if (signalType == SignalType.Buy && !tickerConfig.EnableBuy)
                return null;
            if (signalType == SignalType.Sell && !tickerConfig.EnableSell)
                return null;
        }

        // Throttle: enforce minimum interval between signals
        var now = DateTime.UtcNow;
        if (state.LastSignalTime != default && (now - state.LastSignalTime) < MinSignalInterval)
            return null;

        var indicators = new Dictionary<string, decimal>
        {
            ["CompositeScore"] = Math.Round(compositeScore, 4),
        };

        if (usedSwin && swinPrediction != null)
        {
            indicators["SwinUp"] = Math.Round((decimal)swinPrediction.UpProbability, 4);
            indicators["SwinDown"] = Math.Round((decimal)swinPrediction.DownProbability, 4);
            indicators["SwinConf"] = Math.Round((decimal)swinPrediction.Confidence, 4);
            indicators["Source"] = 1; // 1 = Swin model
        }
        else
        {
            indicators["Source"] = 0; // 0 = weighted scoring
        }

        // Add bar indicator context if available
        if (barIndicators != null)
        {
            indicators["EMA9"] = Math.Round(barIndicators.Ema9, 4);
            indicators["EMA20"] = Math.Round(barIndicators.Ema20, 4);
            indicators["VWAP"] = Math.Round(barIndicators.Vwap, 4);
            indicators["RSI14"] = Math.Round(barIndicators.Rsi14, 2);
            indicators["VolumeRatio"] = Math.Round(barIndicators.VolumeRatio, 2);
            indicators["TrendDir"] = barIndicators.TrendDirection;
        }

        string reason = BuildReason(signalType, strength, indicators);

        var signal = new TradingSignal
        {
            TickerId = tickerId,
            Ticker = ticker,
            Timestamp = now,
            Type = signalType,
            Strength = strength,
            Price = snapshot.MidPrice,
            Reason = reason,
            Indicators = indicators,
        };

        state.LastSignal = signal;
        state.LastSignalTime = now;

        // Log strong signals at Warning level for visibility
        if (strength == SignalStrength.Strong)
        {
            if (usedSwin && swinPrediction != null)
            {
                _logger.LogWarning(
                    "STRONG {SignalType} signal for {Ticker} (tickerId={TickerId}) at {Price:F4} | " +
                    "Score={Score:F4} P(up)={PUp:F3} P(down)={PDown:F3} Conf={Conf:F3} [SWIN]",
                    signalType, ticker, tickerId, snapshot.MidPrice,
                    compositeScore, swinPrediction.UpProbability,
                    swinPrediction.DownProbability, swinPrediction.Confidence);
            }
            else
            {
                _logger.LogWarning(
                    "STRONG {SignalType} signal for {Ticker} (tickerId={TickerId}) at {Price:F4} | " +
                    "Score={Score:F4}" +
                    (tickerConfig != null ? " [ML]" : ""),
                    signalType, ticker, tickerId, snapshot.MidPrice, compositeScore);
            }
        }
        else
        {
            _logger.LogInformation(
                "{Strength} {SignalType} signal for {Ticker} at {Price:F4} | Score={Score:F4} " +
                "Trend={Trend} VWAP={VWAP} RSI={RSI:F1}" +
                (usedSwin ? " [SWIN]" : tickerConfig != null ? " [ML]" : ""),
                strength, signalType, ticker, snapshot.MidPrice, compositeScore,
                barIndicators?.TrendDirection ?? 0,
                barIndicators?.AboveVwap == true ? "above" : "below",
                barIndicators?.Rsi14 ?? 50);
        }

        return signal;
    }

    /// <summary>
    /// Re-compute the composite score for a ticker using the latest cached data
    /// and learned weights. No signal generation, no throttling — just the math.
    /// Used by PositionMonitor for continuous exit evaluation.
    /// </summary>
    public decimal ComputeCurrentScore(long tickerId)
    {
        if (!_state.TryGetValue(tickerId, out var state))
            return 0;

        if (state.RecentImbalances.Count < ShortWindow)
            return 0;

        // Get latest L2 snapshot from cache
        var snapshots = _l2Cache.GetSnapshots(tickerId, 1);
        if (snapshots.Count == 0)
            return 0;

        var snapshot = snapshots[^1];

        var tickerConfig = _modelConfig?.Tickers.GetValueOrDefault(tickerId);
        int etHour = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, EasternTimeZone).Hour;

        decimal compositeScore;
        var barIndicators = _barCache.GetIndicators(tickerId);

        // Try Swin model first — must match AnalyzeSnapshot entry logic
        var swinSnapshots = _l2Cache.GetSnapshots(tickerId, 300);
        var swinPrediction = _swinPredictor.Predict(swinSnapshots);

        if (swinPrediction != null && swinPrediction.Confidence >= 0.40f)
        {
            // Use Swin model output directly (no contextual filters — model learned them)
            compositeScore = (decimal)swinPrediction.Score;
        }
        else
        {
            // Fallback to weighted scoring
            decimal obiScore = ComputeSmoothedObi(state);
            decimal wobiScore = ComputeWeightedObi(snapshot);
            decimal pressureRocScore = ComputePressureRoc(state);
            decimal spreadScore = ComputeSpreadSignal(state, snapshot);
            decimal largeOrderScore = ComputeLargeOrderSignal(snapshot, state);
            decimal tickMomentumScore = ComputeTickMomentumScore(tickerId);
            decimal trendAlignmentScore = ComputeTrendAlignmentScore(barIndicators);
            decimal vwapPositionScore = ComputeVwapPositionScore(barIndicators, snapshot.MidPrice);
            decimal volumeConfirmationScore = ComputeVolumeConfirmationScore(barIndicators);
            decimal rsiFilterScore = ComputeRsiFilterScore(barIndicators);

            if (tickerConfig != null)
            {
                compositeScore =
                    obiScore * tickerConfig.WeightObi +
                    wobiScore * tickerConfig.WeightWobi +
                    pressureRocScore * tickerConfig.WeightPressureRoc +
                    spreadScore * tickerConfig.WeightSpread +
                    largeOrderScore * tickerConfig.WeightLargeOrder +
                    tickMomentumScore * tickerConfig.WeightTickMomentum +
                    trendAlignmentScore * tickerConfig.WeightTrend +
                    vwapPositionScore * tickerConfig.WeightVwap +
                    volumeConfirmationScore * tickerConfig.WeightVolume +
                    rsiFilterScore * tickerConfig.WeightRsi;
            }
            else
            {
                compositeScore =
                    obiScore * WeightObi +
                    wobiScore * WeightWobi +
                    pressureRocScore * WeightPressureRoc +
                    spreadScore * WeightSpread +
                    largeOrderScore * WeightLargeOrder +
                    tickMomentumScore * WeightTickMomentum +
                    trendAlignmentScore * WeightTrendAlignment +
                    vwapPositionScore * WeightVwapPosition +
                    volumeConfirmationScore * WeightVolumeConfirmation +
                    rsiFilterScore * WeightRsiFilter;
            }

            // Apply contextual filters (only for weighted scoring — Swin already learned these)
            if (barIndicators != null)
            {
                decimal preFilterScore = compositeScore;

                if (barIndicators.TrendDirection == -1 && compositeScore > 0)
                    compositeScore *= 0.5m;
                else if (barIndicators.TrendDirection == 1 && compositeScore < 0)
                    compositeScore *= 0.5m;

                if (!barIndicators.AboveVwap && compositeScore > 0)
                    compositeScore *= 0.7m;
                else if (barIndicators.AboveVwap && compositeScore < 0)
                    compositeScore *= 0.7m;

                if (barIndicators.HighVolume)
                    compositeScore *= 1.3m;

                if (barIndicators.OverboughtRsi && compositeScore > 0)
                    compositeScore *= 0.5m;
                else if (barIndicators.OversoldRsi && compositeScore < 0)
                    compositeScore *= 0.5m;

                if (preFilterScore != 0)
                {
                    decimal minAllowed = preFilterScore * 0.50m;
                    if (preFilterScore > 0 && compositeScore < minAllowed)
                        compositeScore = minAllowed;
                    else if (preFilterScore < 0 && compositeScore > minAllowed)
                        compositeScore = minAllowed;
                }
            }
        }

        // Apply hourly score multiplier
        if (tickerConfig != null)
        {
            if (tickerConfig.HourlyAdjustments.TryGetValue(etHour, out var hourAdj))
                compositeScore *= hourAdj.ScoreMultiplier;
        }

        return compositeScore;
    }

    private static void UpdateState(TickerAnalysisState state, SymbolBookSnapshot snapshot)
    {
        EnqueueCapped(state.RecentImbalances, snapshot.Imbalance, HistorySize);
        EnqueueCapped(state.RecentSpreads, snapshot.Spread, HistorySize);
        EnqueueCapped(state.RecentMidPrices, snapshot.MidPrice, HistorySize);

        // Track average bid/ask sizes for large order detection
        decimal avgBidSize = snapshot.BidSizes.Length > 0 ? snapshot.BidSizes.Average() : 0;
        decimal avgAskSize = snapshot.AskSizes.Length > 0 ? snapshot.AskSizes.Average() : 0;
        decimal avgLevelSize = (avgBidSize + avgAskSize) / 2;
        EnqueueCapped(state.RecentAvgLevelSizes, avgLevelSize, HistorySize);
    }

    /// <summary>
    /// A. Order Book Imbalance smoothed over last 30 snapshots.
    /// </summary>
    private static decimal ComputeSmoothedObi(TickerAnalysisState state)
    {
        var recent = state.RecentImbalances.TakeLast(LongWindow);
        return recent.Any() ? recent.Average() : 0;
    }

    /// <summary>
    /// B. Weighted Order Book Imbalance: levels closer to mid price
    /// are weighted more heavily (1/distance weighting on top N levels).
    /// </summary>
    private static decimal ComputeWeightedObi(SymbolBookSnapshot snapshot)
    {
        if (snapshot.MidPrice <= 0) return 0;

        decimal weightedBid = 0, weightedAsk = 0;
        decimal totalWeight = 0;

        int bidLevels = Math.Min(TopLevels, snapshot.BidPrices.Length);
        for (int i = 0; i < bidLevels; i++)
        {
            decimal distance = snapshot.MidPrice - snapshot.BidPrices[i];
            if (distance <= 0) distance = 0.0001m; // avoid division by zero
            decimal weight = 1.0m / distance;
            weightedBid += snapshot.BidSizes[i] * weight;
            totalWeight += weight;
        }

        int askLevels = Math.Min(TopLevels, snapshot.AskPrices.Length);
        for (int i = 0; i < askLevels; i++)
        {
            decimal distance = snapshot.AskPrices[i] - snapshot.MidPrice;
            if (distance <= 0) distance = 0.0001m;
            decimal weight = 1.0m / distance;
            weightedAsk += snapshot.AskSizes[i] * weight;
            totalWeight += weight;
        }

        decimal total = weightedBid + weightedAsk;
        if (total == 0) return 0;

        return (weightedBid - weightedAsk) / total;
    }

    /// <summary>
    /// C. Book Pressure Rate of Change: compares short-term (10) average imbalance
    /// to long-term (30) average. Positive = accelerating buy pressure.
    /// </summary>
    private static decimal ComputePressureRoc(TickerAnalysisState state)
    {
        if (state.RecentImbalances.Count < LongWindow) return 0;

        var items = state.RecentImbalances.ToArray();
        decimal shortAvg = items.TakeLast(ShortWindow).Average();
        decimal longAvg = items.TakeLast(LongWindow).Average();

        // Difference is already in [-2, +2] range, normalize to [-1, +1]
        decimal roc = shortAvg - longAvg;
        return Math.Clamp(roc * 2, -1, 1);
    }

    /// <summary>
    /// D. Spread Analysis: narrow spread = bullish confidence, widening = bearish.
    /// Compares current spread to its percentile position in recent history.
    /// Returns negative if spread is unusually wide (uncertainty), positive if narrow.
    /// </summary>
    private static decimal ComputeSpreadSignal(TickerAnalysisState state, SymbolBookSnapshot snapshot)
    {
        if (state.RecentSpreads.Count < ShortWindow) return 0;

        var spreads = state.RecentSpreads.ToArray();
        decimal currentSpread = snapshot.Spread;
        int countBelow = spreads.Count(s => s < currentSpread);
        decimal percentile = (decimal)countBelow / spreads.Length;

        // percentile near 1.0 = spread is wider than most recent values = bearish
        // percentile near 0.0 = spread is tighter than most = bullish
        // Map to [-1, +1]: tight spread = +1, wide spread = -1
        return 1.0m - 2.0m * percentile;
    }

    /// <summary>
    /// E. Large Order Detection: scan bid/ask for size spikes > 3x the rolling
    /// average level size. Large bids = bullish, large asks = bearish.
    /// </summary>
    private static decimal ComputeLargeOrderSignal(SymbolBookSnapshot snapshot, TickerAnalysisState state)
    {
        if (state.RecentAvgLevelSizes.Count < ShortWindow) return 0;

        decimal rollingAvg = state.RecentAvgLevelSizes.Average();
        if (rollingAvg <= 0) return 0;

        decimal threshold = rollingAvg * LargeOrderMultiplier;

        int largeBids = 0, largeAsks = 0;
        foreach (var size in snapshot.BidSizes)
            if (size > threshold) largeBids++;
        foreach (var size in snapshot.AskSizes)
            if (size > threshold) largeAsks++;

        int total = largeBids + largeAsks;
        if (total == 0) return 0;

        // Normalize: all large bids = +1, all large asks = -1
        return (decimal)(largeBids - largeAsks) / total;
    }

    #region New indicator computations

    /// <summary>
    /// Tick Momentum: uptick/downtick ratio from TickDataCache. Range [-1, +1].
    /// </summary>
    private decimal ComputeTickMomentumScore(long tickerId)
    {
        var tickData = _tickCache.GetData(tickerId);
        if (tickData == null) return 0;

        // TickMomentum is already in [-1, +1]
        return tickData.TickMomentum;
    }

    /// <summary>
    /// Trend Alignment: +1 if bullish EMA trend, -1 if bearish, 0 if neutral or no data.
    /// </summary>
    private static decimal ComputeTrendAlignmentScore(BarIndicators? barIndicators)
    {
        if (barIndicators == null) return 0;
        return barIndicators.TrendDirection; // already +1, 0, or -1
    }

    /// <summary>
    /// VWAP Position: positive if price is above VWAP (bullish), negative if below.
    /// Magnitude based on distance from VWAP relative to price.
    /// </summary>
    private static decimal ComputeVwapPositionScore(BarIndicators? barIndicators, decimal currentPrice)
    {
        if (barIndicators == null || barIndicators.Vwap <= 0 || currentPrice <= 0)
            return 0;

        decimal deviation = (currentPrice - barIndicators.Vwap) / currentPrice;
        // Clamp to [-1, +1], scale by 100 so a 1% deviation maps to ~1.0
        return Math.Clamp(deviation * 100m, -1m, 1m);
    }

    /// <summary>
    /// Volume Confirmation: high volume = stronger signal in the same direction.
    /// Returns value in [0, +1] based on volume ratio.
    /// </summary>
    private static decimal ComputeVolumeConfirmationScore(BarIndicators? barIndicators)
    {
        if (barIndicators == null) return 0;

        // VolumeRatio > 1.5 is significant. Map ratio to [0, 1].
        // ratio=0.5 -> -0.5, ratio=1.0 -> 0, ratio=2.0 -> 1.0
        decimal score = (barIndicators.VolumeRatio - 1.0m);
        return Math.Clamp(score, -1m, 1m);
    }

    /// <summary>
    /// RSI Filter: penalize overbought conditions for buys and oversold for sells.
    /// Returns value in [-1, +1]. Neutral (RSI ~50) = 0.
    /// </summary>
    private static decimal ComputeRsiFilterScore(BarIndicators? barIndicators)
    {
        if (barIndicators == null) return 0;

        // RSI 0-100 mapped to [-1, +1]: RSI=50 -> 0, RSI=30 -> -0.4, RSI=70 -> +0.4
        // This gives a mild directional bias based on RSI
        decimal normalized = (barIndicators.Rsi14 - 50m) / 50m;
        return Math.Clamp(normalized, -1m, 1m);
    }

    #endregion

    private static (SignalType Type, SignalStrength Strength) ClassifySignal(decimal score)
    {
        if (score >= StrongBuyThreshold) return (SignalType.Buy, SignalStrength.Strong);
        if (score >= ModerateBuyThreshold) return (SignalType.Buy, SignalStrength.Moderate);
        if (score >= WeakBuyThreshold) return (SignalType.Buy, SignalStrength.Weak);
        if (score <= StrongSellThreshold) return (SignalType.Sell, SignalStrength.Strong);
        if (score <= ModerateSellThreshold) return (SignalType.Sell, SignalStrength.Moderate);
        if (score <= WeakSellThreshold) return (SignalType.Sell, SignalStrength.Weak);
        return (SignalType.Hold, SignalStrength.Weak);
    }

    private static string BuildReason(
        SignalType type, SignalStrength strength,
        Dictionary<string, decimal> indicators)
    {
        var sb = new StringBuilder();
        sb.Append($"{strength} {type} signal (score={indicators["CompositeScore"]:F3}). ");

        // Highlight dominant contributors
        var dominant = indicators
            .Where(kv => kv.Key != "CompositeScore")
            .OrderByDescending(kv => Math.Abs(kv.Value))
            .Take(2);

        sb.Append("Drivers: ");
        sb.Append(string.Join(", ", dominant.Select(kv =>
            $"{kv.Key}={kv.Value:+0.000;-0.000}")));

        return sb.ToString();
    }

    private static void EnqueueCapped(Queue<decimal> queue, decimal value, int maxSize)
    {
        queue.Enqueue(value);
        while (queue.Count > maxSize)
            queue.Dequeue();
    }
}

internal class TickerAnalysisState
{
    public Queue<decimal> RecentImbalances { get; } = new();
    public Queue<decimal> RecentSpreads { get; } = new();
    public Queue<decimal> RecentMidPrices { get; } = new();
    public Queue<decimal> RecentAvgLevelSizes { get; } = new();
    public TradingSignal? LastSignal { get; set; }
    public DateTime LastSignalTime { get; set; }
}
