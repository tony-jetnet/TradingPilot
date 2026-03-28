namespace TradingPilot.Trading;

/// <summary>
/// All day trading constants in one place. These are the locked parameters from CLAUDE.md.
/// Do NOT change without quantitative backtesting evidence.
/// </summary>
public static class DayTradeConfig
{
    // ── Composite Scoring ──
    public const decimal DefaultSetupWeight = 0.50m;
    public const decimal DefaultTimingWeight = 0.30m;
    public const decimal DefaultContextWeight = 0.20m;

    /// <summary>Minimum composite score to enter a trade.</summary>
    public const decimal MinCompositeScoreEntry = 0.35m;

    /// <summary>Minimum L2 timing score for entry confirmation when setup is active.</summary>
    public const decimal MinTimingScoreForSetup = 0.20m;

    /// <summary>Floor protection: no filter chain can reduce score below 30% of pre-filter value.</summary>
    public const decimal FilterFloorPercent = 0.30m;

    // ── Entry Gating ──
    public const decimal MaxPositionDollars = 30_000m;
    public const int MaxConcurrentPositions = 3;
    public const decimal DailyPnlStopLoss = -1500m;
    public const decimal DailyPnlStopProfit = 1500m;
    public const int RateLimitSeconds = 1800;         // 30 min between trades per symbol
    public const int LossCooldownSeconds = 3600;      // 60 min after losing trade
    public const int EntryTimeoutSeconds = 300;        // 5 min order timeout
    public const decimal SpreadPercentileReject = 0.90m;
    public const decimal MomentumThresholdPct = 0.0005m; // 0.05% of mid price

    /// <summary>Earliest ET hour:minute for entries (avoid open volatility). 9:45 AM.</summary>
    public const int EntryStartHour = 9;
    public const int EntryStartMinute = 45;

    /// <summary>Latest ET hour:minute for new entries. 3:30 PM.</summary>
    public const int EntryEndHour = 15;
    public const int EntryEndMinute = 30;

    // ── Exit Strategy ──

    /// <summary>ATR multiplier for stop loss floor. Setup's structural stop is primary; ATR is minimum.</summary>
    public const decimal StopAtrMultiplier = 1.5m;

    /// <summary>Trailing activates after this % of entry price is gained. 0.40% for day trades.</summary>
    public const decimal TrailingActivationPct = 0.004m;

    /// <summary>Breakeven activates at this multiple of trailing activation threshold.</summary>
    public const decimal BreakevenActivationMultiple = 2.5m;

    /// <summary>Regime exit: when tighteners fire AND trailing not active AND losing > this fraction of stop → exit.</summary>
    public const decimal RegimeExitStopFraction = 0.35m;

    /// <summary>Profit target: minimum % of entry price. Ensures profitable exits on big moves.</summary>
    public const decimal ProfitTargetMinPct = 0.020m;

    /// <summary>Profit target: minimum risk/reward ratio (target distance / stop distance).</summary>
    public const decimal ProfitTargetMinRiskReward = 2.0m;

    /// <summary>Anti-wick filter: peak price must persist this many seconds before trailing uses it.</summary>
    public const int PeakPersistenceSeconds = 15;

    /// <summary>Default hold time in seconds (1 hour). Nightly trainer optimizes per ticker.</summary>
    public const int DefaultHoldSeconds = 3600;

    /// <summary>Hard cap: never hold beyond this many seconds (4 hours). Must close before EOD anyway.</summary>
    public const int MaxHoldSeconds = 14400;

    /// <summary>Hold time candidates for nightly optimization.</summary>
    public static readonly int[] HoldTimeCandidates = [1800, 3600, 5400, 7200, 10800, 14400];

    /// <summary>Position monitor check interval in seconds.</summary>
    public const int ExitCheckIntervalSeconds = 15;

    // ── EOD Close ──

    /// <summary>ET hour to start tightening trailing stops (3:30 PM → 15:30).</summary>
    public const int EodTightenHour = 15;
    public const int EodTightenMinute = 30;

    /// <summary>ET hour to start exiting positions (3:45 PM → 15:45).</summary>
    public const int EodExitHour = 15;
    public const int EodExitMinute = 45;

    /// <summary>ET hour for hard close — market order, no exceptions (3:50 PM → 15:50).</summary>
    public const int EodHardCloseHour = 15;
    public const int EodHardCloseMinute = 50;

    /// <summary>EOD trailing giveback override (very tight).</summary>
    public const decimal EodTrailingGiveback = 0.20m;

    // ── Trailing Giveback ──

    /// <summary>Base giveback for trailing stop (before setup strength scaling).</summary>
    public const decimal TrailingGivebackBase = 0.35m;

    /// <summary>Setup strength multiplier for giveback (higher strength → more room).</summary>
    public const decimal TrailingGivebackStrengthScale = 0.20m;

    /// <summary>VWAP/EMA/RSI tightening override giveback.</summary>
    public const decimal TightenerGiveback = 0.30m;

    /// <summary>Very extreme RSI tightening (RSI > 80 or < 20).</summary>
    public const decimal RsiExtremeGiveback = 0.25m;

    /// <summary>Moderate RSI tightening (RSI 75-80 or 20-25).</summary>
    public const decimal RsiModerateGiveback = 0.40m;

    /// <summary>Setup invalidation tightening.</summary>
    public const decimal InvalidationGiveback = 0.25m;

    // ── Setup Invalidation Hard Exit ──

    /// <summary>If setup invalidated AND losing > this fraction of stop AND trailing not active → hard exit.</summary>
    public const decimal InvalidationHardExitFraction = 0.30m;

    // ── Opposing Setup ──

    /// <summary>Minimum opposing setup strength to trigger exit.</summary>
    public const decimal OpposingSetupMinStrength = 0.50m;

    /// <summary>Minimum L2 timing score to confirm opposing setup exit.</summary>
    public const decimal OpposingSetupMinTiming = 0.20m;

    // ── Breakeven ──

    /// <summary>Breakeven stop buffer as fraction of effective stop loss.</summary>
    public const decimal BreakevenBufferFraction = 0.20m;

    // ── Grace Periods (as fraction of hold time) ──
    public const decimal VwapGraceFraction = 0.40m;
    public const int VwapGraceMaxSeconds = 1800;       // 30 min max

    public const decimal EmaGraceFraction = 0.25m;
    public const int EmaGraceMaxSeconds = 1200;        // 20 min max

    public const decimal RsiGraceFraction = 0.15m;
    public const int RsiGraceMaxSeconds = 600;         // 10 min max

    public const decimal InvalidationGraceFraction = 0.25m;
    public const int InvalidationGraceMaxSeconds = 1200;

    // ── Pre-Market Scanner ──
    public const int ActiveSymbolCount = 10;
    public const decimal ScannerWeightGap = 0.25m;
    public const decimal ScannerWeightVolume = 0.20m;
    public const decimal ScannerWeightCatalyst = 0.20m;
    public const decimal ScannerWeightCapitalFlow = 0.15m;
    public const decimal ScannerWeightSetupQuality = 0.15m;
    public const decimal ScannerWeightAtr = 0.05m;
    public const decimal ScannerMinAtrPct = 0.008m;    // 0.8% minimum for day trading

    // ── Context Scorer Weights ──
    public const decimal ContextWeightCapitalFlow = 0.25m;
    public const decimal ContextWeightNewsSentiment = 0.30m;
    public const decimal ContextWeightShortFloat = 0.15m;
    public const decimal ContextWeightTrendAlignment = 0.15m;
    // Remaining 0.15 from time-of-day + catalyst/earnings (flat add/subtract, not weighted)

    // ── News Risk Thresholds ──
    public const int HighNewsVelocityThreshold = 5;   // > 5 articles in 2 hours
    public const int EarningsProximityDays = 1;        // reduce size within 1 day of earnings
    public const decimal NewsRiskPositionReduction = 0.50m; // half position on high news/earnings
    public const decimal ConflictingSentimentPenalty = 0.20m; // reduce setup strength by 0.20

    // ── Setup Detection Thresholds ──

    /// <summary>Minimum setup strength to emit a signal. Below this, setup is too weak.</summary>
    public const decimal MinSetupStrength = 0.30m;

    /// <summary>Minimum risk/reward ratio for any setup to be valid.</summary>
    public const decimal MinRiskReward = 2.0m;

    /// <summary>Setup shelf life in minutes. Setups expire if no entry taken.</summary>
    public const int SetupExpiryMinutes = 45;

    // ── TrendFollow thresholds ──
    /// <summary>Max distance from EMA20 (as ATR fraction) for pullback entry.</summary>
    public const decimal TrendPullbackMaxAtr = 0.30m;

    // ── VwapBounce thresholds ──
    /// <summary>Max distance from VWAP (% of price) for bounce detection.</summary>
    public const decimal VwapBounceMaxDistancePct = 0.003m; // 0.3%
    /// <summary>Min volume ratio for VWAP bounce confirmation.</summary>
    public const decimal VwapBounceMinVolumeRatio = 1.5m;

    // ── Breakout thresholds ──
    /// <summary>Minimum consolidation duration in seconds for breakout setup.</summary>
    public const int BreakoutMinConsolidationSeconds = 1800; // 30 min
    /// <summary>Min volume ratio for breakout confirmation.</summary>
    public const decimal BreakoutMinVolumeRatio = 2.0m;

    // ── Reversal thresholds ──
    /// <summary>RSI level for overbought reversal (short) on 5m.</summary>
    public const decimal ReversalRsiOverbought = 80m;
    /// <summary>RSI level for oversold reversal (long) on 5m.</summary>
    public const decimal ReversalRsiOversold = 20m;
}
