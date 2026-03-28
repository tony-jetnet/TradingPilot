namespace TradingPilot.Trading;

/// <summary>
/// Complete indicator state at a point in time, aggregating data from multiple caches.
/// Used as input to SetupDetector, ContextScorer, and for persisting to TradingSignalRecord/BarSetup.
/// Avoids ad-hoc reading from multiple caches by collecting everything once.
/// </summary>
public class IndicatorSnapshot
{
    // ── Price ──
    public decimal Price { get; set; }
    public decimal Spread { get; set; }

    // ── 1-min bar indicators (from BarIndicatorCache) ──
    public decimal Ema9 { get; set; }
    public decimal Ema20 { get; set; }
    public decimal Rsi14 { get; set; }
    public decimal Vwap { get; set; }
    public decimal Atr14 { get; set; }
    public decimal Atr14Pct { get; set; }
    public decimal VolumeRatio { get; set; }
    public int TrendDirection { get; set; }
    public bool AboveVwap { get; set; }

    // ── 5-min bar indicators (from BarIndicatorCache, Phase 3) ──
    public decimal Ema20_5m { get; set; }
    public decimal Ema50_5m { get; set; }
    public decimal Rsi14_5m { get; set; }
    public decimal Atr14_5m { get; set; }
    public decimal VolumeRatio_5m { get; set; }
    public int TrendDirection_5m { get; set; }
    public bool AboveVwap_5m { get; set; }

    // ── 15-min bar indicators (from BarIndicatorCache, Phase 3) ──
    public decimal Ema20_15m { get; set; }
    public decimal Ema50_15m { get; set; }
    public decimal Rsi14_15m { get; set; }
    public int TrendDirection_15m { get; set; }

    // ── L2-derived features (from TickDataCache) ──
    /// <summary>Order book imbalance. Named "Obi" for backward compat with StrategyRuleEvaluator.</summary>
    public decimal Obi { get; set; }
    public decimal ObiSmoothed { get; set; }
    public decimal Wobi { get; set; }
    public decimal PressureRoc { get; set; }
    public decimal SpreadSignal { get; set; }
    public decimal LargeOrderSignal { get; set; }
    public decimal TickMomentum { get; set; }
    public decimal BookDepthRatio { get; set; }
    public decimal BidWallSize { get; set; }
    public decimal AskWallSize { get; set; }
    public decimal BidSweepCost { get; set; }
    public decimal AskSweepCost { get; set; }
    public decimal ImbalanceVelocity { get; set; }
    public decimal SpreadPercentile { get; set; }

    // ── Context (from DB, populated by SignalOrchestrator) ──
    public decimal? NewsSentiment { get; set; }
    public string? CatalystType { get; set; }
    public int NewsCount2Hr { get; set; }
    public decimal? CapitalFlowScore { get; set; }
    public decimal? ShortFloat { get; set; }
    public int? DaysToEarnings { get; set; }
    public decimal RelativeVolume { get; set; }

    // ── Derived ──

    /// <summary>
    /// VWAP deviation: (Price - VWAP) / VWAP, clamped to [-1, +1] via ×10 scaling.
    /// Same formula as existing VWAP score in MarketMicrostructureAnalyzer.
    /// </summary>
    public decimal VwapDeviation => Vwap > 0
        ? Math.Clamp((Price - Vwap) / Vwap * 10m, -1m, 1m)
        : 0m;

    /// <summary>
    /// Multi-timeframe trend alignment [-1, +1].
    /// Weighted: 1m contributes 0.30, 5m contributes 0.70.
    /// Strong alignment (both positive or both negative) = closer to ±1.
    /// </summary>
    public decimal TrendAlignment => TrendDirection * 0.30m + TrendDirection_5m * 0.70m;

    /// <summary>
    /// Populate 1m and L2 fields from existing BarIndicators cache object.
    /// Called by SignalOrchestrator to fill the snapshot from cached data.
    /// </summary>
    public void FillFromBarIndicators(BarIndicators bars)
    {
        Ema9 = bars.Ema9;
        Ema20 = bars.Ema20;
        Rsi14 = bars.Rsi14;
        Vwap = bars.Vwap;
        Atr14 = bars.Atr14;
        Atr14Pct = bars.Atr14Pct;
        VolumeRatio = bars.VolumeRatio;
        TrendDirection = bars.TrendDirection;
        AboveVwap = bars.AboveVwap;

        // 5m fields (populated after Phase 3 extends BarIndicators)
        Ema20_5m = bars.Ema20_5m;
        Ema50_5m = bars.Ema50_5m;
        Rsi14_5m = bars.Rsi14_5m;
        Atr14_5m = bars.Atr14_5m;
        VolumeRatio_5m = bars.VolumeRatio_5m;
        TrendDirection_5m = bars.TrendDirection_5m;
        AboveVwap_5m = bars.AboveVwap_5m;

        // 15m fields
        Ema20_15m = bars.Ema20_15m;
        Ema50_15m = bars.Ema50_15m;
        Rsi14_15m = bars.Rsi14_15m;
        TrendDirection_15m = bars.TrendDirection_15m;
    }

    /// <summary>
    /// Populate L2-derived fields from TickDataCache live data.
    /// </summary>
    public void FillFromTickData(TickerLiveData tick)
    {
        TickMomentum = tick.TickMomentum;
        BookDepthRatio = tick.BookDepthRatio;
        BidWallSize = tick.BidWallSize;
        AskWallSize = tick.AskWallSize;
        ImbalanceVelocity = tick.ImbalanceVelocity;
        SpreadPercentile = tick.SpreadPercentile;
    }
}
