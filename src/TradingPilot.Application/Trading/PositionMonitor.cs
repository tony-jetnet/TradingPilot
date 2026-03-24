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

    // Hard cap: never hold longer than 2x the configured hold time
    private const int MaxHoldMultiplier = 2;
    // Anti-wick: peak price must persist this long before trailing stop uses it
    private const int PeakPersistenceSeconds = 10;
    // ATR multiplier for volatility-adaptive stop loss floor
    private const decimal AtrStopMultiplier = 1.5m;

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
        // Get current price — try L2 cache first, fall back to tick/quote cache
        decimal currentPrice = 0;
        var snapshots = _l2Cache.GetSnapshots(pos.TickerId, 1);
        if (snapshots.Count > 0)
        {
            currentPrice = snapshots[^1].MidPrice;
        }
        else
        {
            var fallbackTick = _tickCache.GetData(pos.TickerId);
            if (fallbackTick?.LastPrice > 0)
                currentPrice = fallbackTick.LastPrice;
            else
            {
                var barInd = _barCache.GetIndicators(pos.TickerId);
                if (barInd != null && barInd.Ema9 > 0)
                    currentPrice = barInd.Ema9; // Last known price proxy
            }
        }
        if (currentPrice <= 0) return; // No price source at all

        var now = DateTime.UtcNow;
        decimal elapsed = (decimal)(now - pos.EntryTime).TotalSeconds;
        decimal currentScore = _analyzer.ComputeCurrentScore(pos.TickerId);

        // Update peak favorable score
        if (pos.IsLong && currentScore > pos.PeakFavorableScore)
            pos.PeakFavorableScore = currentScore;
        else if (!pos.IsLong && currentScore < pos.PeakFavorableScore)
            pos.PeakFavorableScore = currentScore;

        // Update peak favorable price with timestamp (for anti-wick trailing stop)
        bool newPeak = (pos.IsLong && currentPrice > pos.PeakFavorablePrice) ||
                       (!pos.IsLong && currentPrice < pos.PeakFavorablePrice);
        if (newPeak)
        {
            pos.PeakFavorablePrice = currentPrice;
            pos.PeakPriceSetAt = now;
        }

        // Get bar indicators for bar-based exit checks
        var barIndicators = _barCache.GetIndicators(pos.TickerId);

        // ═══════════════════════════════════════════════════════════
        // CHECK 1: VWAP Cross — price crossed VWAP against position
        // Grace period: 15 minutes (ignore early VWAP noise)
        // ═══════════════════════════════════════════════════════════
        if (elapsed >= 900 && barIndicators != null)
        {
            bool vwapExit = (pos.IsLong && barIndicators.Vwap > 0 && currentPrice < barIndicators.Vwap) ||
                            (!pos.IsLong && barIndicators.Vwap > 0 && currentPrice > barIndicators.Vwap);
            if (vwapExit)
            {
                await _executor.ExitPositionAsync(symbol, currentPrice,
                    $"VWAP CROSS price={currentPrice:F2} vwap={barIndicators.Vwap:F2} elapsed={elapsed:F0}s", currentScore);
                _logger.LogWarning("PositionMonitor: {Symbol} EXIT VWAP CROSS price={Price:F2} vwap={Vwap:F2}",
                    symbol, currentPrice, barIndicators.Vwap);
                return;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // CHECK 2: EMA Trend Reversal — EMA9 crossed EMA20 against position
        // Grace period: 10 minutes (give the trend time to develop)
        // ═══════════════════════════════════════════════════════════
        if (elapsed >= 600 && barIndicators != null)
        {
            bool trendReversed = (pos.IsLong && barIndicators.Ema9 > 0 && barIndicators.Ema20 > 0 && barIndicators.Ema9 < barIndicators.Ema20) ||
                                 (!pos.IsLong && barIndicators.Ema9 > 0 && barIndicators.Ema20 > 0 && barIndicators.Ema9 > barIndicators.Ema20);
            if (trendReversed)
            {
                await _executor.ExitPositionAsync(symbol, currentPrice,
                    $"TREND REVERSAL ema9={barIndicators.Ema9:F2} ema20={barIndicators.Ema20:F2} elapsed={elapsed:F0}s", currentScore);
                _logger.LogWarning("PositionMonitor: {Symbol} EXIT TREND REVERSAL ema9={Ema9:F2} ema20={Ema20:F2}",
                    symbol, barIndicators.Ema9, barIndicators.Ema20);
                return;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // CHECK 3: RSI Extreme — take profit on overbought/oversold
        // Grace period: 5 minutes
        // ═══════════════════════════════════════════════════════════
        if (elapsed >= 300 && barIndicators != null)
        {
            bool rsiExtreme = (pos.IsLong && barIndicators.Rsi14 > 75) ||
                              (!pos.IsLong && barIndicators.Rsi14 > 0 && barIndicators.Rsi14 < 25);
            if (rsiExtreme)
            {
                await _executor.ExitPositionAsync(symbol, currentPrice,
                    $"RSI EXTREME rsi={barIndicators.Rsi14:F1} elapsed={elapsed:F0}s", currentScore);
                _logger.LogWarning("PositionMonitor: {Symbol} EXIT RSI EXTREME rsi={Rsi:F1}",
                    symbol, barIndicators.Rsi14);
                return;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // CHECK 4: Stop loss — volatility-adaptive with spread floor
        // Uses Max(rule stop, 1.5×ATR, entry spread) so the stop automatically
        // widens when volatility is high and tightens when it's low.
        // Falls back to rule stop + spread floor if no ATR data at entry.
        // ═══════════════════════════════════════════════════════════
        decimal adverse = pos.IsLong
            ? pos.EntryPrice - currentPrice
            : currentPrice - pos.EntryPrice;

        decimal atrFloor = pos.EntryAtr > 0 ? pos.EntryAtr * AtrStopMultiplier : 0;
        decimal effectiveStopLoss = Math.Max(Math.Max(pos.StopLoss, atrFloor), pos.EntrySpread);

        if (adverse > effectiveStopLoss)
        {
            await _executor.ExitPositionAsync(symbol, currentPrice,
                $"STOP LOSS adverse={adverse:F2} stop={effectiveStopLoss:F2} (rule={pos.StopLoss:F2} atr_floor={atrFloor:F2} spread={pos.EntrySpread:F2}) elapsed={elapsed:F0}s", currentScore);
            _logger.LogWarning("PositionMonitor: {Symbol} EXIT STOP LOSS adverse={Adverse:F2} stop={Stop:F2}",
                symbol, adverse, effectiveStopLoss);
            return;
        }

        // ═══════════════════════════════════════════════════════════
        // CHECK 5a: Breakeven stop — once profitable by 1.5x stop distance,
        // move the floor to breakeven (entry price). Prevents winning trades
        // from turning into losers. Uses 1.5x (not 1.0x) to avoid premature
        // exits on normal price oscillation near the stop distance.
        // ═══════════════════════════════════════════════════════════
        decimal currentProfit = pos.IsLong
            ? currentPrice - pos.EntryPrice
            : pos.EntryPrice - currentPrice;
        decimal peakProfit = pos.IsLong
            ? pos.PeakFavorablePrice - pos.EntryPrice
            : pos.EntryPrice - pos.PeakFavorablePrice;

        if (peakProfit > effectiveStopLoss * 1.5m && currentProfit < 0)
        {
            await _executor.ExitPositionAsync(symbol, currentPrice,
                $"BREAKEVEN STOP peak_profit={peakProfit:F2} now_loss={currentProfit:F2} elapsed={elapsed:F0}s", currentScore);
            _logger.LogWarning("PositionMonitor: {Symbol} EXIT BREAKEVEN STOP was_up={PeakProfit:F2} now_down={Loss:F2}",
                symbol, peakProfit, currentProfit);
            return;
        }

        // ═══════════════════════════════════════════════════════════
        // CHECK 5b: Trailing stop using peak price (anti-wick filtered)
        // Only trail from peaks that have persisted for ≥10 seconds.
        // A single tick wick won't set an unrealistic peak that immediately
        // triggers the trail. Higher confidence = tighter trail.
        // ═══════════════════════════════════════════════════════════
        if (peakProfit > effectiveStopLoss)
        {
            // Anti-wick: only use peak if it has persisted for PeakPersistenceSeconds
            bool peakSustained = (now - pos.PeakPriceSetAt).TotalSeconds >= PeakPersistenceSeconds;
            decimal trailPeak = peakSustained ? peakProfit : peakProfit * 0.90m; // Use 90% of wick peak as conservative estimate

            // Trail factor: confidence 0.62 → give back at most 38% of peak profit
            // confidence 0 (Stage 2) → give back at most 60%
            decimal maxGiveBack = pos.RuleConfidence > 0
                ? (1m - pos.RuleConfidence)
                : 0.60m;
            decimal pullback = trailPeak - currentProfit;

            if (pullback > trailPeak * maxGiveBack)
            {
                await _executor.ExitPositionAsync(symbol, currentPrice,
                    $"TRAILING STOP peak={trailPeak:F2} now={currentProfit:F2} pullback={pullback:F2} max_giveback={maxGiveBack:P0} sustained={peakSustained} elapsed={elapsed:F0}s", currentScore);
                _logger.LogWarning("PositionMonitor: {Symbol} EXIT TRAILING STOP peak={PeakProfit:F2} now={CurrentProfit:F2}",
                    symbol, trailPeak, currentProfit);
                return;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // CHECK 6: Adaptive time gate
        // Past BaseHoldSeconds AND score weakening → exit
        // Past BaseHoldSeconds BUT score improving → hold (up to 2x cap)
        // ═══════════════════════════════════════════════════════════
        if (elapsed >= pos.HoldSeconds)
        {
            // Hard cap: never hold beyond 2x the configured hold time
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
