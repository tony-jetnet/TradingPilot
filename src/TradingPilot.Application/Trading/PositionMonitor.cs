using Microsoft.Extensions.Logging;
using TradingPilot.Symbols;
using TradingPilot.Trading;

namespace TradingPilot.Trading;

/// <summary>
/// Background monitor that continuously re-evaluates open positions every 5 seconds.
/// Handles all exit logic except strong opposing signals (which PaperTradingExecutor handles event-driven).
/// Also verifies pending orders and syncs position state with broker.
/// </summary>
public class PositionMonitor : IDisposable
{
    private readonly PaperTradingExecutor _executor;
    private readonly MarketMicrostructureAnalyzer _analyzer;
    private readonly L2BookCache _l2Cache;
    private readonly TickDataCache _tickCache;
    private readonly BarIndicatorCache _barCache;
    private readonly ILogger<PositionMonitor> _logger;

    private readonly Timer _timer;
    private int _tickCount;
    private int _evaluating; // Re-entrancy guard (0 = idle, 1 = running)
    private bool _disposed;
    private bool _initialized;

    // Hard cap: never hold longer than 3x the configured hold time
    private const int MaxHoldMultiplier = 3;
    // Default score decay tolerance for Stage 2 entries (no per-rule confidence)
    private const decimal DefaultScoreDecayTolerance = 0.50m;
    // Grace period: don't trigger score-based exits in the first N seconds (noise protection)
    private const int ScoreExitGracePeriodSeconds = 15;

    public PositionMonitor(
        PaperTradingExecutor executor,
        MarketMicrostructureAnalyzer analyzer,
        L2BookCache l2Cache,
        TickDataCache tickCache,
        BarIndicatorCache barCache,
        ILogger<PositionMonitor> logger)
    {
        _executor = executor;
        _analyzer = analyzer;
        _l2Cache = l2Cache;
        _tickCache = tickCache;
        _barCache = barCache;
        _logger = logger;

        // Start timer: 5-second interval, 10-second initial delay (let app warm up)
        _timer = new Timer(OnTimerTick, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
        _logger.LogWarning("PositionMonitor started: checking positions every 5s, broker sync every 30s");
    }

    private async void OnTimerTick(object? state)
    {
        if (_disposed) return;

        // Re-entrancy guard: skip if previous tick is still running
        if (Interlocked.CompareExchange(ref _evaluating, 1, 0) != 0)
            return;

        try
        {
            _tickCount++;

            // First tick: initialize positions from broker
            if (!_initialized)
            {
                try
                {
                    await _executor.InitializeFromBrokerAsync();
                    _initialized = true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "PositionMonitor: broker initialization failed, will retry");
                }
            }

            // Verify pending orders every tick (5s)
            try
            {
                await _executor.VerifyPendingOrdersAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PositionMonitor: pending order verification failed");
            }

            // Evaluate open positions
            var positions = _executor.GetOpenPositions();

            if (positions.Count > 0)
            {
                _logger.LogDebug("PositionMonitor: checking {Count} positions", positions.Count);
            }

            foreach (var (symbol, pos) in positions)
            {
                try
                {
                    // Skip if exit order already in flight
                    if (_executor.HasPendingExit(symbol))
                        continue;

                    await EvaluatePositionAsync(symbol, pos);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "PositionMonitor: error evaluating position for {Symbol}", symbol);
                }
            }

            // Broker sync every ~30 seconds (every 6th tick)
            if (_tickCount % 6 == 0)
            {
                try
                {
                    await _executor.SyncWithBrokerAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "PositionMonitor: broker sync failed");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PositionMonitor: unhandled error in timer tick");
        }
        finally
        {
            Interlocked.Exchange(ref _evaluating, 0);
        }
    }

    private async Task EvaluatePositionAsync(string symbol, PositionState pos)
    {
        // Get current price from latest L2 snapshot (uses tickerId for cache lookup)
        var snapshots = _l2Cache.GetSnapshots(pos.TickerId, 1);
        if (snapshots.Count == 0) return;

        decimal currentPrice = snapshots[^1].MidPrice;
        decimal currentScore = _analyzer.ComputeCurrentScore(pos.TickerId);

        // Update peak favorable score
        if (pos.IsLong && currentScore > pos.PeakFavorableScore)
            pos.PeakFavorableScore = currentScore;
        else if (!pos.IsLong && currentScore < pos.PeakFavorableScore)
            pos.PeakFavorableScore = currentScore;

        // Update peak favorable price (for trailing stop)
        if (pos.IsLong && currentPrice > pos.PeakFavorablePrice)
            pos.PeakFavorablePrice = currentPrice;
        else if (!pos.IsLong && currentPrice < pos.PeakFavorablePrice)
            pos.PeakFavorablePrice = currentPrice;

        decimal elapsed = (decimal)(DateTime.UtcNow - pos.EntryTime).TotalSeconds;

        // ═══════════════════════════════════════════════════════════
        // CHECK 1: Score flip — entry thesis fully dead
        // No grace period: a full sign flip is definitive, not noise.
        // ═══════════════════════════════════════════════════════════
        bool scoreFlipped = (pos.IsLong && currentScore < 0 && pos.EntryScore > 0) ||
                            (!pos.IsLong && currentScore > 0 && pos.EntryScore < 0);
        if (scoreFlipped)
        {
            await _executor.ExitPositionAsync(symbol, currentPrice,
                $"SCORE FLIP entry={pos.EntryScore:F3} now={currentScore:F3} elapsed={elapsed:F0}s", currentScore);
            _logger.LogWarning("PositionMonitor: {Symbol} EXIT SCORE FLIP entry={Entry:F3} now={Now:F3}",
                symbol, pos.EntryScore, currentScore);
            return;
        }

        // ═══════════════════════════════════════════════════════════
        // CHECK 2: Score decay from peak (grace period: first 15s is noise)
        // Rule entries: tolerance = (1 - RuleConfidence)
        // Stage 2 entries: tolerance = 50% (default)
        // ═══════════════════════════════════════════════════════════
        if (elapsed >= ScoreExitGracePeriodSeconds)
        {
            decimal tolerance = pos.RuleConfidence > 0
                ? (1m - pos.RuleConfidence)       // 0.62 conf → 0.38 tolerance
                : DefaultScoreDecayTolerance;     // Stage 2: 50% decay allowed

            decimal peakAbs = Math.Abs(pos.PeakFavorableScore);
            decimal currentAbs = pos.IsLong ? currentScore : -currentScore;
            decimal decayFromPeak = peakAbs > 0 ? (peakAbs - currentAbs) / peakAbs : 0;

            if (peakAbs > 0.05m && decayFromPeak > tolerance) // Ignore tiny peaks (noise)
            {
                await _executor.ExitPositionAsync(symbol, currentPrice,
                    $"SCORE DECAY peak={pos.PeakFavorableScore:F3} now={currentScore:F3} decay={decayFromPeak:P0} tol={tolerance:P0} elapsed={elapsed:F0}s", currentScore);
                _logger.LogWarning("PositionMonitor: {Symbol} EXIT SCORE DECAY peak={Peak:F3} now={Now:F3} decay={Decay:P0}",
                    symbol, pos.PeakFavorableScore, currentScore, decayFromPeak);
                return;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // CHECK 3: Microstructure collapse — spread at 90th percentile + imbalance reversed
        // ═══════════════════════════════════════════════════════════
        var tickData = _tickCache.GetData(pos.TickerId);
        if (tickData != null)
        {
            bool spreadWide = tickData.SpreadPercentile >= 0.90m;
            bool imbalanceReversed = pos.IsLong
                ? (tickData.BookDepthRatio < 0 && pos.EntryImbalance > 0)
                : (tickData.BookDepthRatio > 0 && pos.EntryImbalance < 0);

            if (spreadWide && imbalanceReversed)
            {
                await _executor.ExitPositionAsync(symbol, currentPrice,
                    $"MICROSTRUCTURE COLLAPSE spread_pctl={tickData.SpreadPercentile:F2} obi_entry={pos.EntryImbalance:F3} obi_now={tickData.BookDepthRatio:F3} elapsed={elapsed:F0}s", currentScore);
                _logger.LogWarning("PositionMonitor: {Symbol} EXIT MICROSTRUCTURE COLLAPSE", symbol);
                return;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // CHECK 4: Stop loss (price-relative, with spread floor)
        // Floor = actual entry spread to avoid exits on normal bid-ask noise
        // ═══════════════════════════════════════════════════════════
        decimal adverse = pos.IsLong
            ? pos.EntryPrice - currentPrice
            : currentPrice - pos.EntryPrice;

        decimal effectiveStopLoss = Math.Max(pos.StopLoss, pos.EntrySpread);

        if (adverse > effectiveStopLoss)
        {
            await _executor.ExitPositionAsync(symbol, currentPrice,
                $"STOP LOSS adverse={adverse:F2} stop={effectiveStopLoss:F2} elapsed={elapsed:F0}s", currentScore);
            _logger.LogWarning("PositionMonitor: {Symbol} EXIT STOP LOSS adverse={Adverse:F2} stop={Stop:F2}",
                symbol, adverse, effectiveStopLoss);
            return;
        }

        // ═══════════════════════════════════════════════════════════
        // CHECK 5: Trailing stop using peak price
        // Once profitable by > stop amount, trail from peak price.
        // Higher confidence = tighter trail.
        // ═══════════════════════════════════════════════════════════
        decimal peakProfit = pos.IsLong
            ? pos.PeakFavorablePrice - pos.EntryPrice
            : pos.EntryPrice - pos.PeakFavorablePrice;
        decimal currentProfit = pos.IsLong
            ? currentPrice - pos.EntryPrice
            : pos.EntryPrice - currentPrice;

        if (peakProfit > effectiveStopLoss)
        {
            // Trail factor: confidence 0.62 → give back at most 38% of peak profit
            // confidence 0 (Stage 2) → give back at most 60%
            decimal maxGiveBack = pos.RuleConfidence > 0
                ? (1m - pos.RuleConfidence)
                : 0.60m;
            decimal pullback = peakProfit - currentProfit;

            if (pullback > peakProfit * maxGiveBack)
            {
                await _executor.ExitPositionAsync(symbol, currentPrice,
                    $"TRAILING STOP peak_profit={peakProfit:F2} now={currentProfit:F2} pullback={pullback:F2} max_giveback={maxGiveBack:P0} elapsed={elapsed:F0}s", currentScore);
                _logger.LogWarning("PositionMonitor: {Symbol} EXIT TRAILING STOP peak={PeakProfit:F2} now={CurrentProfit:F2}",
                    symbol, peakProfit, currentProfit);
                return;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // CHECK 6: Adaptive time gate
        // Past BaseHoldSeconds AND score weakening → exit
        // Past BaseHoldSeconds BUT score improving → hold (up to 3x cap)
        // ═══════════════════════════════════════════════════════════
        if (elapsed >= pos.HoldSeconds)
        {
            // Hard cap: never hold beyond 3x the configured hold time
            if (elapsed >= pos.HoldSeconds * MaxHoldMultiplier)
            {
                await _executor.ExitPositionAsync(symbol, currentPrice,
                    $"TIME CAP {elapsed:F0}s (max={pos.HoldSeconds * MaxHoldMultiplier}s) score={currentScore:F3}", currentScore);
                _logger.LogWarning("PositionMonitor: {Symbol} EXIT TIME CAP {Elapsed:F0}s", symbol, elapsed);
                return;
            }

            // Past base hold time: exit if score is weakening, hold if improving
            decimal scoreStrength = pos.IsLong ? currentScore : -currentScore;
            decimal entryStrength = pos.IsLong ? pos.EntryScore : -pos.EntryScore;
            bool scoreWeakening = scoreStrength < entryStrength * 0.5m;

            if (scoreWeakening)
            {
                await _executor.ExitPositionAsync(symbol, currentPrice,
                    $"TIME+WEAK {elapsed:F0}s score={currentScore:F3} (entry={pos.EntryScore:F3})", currentScore);
                _logger.LogWarning("PositionMonitor: {Symbol} EXIT TIME+WEAK {Elapsed:F0}s score={Score:F3}",
                    symbol, elapsed, currentScore);
                return;
            }

            _logger.LogDebug("PositionMonitor: {Symbol} past hold time ({Elapsed:F0}s/{Hold}s) but score strong ({Score:F3}), holding",
                symbol, elapsed, pos.HoldSeconds, currentScore);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }
}
