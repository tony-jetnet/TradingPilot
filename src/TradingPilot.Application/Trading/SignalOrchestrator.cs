using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingPilot.Symbols;
using TradingPilot.Trading;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Linq;
using Volo.Abp.Uow;

namespace TradingPilot.Trading;

/// <summary>
/// Orchestrates the three-layer day trading signal pipeline:
///   1. SetupDetector → bar-based setup detection
///   2. MarketMicrostructureAnalyzer.ComputeCurrentScore → L2 timing
///   3. ContextScorer → news/fundamental context
///   4. CompositeScorer → blend into final score
///
/// Also persists BarSetup entities to DB for training and analysis.
/// Falls back to L2-only signals if no setup is active (backward compatible).
/// </summary>
public class SignalOrchestrator
{
    private readonly SetupDetector _setupDetector;
    private readonly ContextScorer _contextScorer;
    private readonly CompositeScorer _compositeScorer;
    private readonly MarketMicrostructureAnalyzer _analyzer;
    private readonly BarIndicatorCache _barCache;
    private readonly L2BookCache _l2Cache;
    private readonly TickDataCache _tickCache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SignalOrchestrator> _logger;

    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    public SignalOrchestrator(
        SetupDetector setupDetector,
        ContextScorer contextScorer,
        CompositeScorer compositeScorer,
        MarketMicrostructureAnalyzer analyzer,
        BarIndicatorCache barCache,
        L2BookCache l2Cache,
        TickDataCache tickCache,
        IServiceScopeFactory scopeFactory,
        ILogger<SignalOrchestrator> logger)
    {
        _setupDetector = setupDetector;
        _contextScorer = contextScorer;
        _compositeScorer = compositeScorer;
        _analyzer = analyzer;
        _barCache = barCache;
        _l2Cache = l2Cache;
        _tickCache = tickCache;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Evaluate the full day trading pipeline for a symbol.
    /// Called from MqttMessageProcessor after L2 data arrives, only for IsActiveForTrading symbols.
    /// Returns an enriched TradingSignal if composite score exceeds threshold, null otherwise.
    /// </summary>
    public async Task<TradingSignal?> EvaluateAsync(long tickerId, string symbolId, string ticker, decimal currentPrice)
    {
        if (currentPrice <= 0) return null;

        var barIndicators = _barCache.GetIndicators(tickerId);
        if (barIndicators == null) return null;

        // Check staleness — skip if bar indicators are too old
        if ((DateTime.UtcNow - barIndicators.LastRefreshTime).TotalMinutes > 2)
            return null;

        var nowEt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Eastern);
        int etHour = nowEt.Hour;
        int etMinute = nowEt.Minute;

        // Market hours gate
        bool beforeOpen = etHour < DayTradeConfig.EntryStartHour
            || (etHour == DayTradeConfig.EntryStartHour && etMinute < DayTradeConfig.EntryStartMinute);
        bool afterClose = etHour > DayTradeConfig.EntryEndHour
            || (etHour == DayTradeConfig.EntryEndHour && etMinute > DayTradeConfig.EntryEndMinute);
        if (beforeOpen || afterClose) return null;

        // ── Layer 1: Setup Detection ──
        var setups = _setupDetector.DetectSetups(tickerId, barIndicators, currentPrice);

        // ── Layer 2: L2 Timing Score ──
        decimal timingScore = _analyzer.ComputeCurrentScore(tickerId);

        // ── Layer 3: Context Score ──
        var (newsSentiment, catalystType, capitalFlowScore, shortFloat, daysToEarnings, newsCount2Hr) =
            await LoadContextDataAsync(symbolId);

        decimal contextScore = _contextScorer.ScoreContext(
            newsSentiment, catalystType, capitalFlowScore, shortFloat, daysToEarnings,
            etHour, barIndicators.TrendDirection_15m);

        // Get scoring weights from model config (or defaults)
        var weights = LoadScoringWeights(tickerId);

        // ── Build indicator snapshot for persistence ──
        var snapshot = BuildSnapshot(tickerId, currentPrice, barIndicators, newsSentiment,
            catalystType, capitalFlowScore, shortFloat, daysToEarnings, newsCount2Hr);

        // ── Try composite signal from best setup ──
        if (setups.Count > 0)
        {
            var bestSetup = setups[0]; // Already sorted by strength descending

            // Check L2 timing confirms setup direction
            bool timingConfirms = (bestSetup.Direction == SignalType.Buy && timingScore >= DayTradeConfig.MinTimingScoreForSetup)
                               || (bestSetup.Direction == SignalType.Sell && timingScore <= -DayTradeConfig.MinTimingScoreForSetup);

            if (timingConfirms)
            {
                int setupDirection = bestSetup.Direction == SignalType.Buy ? 1 : -1;
                var (compositeScore, breakdown) = _compositeScorer.Score(
                    bestSetup.Strength, setupDirection, timingScore, contextScore,
                    weights, barIndicators);

                // Persist setup to DB for training
                await PersistBarSetupAsync(bestSetup, symbolId, tickerId, barIndicators, snapshot);

                if (Math.Abs(compositeScore) >= DayTradeConfig.MinCompositeScoreEntry)
                {
                    var signal = BuildSignal(tickerId, ticker, currentPrice, compositeScore,
                        bestSetup, timingScore, contextScore, snapshot, breakdown);

                    _logger.LogWarning(
                        "COMPOSITE signal: {Ticker} {Type} score={Score:F3} setup={Setup} strength={Strength:F2} " +
                        "timing={Timing:F3} context={Context:F3} | {Breakdown}",
                        ticker, signal.Type, compositeScore, bestSetup.Type, bestSetup.Strength,
                        timingScore, contextScore, breakdown);

                    return signal;
                }
            }
            else
            {
                // Setup exists but L2 doesn't confirm — persist setup for tracking but don't signal
                await PersistBarSetupAsync(setups[0], symbolId, tickerId, barIndicators, snapshot);

                _logger.LogDebug(
                    "Setup {Type} for {Ticker} strength={Strength:F2} but L2 timing={Timing:F3} doesn't confirm",
                    setups[0].Type, ticker, setups[0].Strength, timingScore);
            }
        }

        // ── Fallback: L2-only signal if timing is strong enough (backward compatible) ──
        // Only emit L2-only signals if |timingScore| >= 0.40 (strong L2 conviction without setup)
        if (Math.Abs(timingScore) >= 0.40m)
        {
            // Use timing as primary, context as secondary, no setup
            var (compositeScore, breakdown) = _compositeScorer.Score(
                0, 0, timingScore, contextScore, weights, barIndicators);

            if (Math.Abs(compositeScore) >= DayTradeConfig.MinCompositeScoreEntry)
            {
                var signal = BuildSignal(tickerId, ticker, currentPrice, compositeScore,
                    null, timingScore, contextScore, snapshot, breakdown);

                _logger.LogDebug(
                    "L2-only signal: {Ticker} {Type} score={Score:F3} timing={Timing:F3}",
                    ticker, signal.Type, compositeScore, timingScore);

                return signal;
            }
        }

        return null;
    }

    private TradingSignal BuildSignal(
        long tickerId, string ticker, decimal price, decimal compositeScore,
        SetupResult? setup, decimal timingScore, decimal contextScore,
        IndicatorSnapshot snapshot, string breakdown)
    {
        SignalType type = compositeScore > 0 ? SignalType.Buy : SignalType.Sell;
        decimal absScore = Math.Abs(compositeScore);
        SignalStrength strength = absScore >= 0.40m ? SignalStrength.Strong
                               : absScore >= 0.20m ? SignalStrength.Moderate
                               : SignalStrength.Weak;

        string source = setup != null ? $"[{setup.Type}]" : "[L2]";
        string reason = setup != null
            ? $"{source} {setup.Description} | {breakdown}"
            : $"{source} timing={timingScore:F3} | {breakdown}";

        var signal = new TradingSignal
        {
            TickerId = tickerId,
            Ticker = ticker,
            Timestamp = DateTime.UtcNow,
            Type = type,
            Strength = strength,
            Price = price,
            Reason = reason,
            Source = setup != null ? SignalSource.Composite : SignalSource.L2Micro,
            SignalSetupType = setup?.Type ?? SetupType.None,
            SetupStrength = setup?.Strength ?? 0,
            TimingScore = timingScore,
            ContextScore = contextScore,
            CompositeScore = compositeScore,
            Setup = setup,
            Snapshot = snapshot,
            Indicators = new Dictionary<string, decimal>
            {
                ["CompositeScore"] = compositeScore,
                ["SetupStrength"] = setup?.Strength ?? 0,
                ["TimingScore"] = timingScore,
                ["ContextScore"] = contextScore,
                ["SpreadPercentile"] = snapshot.SpreadPercentile,
            },
        };

        // Add rule-related indicators if this came from existing L2 analyzer
        // (backward compat: PaperTradingExecutor checks for "RuleConfidence" key)
        if (setup != null)
        {
            signal.Indicators["RuleHoldSeconds"] = (decimal)(setup.Atr > 2m ? DayTradeConfig.DefaultHoldSeconds : 3600);
            signal.Indicators["RuleStopLoss"] = setup.StopDistance;
        }

        return signal;
    }

    private IndicatorSnapshot BuildSnapshot(
        long tickerId, decimal price, BarIndicators bars,
        decimal? newsSentiment, string? catalystType, decimal? capitalFlowScore,
        decimal? shortFloat, int? daysToEarnings, int newsCount2Hr)
    {
        var tickData = _tickCache.GetData(tickerId);
        var latestL2 = _l2Cache.GetSnapshots(tickerId, 1);

        var snapshot = new IndicatorSnapshot
        {
            Price = price,
            Spread = latestL2.Count > 0 ? latestL2[^1].Spread : 0,
            NewsSentiment = newsSentiment,
            CatalystType = catalystType,
            CapitalFlowScore = capitalFlowScore,
            ShortFloat = shortFloat,
            DaysToEarnings = daysToEarnings,
            NewsCount2Hr = newsCount2Hr,
        };

        snapshot.FillFromBarIndicators(bars);
        if (tickData != null)
            snapshot.FillFromTickData(tickData);

        return snapshot;
    }

    private ScoringWeights LoadScoringWeights(long tickerId)
    {
        var modelConfig = _analyzer.CurrentModelConfig;
        if (modelConfig?.Tickers.TryGetValue(tickerId, out var tickerConfig) == true)
        {
            return new ScoringWeights
            {
                SetupWeight = tickerConfig.WeightSetup > 0 ? tickerConfig.WeightSetup : DayTradeConfig.DefaultSetupWeight,
                TimingWeight = tickerConfig.WeightTiming > 0 ? tickerConfig.WeightTiming : DayTradeConfig.DefaultTimingWeight,
                ContextWeight = tickerConfig.WeightContext > 0 ? tickerConfig.WeightContext : DayTradeConfig.DefaultContextWeight,
            };
        }
        return ScoringWeights.Default();
    }

    /// <summary>
    /// Load context data (news sentiment, capital flow, fundamentals) from DB.
    /// Cached per symbol per day to avoid repeated DB queries.
    /// </summary>
    private async Task<(decimal? NewsSentiment, string? CatalystType, decimal? CapitalFlowScore,
                        decimal? ShortFloat, int? DaysToEarnings, int NewsCount2Hr)>
        LoadContextDataAsync(string symbolId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            var newsRepo = scope.ServiceProvider.GetRequiredService<IRepository<SymbolNews, Guid>>();
            var flowRepo = scope.ServiceProvider.GetRequiredService<IRepository<SymbolCapitalFlow, Guid>>();
            var finRepo = scope.ServiceProvider.GetRequiredService<IRepository<SymbolFinancialSnapshot, Guid>>();
            var asyncExec = scope.ServiceProvider.GetRequiredService<IAsyncQueryableExecuter>();

            using var uow = uowManager.Begin();

            // News sentiment: average of scored articles in last 24 hours
            decimal? newsSentiment = null;
            string? catalystType = null;
            int newsCount2Hr = 0;

            var cutoff24h = DateTime.UtcNow.AddHours(-24);
            var cutoff2h = DateTime.UtcNow.AddHours(-2);

            var recentNews = await asyncExec.ToListAsync(
                (await newsRepo.GetQueryableAsync())
                    .Where(n => n.SymbolId == symbolId && n.PublishedAt >= cutoff24h)
                    .OrderByDescending(n => n.PublishedAt)
                    .Take(20));

            if (recentNews.Count > 0)
            {
                var scored = recentNews.Where(n => n.SentimentScore.HasValue).ToList();
                if (scored.Count > 0)
                    newsSentiment = scored.Average(n => n.SentimentScore!.Value);

                catalystType = recentNews.FirstOrDefault(n => n.CatalystType != null)?.CatalystType;
                newsCount2Hr = recentNews.Count(n => n.PublishedAt >= cutoff2h);
            }

            // Capital flow: latest 3 days average
            decimal? capitalFlowScore = null;
            var recentFlows = await asyncExec.ToListAsync(
                (await flowRepo.GetQueryableAsync())
                    .Where(f => f.SymbolId == symbolId)
                    .OrderByDescending(f => f.Date)
                    .Take(3));

            if (recentFlows.Count > 0)
            {
                decimal totalNet = recentFlows.Sum(f =>
                    (f.SuperLargeInflow + f.LargeInflow) - (f.SuperLargeOutflow + f.LargeOutflow));
                decimal totalVolume = recentFlows.Sum(f =>
                    f.SuperLargeInflow + f.LargeInflow + f.SuperLargeOutflow + f.LargeOutflow);

                capitalFlowScore = totalVolume > 0
                    ? Math.Clamp(totalNet / totalVolume, -1m, 1m)
                    : 0;
            }

            // Fundamentals: short float and earnings date
            decimal? shortFloat = null;
            int? daysToEarnings = null;
            var today = DateOnly.FromDateTime(DateTime.UtcNow);

            var latestFin = await asyncExec.FirstOrDefaultAsync(
                (await finRepo.GetQueryableAsync())
                    .Where(f => f.SymbolId == symbolId)
                    .OrderByDescending(f => f.Date));

            if (latestFin != null)
            {
                shortFloat = latestFin.ShortFloat;
                if (!string.IsNullOrEmpty(latestFin.NextEarningsDate)
                    && DateOnly.TryParse(latestFin.NextEarningsDate, out var earningsDate))
                {
                    daysToEarnings = earningsDate.DayNumber - today.DayNumber;
                    if (daysToEarnings < 0) daysToEarnings = null; // Past earnings, not relevant
                }
            }

            await uow.CompleteAsync();

            return (newsSentiment, catalystType, capitalFlowScore, shortFloat, daysToEarnings, newsCount2Hr);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load context data for {SymbolId}", symbolId);
            return (null, null, null, null, null, 0);
        }
    }

    /// <summary>
    /// Persist a detected setup to DB for nightly training and backtest evaluation.
    /// </summary>
    private async Task PersistBarSetupAsync(
        SetupResult setup, string symbolId, long tickerId,
        BarIndicators bars, IndicatorSnapshot snapshot)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            var repo = scope.ServiceProvider.GetRequiredService<IRepository<BarSetup, Guid>>();

            using var uow = uowManager.Begin();

            var entity = new BarSetup
            {
                SymbolId = symbolId,
                TickerId = tickerId,
                Timestamp = DateTime.UtcNow,
                SetupType = (byte)setup.Type,
                Direction = (byte)setup.Direction,
                Strength = setup.Strength,
                EntryZoneLow = setup.EntryZoneLow,
                EntryZoneHigh = setup.EntryZoneHigh,
                StopLevel = setup.StopLevel,
                TargetLevel = setup.TargetLevel,
                ExpiresAt = setup.ExpiresAt,
                Price = setup.DetectionPrice,
                // 1m indicators
                Ema9 = bars.Ema9,
                Ema20 = bars.Ema20,
                Rsi14 = bars.Rsi14,
                Vwap = bars.Vwap,
                Atr14 = bars.Atr14,
                VolumeRatio = bars.VolumeRatio,
                TrendDirection = bars.TrendDirection,
                // 5m indicators
                Ema20_5m = bars.Ema20_5m,
                Ema50_5m = bars.Ema50_5m,
                Rsi14_5m = bars.Rsi14_5m,
                Atr14_5m = bars.Atr14_5m,
                TrendDirection_5m = bars.TrendDirection_5m,
                // 15m indicators
                Ema20_15m = bars.Ema20_15m,
                Ema50_15m = bars.Ema50_15m,
                Rsi14_15m = bars.Rsi14_15m,
                TrendDirection_15m = bars.TrendDirection_15m,
                // Context
                CapitalFlowScore = snapshot.CapitalFlowScore,
                NewsSentiment = snapshot.NewsSentiment,
                HasCatalyst = !string.IsNullOrEmpty(snapshot.CatalystType),
                CatalystType = snapshot.CatalystType,
                NewsCount2Hr = snapshot.NewsCount2Hr,
            };

            await repo.InsertAsync(entity, autoSave: false);
            await uow.CompleteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist BarSetup for tickerId={TickerId}", tickerId);
        }
    }
}
