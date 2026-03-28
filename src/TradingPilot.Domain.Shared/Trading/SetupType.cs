namespace TradingPilot.Trading;

/// <summary>
/// Day-trade setup types detected by SetupDetector from bar data.
/// Each type has specific entry conditions, stop/target logic, and invalidation criteria.
/// </summary>
public enum SetupType : byte
{
    /// <summary>No setup — L2-only signal or rule-based.</summary>
    None = 0,

    /// <summary>
    /// EMA9 > EMA20 > EMA50 on 5m, all rising, price pulls back to EMA20 zone.
    /// Stop: below EMA50 or recent swing low. Target: 2-3× ATR above entry.
    /// Invalidation: EMA20 crosses below EMA50 on 5m.
    /// </summary>
    TrendFollow = 1,

    /// <summary>
    /// Price touches VWAP from above, bounces with VolumeRatio > 1.5, RSI 40-60.
    /// Stop: below VWAP by 0.3%. Target: prior high or VWAP + 2× pullback distance.
    /// Invalidation: price closes below VWAP on 5m bar.
    /// </summary>
    VwapBounce = 2,

    /// <summary>
    /// Price breaks above 15m consolidation range (>30 min range) with volume > 2× avg.
    /// Stop: mid-point of consolidation range. Target: range height projected above breakout.
    /// Invalidation: price closes back inside range.
    /// </summary>
    Breakout = 3,

    /// <summary>
    /// RSI divergence (price new low, RSI higher low) + volume + key support level.
    /// Stop: below the swing low that formed divergence. Target: EMA20 on 15m.
    /// Invalidation: price makes new low below divergence low.
    /// </summary>
    Reversal = 4,
}
