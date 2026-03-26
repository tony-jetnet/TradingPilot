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
    private const decimal AtrStopMultiplier = 2.0m;

    // ── Profit-side thresholds (decoupled from stop distance) ──
    // Trailing activates after this % of entry price is gained as profit.
    // 0.15% = $0.33 for $218 stock, $0.27 for $180 stock. Reachable in 5-60 min day trades.
    // Previously used effectiveStopLoss (2×ATR ≈ 1-2% of price) which was unreachable.
    private const decimal TrailingActivationPct = 0.0015m;
    // Breakeven activates at 2× the trailing activation threshold.
    // AMD: 2 × $0.33 = $0.66 peak profit needed → then protects at breakeven.
    private const decimal BreakevenActivationMultiple = 2.0m;
    // Regime exit: when VWAP/EMA/RSI are adverse AND trailing not active,
    // exit if loss exceeds this fraction of the full stop distance.
    // 0.40 × $4 stop = $1.60 loss threshold (vs waiting for $4.00 full stop).
    private const decimal RegimeExitStopFraction = 0.40m;

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

        // Pre-compute stop loss, profit, and peak profit for use across all checks
        decimal adverse = pos.IsLong
            ? pos.EntryPrice - currentPrice
            : currentPrice - pos.EntryPrice;

        decimal atrFloor = pos.EntryAtr > 0 ? pos.EntryAtr * AtrStopMultiplier : 0;
        decimal effectiveStopLoss = Math.Max(Math.Max(pos.StopLoss, atrFloor), pos.EntrySpread);

        decimal currentProfit = pos.IsLong
            ? currentPrice - pos.EntryPrice
            : pos.EntryPrice - currentPrice;
        decimal peakProfit = pos.IsLong
            ? pos.PeakFavorablePrice - pos.EntryPrice
            : pos.EntryPrice - pos.PeakFavorablePrice;

        // ═══════════════════════════════════════════════════════════
        // CHECK 0: Profit target — captures strong intraday moves.
        // Uses Max(1.0% of entry price, 1.5× stop distance).
        // AMD $218: Max($2.18, $6.00) = $6.00. Old was $12.00 (unreachable).
        // RIVN $15.66: Max($0.16, $2.25) = $2.25.
        // The 1.5× stop floor ensures we never take profit at less than 1.5× risk.
        // ═══════════════════════════════════════════════════════════
        decimal profitTarget = Math.Max(pos.EntryPrice * 0.010m, effectiveStopLoss * 1.5m);
        if (currentProfit >= profitTarget)
        {
            await _executor.ExitPositionAsync(symbol, currentPrice,
                $"PROFIT TARGET hit={currentProfit:F2} target={profitTarget:F2}", currentScore);
            return;
        }

        // Scale grace periods to hold time
        int vwapGrace = Math.Min(900, (int)(pos.HoldSeconds * 0.50));
        int emaGrace = Math.Min(600, (int)(pos.HoldSeconds * 0.33));
        int rsiGrace = Math.Min(300, (int)(pos.HoldSeconds * 0.17));

        // Trailing override: VWAP/EMA/RSI exits tighten the trailing stop instead of hard exiting
        decimal trailingOverride = -1m;

        // Trailing activation threshold: 0.15% of entry price, floored at 2× entry spread.
        // This is used by trailing stop, breakeven, and regime exit checks.
        decimal trailingActivation = Math.Max(pos.EntryPrice * TrailingActivationPct, pos.EntrySpread * 2m);

        // ═══════════════════════════════════════════════════════════
        // CHECK 1: VWAP Cross — price crossed VWAP against position
        // Grace period: scaled to hold time (max 15 minutes)
        // ═══════════════════════════════════════════════════════════
        if (elapsed >= vwapGrace && barIndicators != null)
        {
            bool vwapExit = (pos.IsLong && barIndicators.Vwap > 0 && currentPrice < barIndicators.Vwap) ||
                            (!pos.IsLong && barIndicators.Vwap > 0 && currentPrice > barIndicators.Vwap);
            if (vwapExit)
            {
                trailingOverride = 0.30m;
                _logger.LogWarning("PositionMonitor: {Symbol} VWAP CROSS tightening trail to 30% price={Price:F2} vwap={Vwap:F2}",
                    symbol, currentPrice, barIndicators.Vwap);
            }
        }

        // ═══════════════════════════════════════════════════════════
        // CHECK 2: EMA Trend Reversal — EMA9 crossed EMA20 against position
        // Grace period: scaled to hold time (max 10 minutes)
        // ═══════════════════════════════════════════════════════════
        if (elapsed >= emaGrace && barIndicators != null)
        {
            bool trendReversed = (pos.IsLong && barIndicators.Ema9 > 0 && barIndicators.Ema20 > 0 && barIndicators.Ema9 < barIndicators.Ema20) ||
                                 (!pos.IsLong && barIndicators.Ema9 > 0 && barIndicators.Ema20 > 0 && barIndicators.Ema9 > barIndicators.Ema20);
            if (trendReversed)
            {
                trailingOverride = trailingOverride > 0 ? Math.Min(trailingOverride, 0.30m) : 0.30m;
                _logger.LogWarning("PositionMonitor: {Symbol} TREND REVERSAL tightening trail to 30% ema9={Ema9:F2} ema20={Ema20:F2}",
                    symbol, barIndicators.Ema9, barIndicators.Ema20);
            }
        }

        // ═══════════════════════════════════════════════════════════
        // CHECK 3: RSI Extreme — graduated trailing tightening
        // Grace period: scaled to hold time (max 5 minutes)
        // ═══════════════════════════════════════════════════════════
        if (elapsed >= rsiGrace && barIndicators != null)
        {
            // Very extreme RSI: tighten to 25%
            bool rsiVeryExtreme = (pos.IsLong && barIndicators.Rsi14 > 80) ||
                                  (!pos.IsLong && barIndicators.Rsi14 > 0 && barIndicators.Rsi14 < 20);
            // Moderately extreme RSI: tighten to 40%
            bool rsiExtreme = (pos.IsLong && barIndicators.Rsi14 > 75) ||
                              (!pos.IsLong && barIndicators.Rsi14 > 0 && barIndicators.Rsi14 < 25);

            if (rsiVeryExtreme)
            {
                trailingOverride = trailingOverride > 0 ? Math.Min(trailingOverride, 0.25m) : 0.25m;
                _logger.LogWarning("PositionMonitor: {Symbol} RSI VERY EXTREME tightening trail to 25% rsi={Rsi:F1}",
                    symbol, barIndicators.Rsi14);
            }
            else if (rsiExtreme)
            {
                trailingOverride = trailingOverride > 0 ? Math.Min(trailingOverride, 0.40m) : 0.40m;
                _logger.LogWarning("PositionMonitor: {Symbol} RSI EXTREME tightening trail to 40% rsi={Rsi:F1}",
                    symbol, barIndicators.Rsi14);
            }
        }

        // ═══════════════════════════════════════════════════════════
        // CHECK 3.5: Regime exit — VWAP/EMA/RSI say "wrong direction"
        // AND trailing is NOT active (small/no profit) AND position is losing.
        // This makes the VWAP/EMA/RSI tighteners effective even when trailing
        // hasn't activated. Uses 40% of stop distance as the accelerated exit
        // threshold (vs 100% for full stop loss).
        // NOT a hard exit from VWAP/EMA/RSI — requires indicator confirmation
        // + adverse position + 40% stop threshold. Respects "tighteners not hard exits" principle.
        // ═══════════════════════════════════════════════════════════
        if (trailingOverride > 0 && peakProfit <= trailingActivation)
        {
            decimal softStopThreshold = effectiveStopLoss * RegimeExitStopFraction;
            if (adverse > softStopThreshold)
            {
                // Identify which indicator triggered for logging
                string indicator = "RSI";
                if (barIndicators != null)
                {
                    bool vc = (pos.IsLong && barIndicators.Vwap > 0 && currentPrice < barIndicators.Vwap) ||
                              (!pos.IsLong && barIndicators.Vwap > 0 && currentPrice > barIndicators.Vwap);
                    if (vc) indicator = "VWAP";
                    else
                    {
                        bool tr = (pos.IsLong && barIndicators.Ema9 > 0 && barIndicators.Ema20 > 0 && barIndicators.Ema9 < barIndicators.Ema20) ||
                                  (!pos.IsLong && barIndicators.Ema9 > 0 && barIndicators.Ema20 > 0 && barIndicators.Ema9 > barIndicators.Ema20);
                        if (tr) indicator = "EMA";
                    }
                }

                await _executor.ExitPositionAsync(symbol, currentPrice,
                    $"REGIME EXIT ({indicator}) adverse={adverse:F2} threshold={softStopThreshold:F2} (40% of stop={effectiveStopLoss:F2}) override={trailingOverride:P0} elapsed={elapsed:F0}s", currentScore);
                _logger.LogWarning("PositionMonitor: {Symbol} EXIT REGIME ({Indicator}) adverse={Adverse:F2} threshold={Threshold:F2}",
                    symbol, indicator, adverse, softStopThreshold);
                return;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // CHECK 4: Stop loss — volatility-adaptive with spread floor
        // Uses Max(rule stop, 2.0×ATR, entry spread) so the stop automatically
        // widens when volatility is high and tightens when it's low.
        // Falls back to rule stop + spread floor if no ATR data at entry.
        // ═══════════════════════════════════════════════════════════
        if (adverse > effectiveStopLoss)
        {
            await _executor.ExitPositionAsync(symbol, currentPrice,
                $"STOP LOSS adverse={adverse:F2} stop={effectiveStopLoss:F2} (rule={pos.StopLoss:F2} atr_floor={atrFloor:F2} spread={pos.EntrySpread:F2}) elapsed={elapsed:F0}s", currentScore);
            _logger.LogWarning("PositionMonitor: {Symbol} EXIT STOP LOSS adverse={Adverse:F2} stop={Stop:F2}",
                symbol, adverse, effectiveStopLoss);
            return;
        }

        // ═══════════════════════════════════════════════════════════
        // CHECK 5a: Breakeven stop — once profitable by 2× trailing activation,
        // move the floor to breakeven minus buffer. Prevents winning trades
        // from turning into losers.
        // AMD $218: activation = $0.33, breakeven triggers at $0.66 peak profit.
        // Previously used 2× effectiveStopLoss ($8.00 for AMD) — unreachable.
        // Buffer uses full stop distance (0.25× effectiveStopLoss) to allow normal drawdown.
        // ═══════════════════════════════════════════════════════════
        decimal breakevenBuffer = effectiveStopLoss * 0.25m;
        decimal breakevenActivation = trailingActivation * BreakevenActivationMultiple;
        if (peakProfit > breakevenActivation && currentProfit < -breakevenBuffer)
        {
            await _executor.ExitPositionAsync(symbol, currentPrice,
                $"BREAKEVEN STOP peak_profit={peakProfit:F2} now_loss={currentProfit:F2} buffer={breakevenBuffer:F2} elapsed={elapsed:F0}s", currentScore);
            _logger.LogWarning("PositionMonitor: {Symbol} EXIT BREAKEVEN STOP was_up={PeakProfit:F2} now_down={Loss:F2} buffer={Buffer:F2}",
                symbol, peakProfit, currentProfit, breakevenBuffer);
            return;
        }

        // ═══════════════════════════════════════════════════════════
        // CHECK 5b: Trailing stop using peak price (anti-wick filtered)
        // Activates when peak profit exceeds trailing activation threshold
        // (0.15% of entry price, floored at 2× entry spread).
        // AMD $218: activates at $0.33 profit. Old was $4.00 (unreachable).
        // VWAP/EMA/RSI checks above may tighten giveback via trailingOverride.
        // Giveback formula UNCHANGED (locked): 0.35 + conf×0.25 for rules, 0.50 for Stage 2.
        // ═══════════════════════════════════════════════════════════
        if (peakProfit > trailingActivation)
        {
            // Anti-wick: only use peak if it has persisted for PeakPersistenceSeconds
            bool peakSustained = (now - pos.PeakPriceSetAt).TotalSeconds >= PeakPersistenceSeconds;
            decimal trailPeak = peakSustained ? peakProfit : peakProfit * 0.90m; // Use 90% of wick peak as conservative estimate

            // Trail factor: confidence 0.62 → give back 44% (0.35 + 0.62*0.25 = 0.505 kept)
            // confidence 0 (Stage 2) → give back 50%
            decimal maxGiveBack = pos.RuleConfidence > 0
                ? (0.35m + pos.RuleConfidence * 0.25m)
                : 0.50m;

            // Apply trailing override from VWAP/EMA/RSI tighteners
            decimal effectiveGiveBack = trailingOverride > 0 ? Math.Min(maxGiveBack, trailingOverride) : maxGiveBack;

            decimal pullback = trailPeak - currentProfit;

            if (pullback > trailPeak * effectiveGiveBack)
            {
                await _executor.ExitPositionAsync(symbol, currentPrice,
                    $"TRAILING STOP peak={trailPeak:F2} now={currentProfit:F2} pullback={pullback:F2} max_giveback={effectiveGiveBack:P0} (base={maxGiveBack:P0} override={trailingOverride:F2}) sustained={peakSustained} elapsed={elapsed:F0}s", currentScore);
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

            // Past base hold time: exit if score is weakening, hold if improving.
            // ComputeCurrentScore returns 0 on data gaps (no snapshots, cold cache).
            // Score of exactly 0.000 is never natural (6 blended indicators), so treat as "unknown".
            // Don't exit profitable positions on data gaps — only exit if unknown AND losing.
            decimal scoreStrength = pos.IsLong ? currentScore : -currentScore;
            decimal entryStrength = pos.IsLong ? pos.EntryScore : -pos.EntryScore;
            bool hasValidScore = currentScore != 0;
            bool scoreWeakening = hasValidScore && scoreStrength < entryStrength * 0.5m;
            bool unknownAndLosing = !hasValidScore && currentProfit < 0;

            if (scoreWeakening)
            {
                await _executor.ExitPositionAsync(symbol, currentPrice,
                    $"TIME+WEAK {elapsed:F0}s score={currentScore:F3} (entry={pos.EntryScore:F3})", currentScore);
                _logger.LogWarning("PositionMonitor: {Symbol} EXIT TIME+WEAK {Elapsed:F0}s score={Score:F3}",
                    symbol, elapsed, currentScore);
                return;
            }

            if (unknownAndLosing)
            {
                await _executor.ExitPositionAsync(symbol, currentPrice,
                    $"TIME+NOSIGNAL {elapsed:F0}s score=0 (data gap) loss={currentProfit:F2}", currentScore);
                _logger.LogWarning("PositionMonitor: {Symbol} EXIT TIME+NOSIGNAL {Elapsed:F0}s loss={Loss:F2} (score=0, data gap)",
                    symbol, elapsed, currentProfit);
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
