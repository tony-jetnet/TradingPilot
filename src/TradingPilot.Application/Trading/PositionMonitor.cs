using Microsoft.Extensions.Logging;
using TradingPilot.Symbols;
using TradingPilot.Trading;

namespace TradingPilot.Trading;

/// <summary>
/// Background monitor that continuously re-evaluates open positions every 15 seconds.
/// Implements 9 thesis-aware exit types for day trading plus EOD mandatory close.
/// Handles all exit logic except opposing setup signals (which PaperTradingExecutor handles event-driven).
/// Also verifies pending orders and syncs position state with broker.
/// </summary>
public class PositionMonitor : IDisposable
{
    private readonly PaperTradingExecutor _executor;
    private readonly MarketMicrostructureAnalyzer _analyzer;
    private readonly SetupDetector _setupDetector;
    private readonly L2BookCache _l2Cache;
    private readonly TickDataCache _tickCache;
    private readonly BarIndicatorCache _barCache;
    private readonly ILogger<PositionMonitor> _logger;

    private readonly Timer _timer;
    private int _tickCount;
    private int _evaluating;
    private bool _disposed;
    private bool _initialized;

    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    public PositionMonitor(
        PaperTradingExecutor executor,
        MarketMicrostructureAnalyzer analyzer,
        SetupDetector setupDetector,
        L2BookCache l2Cache,
        TickDataCache tickCache,
        BarIndicatorCache barCache,
        ILogger<PositionMonitor> logger)
    {
        _executor = executor;
        _analyzer = analyzer;
        _setupDetector = setupDetector;
        _l2Cache = l2Cache;
        _tickCache = tickCache;
        _barCache = barCache;
        _logger = logger;

        // Day trading: 15-second interval (up from 5s — less noise on longer holds)
        _timer = new Timer(OnTimerTick, null, TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(DayTradeConfig.ExitCheckIntervalSeconds));
        _logger.LogWarning("PositionMonitor started: checking positions every {Interval}s, broker sync every 30s",
            DayTradeConfig.ExitCheckIntervalSeconds);
    }

    private async void OnTimerTick(object? state)
    {
        if (_disposed) return;

        if (Interlocked.CompareExchange(ref _evaluating, 1, 0) != 0)
            return;

        try
        {
            _tickCount++;

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

            try { await _executor.VerifyPendingOrdersAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "PositionMonitor: pending order verification failed"); }

            var positions = _executor.GetOpenPositions();

            if (positions.Count > 0)
                _logger.LogDebug("PositionMonitor: checking {Count} positions", positions.Count);

            foreach (var (symbol, pos) in positions)
            {
                try
                {
                    if (_executor.HasPendingExit(symbol)) continue;
                    await EvaluatePositionAsync(symbol, pos);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "PositionMonitor: error evaluating position for {Symbol}", symbol);
                }
            }

            // Broker sync every ~30 seconds
            int syncInterval = 30 / DayTradeConfig.ExitCheckIntervalSeconds;
            if (syncInterval < 1) syncInterval = 1;
            if (_tickCount % syncInterval == 0)
            {
                try { await _executor.SyncWithBrokerAsync(); }
                catch (Exception ex) { _logger.LogError(ex, "PositionMonitor: broker sync failed"); }
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
        // ── Get current price ──
        decimal currentPrice = 0;
        var snapshots = _l2Cache.GetSnapshots(pos.TickerId, 1);
        if (snapshots.Count > 0)
            currentPrice = snapshots[^1].MidPrice;
        else
        {
            var fallbackTick = _tickCache.GetData(pos.TickerId);
            if (fallbackTick?.LastPrice > 0) currentPrice = fallbackTick.LastPrice;
            else
            {
                var barInd = _barCache.GetIndicators(pos.TickerId);
                if (barInd != null && barInd.Ema9 > 0) currentPrice = barInd.Ema9;
            }
        }
        if (currentPrice <= 0) return;

        var now = DateTime.UtcNow;
        var nowEt = TimeZoneInfo.ConvertTimeFromUtc(now, Eastern);
        decimal elapsed = (decimal)(now - pos.EntryTime).TotalSeconds;
        decimal currentScore = _analyzer.ComputeCurrentScore(pos.TickerId);

        // Update peak favorable score
        if (pos.IsLong && currentScore > pos.PeakFavorableScore)
            pos.PeakFavorableScore = currentScore;
        else if (!pos.IsLong && currentScore < pos.PeakFavorableScore)
            pos.PeakFavorableScore = currentScore;

        // Update peak favorable price with timestamp
        bool newPeak = (pos.IsLong && currentPrice > pos.PeakFavorablePrice) ||
                       (!pos.IsLong && currentPrice < pos.PeakFavorablePrice);
        if (newPeak)
        {
            pos.PeakFavorablePrice = currentPrice;
            pos.PeakPriceSetAt = now;
        }

        // Track MaxFavorableExcursion for analytics
        decimal mfe = pos.IsLong ? currentPrice - pos.EntryPrice : pos.EntryPrice - currentPrice;
        if (mfe > pos.MaxFavorableExcursion) pos.MaxFavorableExcursion = mfe;

        var barIndicators = _barCache.GetIndicators(pos.TickerId);

        // Pre-compute core values
        decimal adverse = pos.IsLong ? pos.EntryPrice - currentPrice : currentPrice - pos.EntryPrice;
        decimal currentProfit = -adverse; // positive = profitable

        // Effective stop: structural stop from setup (if available), floored at ATR, floored at spread
        decimal atrFloor = pos.EntryAtr > 0 ? pos.EntryAtr * DayTradeConfig.StopAtrMultiplier : 0;
        decimal setupStopDist = pos.HasSetup ? Math.Abs(pos.EntryPrice - pos.SetupStopLevel) : 0;
        decimal effectiveStopLoss = Math.Max(Math.Max(setupStopDist, atrFloor), pos.EntrySpread);
        if (effectiveStopLoss <= 0) effectiveStopLoss = pos.EntryPrice * 0.02m; // 2% fallback

        decimal peakProfit = pos.IsLong
            ? pos.PeakFavorablePrice - pos.EntryPrice
            : pos.EntryPrice - pos.PeakFavorablePrice;

        decimal trailingActivation = Math.Max(pos.EntryPrice * DayTradeConfig.TrailingActivationPct, pos.EntrySpread * 2m);

        // Holdtime: use day trade config or per-ticker optimized
        int holdSeconds = pos.HoldSeconds > 0 ? pos.HoldSeconds : DayTradeConfig.DefaultHoldSeconds;

        // ═══════════════════════════════════════════════════════════
        // EXIT 0: EOD MANDATORY CLOSE — no overnight positions
        // ═══════════════════════════════════════════════════════════
        if (nowEt.Hour > DayTradeConfig.EodHardCloseHour ||
            (nowEt.Hour == DayTradeConfig.EodHardCloseHour && nowEt.Minute >= DayTradeConfig.EodHardCloseMinute))
        {
            await _executor.ExitPositionAsync(symbol, currentPrice,
                $"EOD HARD CLOSE {nowEt:HH:mm} ET profit={currentProfit:F2} elapsed={elapsed:F0}s", currentScore);
            return;
        }

        // ═══════════════════════════════════════════════════════════
        // EXIT 1: STOP LOSS — structural + ATR floor
        // ═══════════════════════════════════════════════════════════
        if (adverse > effectiveStopLoss)
        {
            await _executor.ExitPositionAsync(symbol, currentPrice,
                $"STOP LOSS adverse={adverse:F2} stop={effectiveStopLoss:F2} (setup={setupStopDist:F2} atr={atrFloor:F2}) elapsed={elapsed:F0}s", currentScore);
            return;
        }

        // ═══════════════════════════════════════════════════════════
        // EXIT 2: PROFIT TARGET
        // ═══════════════════════════════════════════════════════════
        decimal setupTargetDist = pos.HasSetup ? Math.Abs(pos.SetupTargetLevel - pos.EntryPrice) : 0;
        decimal profitTarget = Math.Max(Math.Max(setupTargetDist, pos.EntryPrice * DayTradeConfig.ProfitTargetMinPct),
                                        effectiveStopLoss * DayTradeConfig.ProfitTargetMinRiskReward);
        if (currentProfit >= profitTarget)
        {
            await _executor.ExitPositionAsync(symbol, currentPrice,
                $"PROFIT TARGET hit={currentProfit:F2} target={profitTarget:F2} elapsed={elapsed:F0}s", currentScore);
            return;
        }

        // ═══════════════════════════════════════════════════════════
        // EXIT 3: SETUP INVALIDATION — thesis broke
        // ═══════════════════════════════════════════════════════════
        decimal trailingOverride = -1m;
        int invalidationGrace = Math.Min(DayTradeConfig.InvalidationGraceMaxSeconds, (int)(holdSeconds * DayTradeConfig.InvalidationGraceFraction));

        if (pos.HasSetup && elapsed >= invalidationGrace && barIndicators != null)
        {
            bool invalidated = _setupDetector.IsSetupInvalidated(pos.EntrySetupType, barIndicators, pos);
            if (invalidated)
            {
                trailingOverride = DayTradeConfig.InvalidationGiveback;
                _logger.LogWarning("PositionMonitor: {Symbol} SETUP INVALIDATED ({Type}) tightening trail to {Giveback:P0}",
                    symbol, pos.EntrySetupType, DayTradeConfig.InvalidationGiveback);

                // Hard exit if trailing not active AND losing > 30% of stop
                if (peakProfit <= trailingActivation && adverse > effectiveStopLoss * DayTradeConfig.InvalidationHardExitFraction)
                {
                    await _executor.ExitPositionAsync(symbol, currentPrice,
                        $"SETUP INVALIDATION ({pos.EntrySetupType}) adverse={adverse:F2} threshold={effectiveStopLoss * DayTradeConfig.InvalidationHardExitFraction:F2} elapsed={elapsed:F0}s", currentScore);
                    return;
                }
            }
        }

        // ═══════════════════════════════════════════════════════════
        // TRAILING TIGHTENERS: VWAP, EMA, RSI, EOD
        // ═══════════════════════════════════════════════════════════
        int vwapGrace = Math.Min(DayTradeConfig.VwapGraceMaxSeconds, (int)(holdSeconds * DayTradeConfig.VwapGraceFraction));
        int emaGrace = Math.Min(DayTradeConfig.EmaGraceMaxSeconds, (int)(holdSeconds * DayTradeConfig.EmaGraceFraction));
        int rsiGrace = Math.Min(DayTradeConfig.RsiGraceMaxSeconds, (int)(holdSeconds * DayTradeConfig.RsiGraceFraction));

        // VWAP cross
        if (elapsed >= vwapGrace && barIndicators != null)
        {
            bool vwapExit = (pos.IsLong && barIndicators.Vwap > 0 && currentPrice < barIndicators.Vwap) ||
                            (!pos.IsLong && barIndicators.Vwap > 0 && currentPrice > barIndicators.Vwap);
            if (vwapExit)
            {
                trailingOverride = trailingOverride > 0 ? Math.Min(trailingOverride, DayTradeConfig.TightenerGiveback) : DayTradeConfig.TightenerGiveback;
            }
        }

        // EMA trend reversal (5m)
        if (elapsed >= emaGrace && barIndicators != null)
        {
            bool trendReversed = (pos.IsLong && barIndicators.Ema20_5m > 0 && barIndicators.Ema50_5m > 0 && barIndicators.Ema20_5m < barIndicators.Ema50_5m) ||
                                 (!pos.IsLong && barIndicators.Ema20_5m > 0 && barIndicators.Ema50_5m > 0 && barIndicators.Ema20_5m > barIndicators.Ema50_5m);
            if (trendReversed)
            {
                trailingOverride = trailingOverride > 0 ? Math.Min(trailingOverride, DayTradeConfig.TightenerGiveback) : DayTradeConfig.TightenerGiveback;
            }
        }

        // RSI extreme
        if (elapsed >= rsiGrace && barIndicators != null && barIndicators.Rsi14 > 0)
        {
            bool rsiVeryExtreme = (pos.IsLong && barIndicators.Rsi14 > 80) || (!pos.IsLong && barIndicators.Rsi14 < 20);
            bool rsiExtreme = (pos.IsLong && barIndicators.Rsi14 > 75) || (!pos.IsLong && barIndicators.Rsi14 < 25);

            if (rsiVeryExtreme)
                trailingOverride = trailingOverride > 0 ? Math.Min(trailingOverride, DayTradeConfig.RsiExtremeGiveback) : DayTradeConfig.RsiExtremeGiveback;
            else if (rsiExtreme)
                trailingOverride = trailingOverride > 0 ? Math.Min(trailingOverride, DayTradeConfig.RsiModerateGiveback) : DayTradeConfig.RsiModerateGiveback;
        }

        // EOD tightening (3:30 PM ET)
        if (nowEt.Hour > DayTradeConfig.EodTightenHour ||
            (nowEt.Hour == DayTradeConfig.EodTightenHour && nowEt.Minute >= DayTradeConfig.EodTightenMinute))
        {
            trailingOverride = trailingOverride > 0 ? Math.Min(trailingOverride, DayTradeConfig.EodTrailingGiveback) : DayTradeConfig.EodTrailingGiveback;
        }

        // EOD exit (3:45 PM ET): exit profitable positions at market, losing positions at limit
        if (nowEt.Hour > DayTradeConfig.EodExitHour ||
            (nowEt.Hour == DayTradeConfig.EodExitHour && nowEt.Minute >= DayTradeConfig.EodExitMinute))
        {
            await _executor.ExitPositionAsync(symbol, currentPrice,
                $"EOD EXIT {nowEt:HH:mm} ET profit={currentProfit:F2} elapsed={elapsed:F0}s", currentScore);
            return;
        }

        // ═══════════════════════════════════════════════════════════
        // EXIT 5: REGIME EXIT — tighteners active + trailing not active + losing
        // ═══════════════════════════════════════════════════════════
        if (trailingOverride > 0 && peakProfit <= trailingActivation)
        {
            decimal softStopThreshold = effectiveStopLoss * DayTradeConfig.RegimeExitStopFraction;
            if (adverse > softStopThreshold)
            {
                await _executor.ExitPositionAsync(symbol, currentPrice,
                    $"REGIME EXIT adverse={adverse:F2} threshold={softStopThreshold:F2} ({DayTradeConfig.RegimeExitStopFraction:P0} of stop={effectiveStopLoss:F2}) elapsed={elapsed:F0}s", currentScore);
                return;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // EXIT 6: BREAKEVEN STOP
        // ═══════════════════════════════════════════════════════════
        decimal breakevenBuffer = effectiveStopLoss * DayTradeConfig.BreakevenBufferFraction;
        decimal breakevenActivation = trailingActivation * DayTradeConfig.BreakevenActivationMultiple;
        if (peakProfit > breakevenActivation && currentProfit < -breakevenBuffer)
        {
            await _executor.ExitPositionAsync(symbol, currentPrice,
                $"BREAKEVEN STOP peak_profit={peakProfit:F2} now_loss={currentProfit:F2} buffer={breakevenBuffer:F2} elapsed={elapsed:F0}s", currentScore);
            return;
        }

        // ═══════════════════════════════════════════════════════════
        // EXIT 4: TRAILING STOP (anti-wick filtered)
        // ═══════════════════════════════════════════════════════════
        if (peakProfit > trailingActivation)
        {
            bool peakSustained = (now - pos.PeakPriceSetAt).TotalSeconds >= DayTradeConfig.PeakPersistenceSeconds;
            decimal trailPeak = peakSustained ? peakProfit : peakProfit * 0.90m;

            // Giveback: base + setup strength scaling (higher quality → more room)
            decimal maxGiveBack = pos.HasSetup
                ? DayTradeConfig.TrailingGivebackBase + pos.SetupStrength * DayTradeConfig.TrailingGivebackStrengthScale
                : 0.50m; // L2-only: fixed 50%

            // Apply trailing override from tighteners (VWAP/EMA/RSI/invalidation/EOD)
            decimal effectiveGiveBack = trailingOverride > 0 ? Math.Min(maxGiveBack, trailingOverride) : maxGiveBack;

            decimal pullback = trailPeak - currentProfit;

            if (pullback > trailPeak * effectiveGiveBack)
            {
                await _executor.ExitPositionAsync(symbol, currentPrice,
                    $"TRAILING STOP peak={trailPeak:F2} now={currentProfit:F2} pullback={pullback:F2} giveback={effectiveGiveBack:P0} (base={maxGiveBack:P0} override={trailingOverride:F2}) sustained={peakSustained} elapsed={elapsed:F0}s", currentScore);
                return;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // EXIT 8: ADAPTIVE TIME GATE
        // ═══════════════════════════════════════════════════════════
        if (elapsed >= holdSeconds)
        {
            // Hard cap: never hold beyond 2× holdSeconds or DayTradeConfig.MaxHoldSeconds
            int maxHold = Math.Min(holdSeconds * 2, DayTradeConfig.MaxHoldSeconds);
            if (elapsed >= maxHold)
            {
                await _executor.ExitPositionAsync(symbol, currentPrice,
                    $"TIME CAP {elapsed:F0}s (max={maxHold}s) score={currentScore:F3} profit={currentProfit:F2}", currentScore);
                return;
            }

            // Adaptive: check setup health + score strength
            decimal scoreStrength = pos.IsLong ? currentScore : -currentScore;
            decimal entryStrength = pos.IsLong ? pos.EntryScore : -pos.EntryScore;
            bool hasValidScore = currentScore != 0;
            bool scoreWeakening = hasValidScore && scoreStrength < entryStrength * 0.5m;
            bool unknownAndLosing = !hasValidScore && currentProfit < 0;

            // If setup still valid + profitable → hold
            if (pos.HasSetup && barIndicators != null &&
                !_setupDetector.IsSetupInvalidated(pos.EntrySetupType, barIndicators, pos) &&
                currentProfit > 0)
            {
                // Setup thesis still intact and profitable → extend hold
                _logger.LogDebug("PositionMonitor: {Symbol} past hold time ({Elapsed:F0}s/{Hold}s) but setup intact + profitable, holding",
                    symbol, elapsed, holdSeconds);
            }
            else if (pos.HasSetup && barIndicators != null &&
                     !_setupDetector.IsSetupInvalidated(pos.EntrySetupType, barIndicators, pos) &&
                     currentProfit <= 0)
            {
                // Setup intact but losing → tighten trailing to 30%
                if (trailingOverride < 0 || trailingOverride > DayTradeConfig.TightenerGiveback)
                    trailingOverride = DayTradeConfig.TightenerGiveback;
            }
            else if (scoreWeakening)
            {
                await _executor.ExitPositionAsync(symbol, currentPrice,
                    $"TIME+WEAK {elapsed:F0}s score={currentScore:F3} (entry={pos.EntryScore:F3}) profit={currentProfit:F2}", currentScore);
                return;
            }
            else if (unknownAndLosing)
            {
                await _executor.ExitPositionAsync(symbol, currentPrice,
                    $"TIME+NOSIGNAL {elapsed:F0}s score=0 (data gap) loss={currentProfit:F2}", currentScore);
                return;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }
}
