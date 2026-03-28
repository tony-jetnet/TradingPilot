using Microsoft.Extensions.Logging;

namespace TradingPilot.Trading;

/// <summary>
/// Scans bar indicator data for 4 day-trade setup types.
/// Pure domain logic — no DB access, no side effects.
/// Registered as singleton. Called by SignalOrchestrator every 5-min bar close.
/// </summary>
public class SetupDetector
{
    private readonly ILogger<SetupDetector> _logger;

    public SetupDetector(ILogger<SetupDetector> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Detect all valid setups from current bar indicators and price.
    /// Returns 0 or more SetupResults (multiple setups can co-exist).
    /// Sorted by strength descending.
    /// </summary>
    public List<SetupResult> DetectSetups(long tickerId, BarIndicators indicators, decimal currentPrice)
    {
        var results = new List<SetupResult>();

        if (currentPrice <= 0 || indicators.Atr14_5m <= 0) return results;

        var trendFollow = DetectTrendFollow(indicators, currentPrice);
        if (trendFollow != null) results.Add(trendFollow);

        var vwapBounce = DetectVwapBounce(indicators, currentPrice);
        if (vwapBounce != null) results.Add(vwapBounce);

        var breakout = DetectBreakout(indicators, currentPrice);
        if (breakout != null) results.Add(breakout);

        var reversal = DetectReversal(indicators, currentPrice);
        if (reversal != null) results.Add(reversal);

        // Filter by minimum strength and risk/reward
        results = results
            .Where(r => r.Strength >= DayTradeConfig.MinSetupStrength)
            .Where(r => r.RiskReward >= DayTradeConfig.MinRiskReward)
            .OrderByDescending(r => r.Strength)
            .ToList();

        foreach (var r in results)
        {
            _logger.LogInformation(
                "Setup detected: {Type} {Direction} strength={Strength:F2} for tickerId={TickerId} price={Price:F2} " +
                "stop={Stop:F2} target={Target:F2} R:R={RR:F1}",
                r.Type, r.Direction, r.Strength, tickerId, currentPrice, r.StopLevel, r.TargetLevel, r.RiskReward);
        }

        return results;
    }

    /// <summary>
    /// Check if a position's setup thesis has been invalidated by current indicators.
    /// Called by PositionMonitor for EXIT 3: Setup Invalidation.
    /// </summary>
    public bool IsSetupInvalidated(SetupType type, BarIndicators currentIndicators, PositionState pos)
    {
        return type switch
        {
            // TREND_FOLLOW long: invalidated when EMA20 crosses below EMA50 on 5m
            // TREND_FOLLOW short: invalidated when EMA20 crosses above EMA50 on 5m
            SetupType.TrendFollow => pos.IsLong
                ? currentIndicators.Ema20_5m > 0 && currentIndicators.Ema50_5m > 0
                  && currentIndicators.Ema20_5m < currentIndicators.Ema50_5m
                : currentIndicators.Ema20_5m > 0 && currentIndicators.Ema50_5m > 0
                  && currentIndicators.Ema20_5m > currentIndicators.Ema50_5m,

            // VWAP_BOUNCE long: invalidated when price closes below VWAP on 5m
            // VWAP_BOUNCE short: invalidated when price closes above VWAP
            SetupType.VwapBounce => pos.IsLong
                ? !currentIndicators.AboveVwap_5m
                : currentIndicators.AboveVwap_5m,

            // BREAKOUT long: price fell back below entry zone (stop level acts as range mid)
            // Use a proxy: price below EMA20_5m AND trend reversed
            SetupType.Breakout => pos.IsLong
                ? currentIndicators.TrendDirection_5m < 0
                : currentIndicators.TrendDirection_5m > 0,

            // REVERSAL long: RSI makes new extreme (deeper oversold) → thesis dead
            // REVERSAL short: RSI makes new extreme (deeper overbought)
            SetupType.Reversal => pos.IsLong
                ? currentIndicators.Rsi14_5m < DayTradeConfig.ReversalRsiOversold
                : currentIndicators.Rsi14_5m > DayTradeConfig.ReversalRsiOverbought,

            _ => false,
        };
    }

    private SetupResult? DetectTrendFollow(BarIndicators ind, decimal price)
    {
        if (ind.Ema20_5m <= 0 || ind.Ema50_5m <= 0 || ind.Atr14_5m <= 0) return null;

        // ── LONG: EMA20_5m > EMA50_5m, price pulls back to EMA20 zone ──
        bool bullishTrend = ind.Ema20_5m > ind.Ema50_5m && ind.TrendDirection_5m > 0;
        decimal distToEma20 = price - ind.Ema20_5m;
        decimal pullbackDepth = ind.Atr14_5m > 0 ? Math.Abs(distToEma20) / ind.Atr14_5m : 999;
        bool nearEma20Long = distToEma20 >= -ind.Atr14_5m * DayTradeConfig.TrendPullbackMaxAtr
                          && distToEma20 <= ind.Atr14_5m * 0.5m; // Not too far above either
        bool rsiOkLong = ind.Rsi14_5m >= 40 && ind.Rsi14_5m <= 70;

        if (bullishTrend && nearEma20Long && rsiOkLong)
        {
            decimal atr = ind.Atr14_5m;
            decimal stopLevel = Math.Min(ind.Ema50_5m, price - 1.5m * atr);
            decimal stopDistance = price - stopLevel;
            // Target must be at least MinRiskReward × stopDistance
            decimal targetLevel = price + Math.Max(2.5m * atr, stopDistance * DayTradeConfig.MinRiskReward);

            // Strength: tighter pullback + stronger trend alignment + volume = higher
            decimal pullbackScore = Math.Max(0, 1.0m - pullbackDepth / DayTradeConfig.TrendPullbackMaxAtr);
            decimal trendScore = ind.TrendDirection_15m > 0 ? 0.3m : 0; // Multi-TF alignment bonus
            decimal volumeScore = ind.VolumeRatio_5m > 1.2m ? 0.2m : 0;
            decimal strength = Math.Clamp(0.40m + pullbackScore * 0.30m + trendScore + volumeScore, 0, 1);

            return new SetupResult
            {
                Type = SetupType.TrendFollow,
                Direction = SignalType.Buy,
                Strength = strength,
                EntryZoneLow = price - atr * 0.1m,
                EntryZoneHigh = price + atr * 0.1m,
                StopLevel = Math.Round(stopLevel, 4),
                TargetLevel = Math.Round(targetLevel, 4),
                DetectionPrice = price,
                Atr = atr,
                ExpiresAt = DateTime.UtcNow.AddMinutes(DayTradeConfig.SetupExpiryMinutes),
                InvalidationDescription = "EMA20 crosses below EMA50 on 5m bars",
                Description = $"TREND_FOLLOW BUY: pullback to EMA20_5m at {price:F2}, stop={stopLevel:F2}, target={targetLevel:F2}",
            };
        }

        // ── SHORT: EMA20_5m < EMA50_5m, price rallies up to EMA20 zone ──
        bool bearishTrend = ind.Ema20_5m < ind.Ema50_5m && ind.TrendDirection_5m < 0;
        decimal distToEma20Short = ind.Ema20_5m - price;
        bool nearEma20Short = distToEma20Short >= -ind.Atr14_5m * DayTradeConfig.TrendPullbackMaxAtr
                           && distToEma20Short <= ind.Atr14_5m * 0.5m;
        bool rsiOkShort = ind.Rsi14_5m >= 30 && ind.Rsi14_5m <= 60;

        if (bearishTrend && nearEma20Short && rsiOkShort)
        {
            decimal atr = ind.Atr14_5m;
            decimal stopLevel = Math.Max(ind.Ema50_5m, price + 1.5m * atr);
            decimal stopDistanceShort = stopLevel - price;
            decimal targetLevel = price - Math.Max(2.5m * atr, stopDistanceShort * DayTradeConfig.MinRiskReward);

            decimal pullbackScore = Math.Max(0, 1.0m - Math.Abs(distToEma20Short) / ind.Atr14_5m / DayTradeConfig.TrendPullbackMaxAtr);
            decimal trendScore = ind.TrendDirection_15m < 0 ? 0.3m : 0;
            decimal volumeScore = ind.VolumeRatio_5m > 1.2m ? 0.2m : 0;
            decimal strength = Math.Clamp(0.40m + pullbackScore * 0.30m + trendScore + volumeScore, 0, 1);

            return new SetupResult
            {
                Type = SetupType.TrendFollow,
                Direction = SignalType.Sell,
                Strength = strength,
                EntryZoneLow = price - atr * 0.1m,
                EntryZoneHigh = price + atr * 0.1m,
                StopLevel = Math.Round(stopLevel, 4),
                TargetLevel = Math.Round(targetLevel, 4),
                DetectionPrice = price,
                Atr = atr,
                ExpiresAt = DateTime.UtcNow.AddMinutes(DayTradeConfig.SetupExpiryMinutes),
                InvalidationDescription = "EMA20 crosses above EMA50 on 5m bars",
                Description = $"TREND_FOLLOW SELL: rally to EMA20_5m at {price:F2}, stop={stopLevel:F2}, target={targetLevel:F2}",
            };
        }

        return null;
    }

    private SetupResult? DetectVwapBounce(BarIndicators ind, decimal price)
    {
        if (ind.Vwap <= 0 || ind.Atr14_5m <= 0) return null;

        decimal vwapDistPct = Math.Abs(price - ind.Vwap) / ind.Vwap;
        bool nearVwap = vwapDistPct <= DayTradeConfig.VwapBounceMaxDistancePct;
        bool volumeOk = ind.VolumeRatio >= DayTradeConfig.VwapBounceMinVolumeRatio;
        bool rsiNeutral = ind.Rsi14_5m >= 40 && ind.Rsi14_5m <= 60;

        if (!nearVwap || !volumeOk || !rsiNeutral) return null;

        decimal atr = ind.Atr14_5m;

        // LONG bounce: price above VWAP (just bounced up from it)
        if (price >= ind.Vwap && ind.TrendDirection >= 0)
        {
            decimal stopLevel = ind.Vwap - ind.Vwap * 0.003m; // 0.3% below VWAP
            decimal targetLevel = price + 2.0m * Math.Abs(price - ind.Vwap + atr * 0.5m);
            targetLevel = Math.Max(targetLevel, price + 2.0m * atr); // Minimum 2×ATR target

            decimal strength = 0.45m;
            if (ind.TrendDirection_5m > 0) strength += 0.15m;
            if (ind.VolumeRatio > 2.0m) strength += 0.10m;
            strength = Math.Clamp(strength, 0, 1);

            return new SetupResult
            {
                Type = SetupType.VwapBounce,
                Direction = SignalType.Buy,
                Strength = strength,
                EntryZoneLow = ind.Vwap,
                EntryZoneHigh = ind.Vwap + atr * 0.2m,
                StopLevel = Math.Round(stopLevel, 4),
                TargetLevel = Math.Round(targetLevel, 4),
                DetectionPrice = price,
                Atr = atr,
                ExpiresAt = DateTime.UtcNow.AddMinutes(DayTradeConfig.SetupExpiryMinutes),
                InvalidationDescription = "Price closes below VWAP on 5m bar",
                Description = $"VWAP_BOUNCE BUY: bounce from VWAP={ind.Vwap:F2} at {price:F2}",
            };
        }

        // SHORT bounce: price below VWAP (rejected down from it)
        if (price <= ind.Vwap && ind.TrendDirection <= 0)
        {
            decimal stopLevel = ind.Vwap + ind.Vwap * 0.003m;
            decimal targetLevel = price - 2.0m * Math.Abs(ind.Vwap - price + atr * 0.5m);
            targetLevel = Math.Min(targetLevel, price - 2.0m * atr);

            decimal strength = 0.45m;
            if (ind.TrendDirection_5m < 0) strength += 0.15m;
            if (ind.VolumeRatio > 2.0m) strength += 0.10m;
            strength = Math.Clamp(strength, 0, 1);

            return new SetupResult
            {
                Type = SetupType.VwapBounce,
                Direction = SignalType.Sell,
                Strength = strength,
                EntryZoneLow = ind.Vwap - atr * 0.2m,
                EntryZoneHigh = ind.Vwap,
                StopLevel = Math.Round(stopLevel, 4),
                TargetLevel = Math.Round(targetLevel, 4),
                DetectionPrice = price,
                Atr = atr,
                ExpiresAt = DateTime.UtcNow.AddMinutes(DayTradeConfig.SetupExpiryMinutes),
                InvalidationDescription = "Price closes above VWAP on 5m bar",
                Description = $"VWAP_BOUNCE SELL: rejection from VWAP={ind.Vwap:F2} at {price:F2}",
            };
        }

        return null;
    }

    private SetupResult? DetectBreakout(BarIndicators ind, decimal price)
    {
        if (ind.Atr14_5m <= 0) return null;

        // Breakout detection uses volume surge as primary signal.
        // Without explicit consolidation range tracking (needs bar history, added in Phase 5),
        // we approximate: high volume + price above/below EMA20_5m + trend alignment.
        bool volumeSurge = ind.VolumeRatio_5m >= DayTradeConfig.BreakoutMinVolumeRatio;
        if (!volumeSurge) return null;

        decimal atr = ind.Atr14_5m;

        // LONG breakout: price well above EMA20_5m with volume surge
        bool aboveEma20 = ind.Ema20_5m > 0 && price > ind.Ema20_5m + atr * 0.3m;
        bool trendConfirm = ind.TrendDirection_5m > 0;

        if (aboveEma20 && trendConfirm)
        {
            decimal rangeMid = ind.Ema20_5m; // Approximate consolidation mid
            decimal stopLevel = rangeMid;
            decimal stopDist = price - stopLevel;
            decimal rangeHeight = price - rangeMid;
            decimal targetLevel = price + Math.Max(rangeHeight, stopDist * DayTradeConfig.MinRiskReward);

            decimal strength = 0.50m;
            if (ind.VolumeRatio_5m > 3.0m) strength += 0.15m;
            if (ind.TrendDirection_15m > 0) strength += 0.10m;
            strength = Math.Clamp(strength, 0, 1);

            return new SetupResult
            {
                Type = SetupType.Breakout,
                Direction = SignalType.Buy,
                Strength = strength,
                EntryZoneLow = price - atr * 0.1m,
                EntryZoneHigh = price + atr * 0.2m,
                StopLevel = Math.Round(stopLevel, 4),
                TargetLevel = Math.Round(targetLevel, 4),
                DetectionPrice = price,
                Atr = atr,
                ExpiresAt = DateTime.UtcNow.AddMinutes(DayTradeConfig.SetupExpiryMinutes),
                InvalidationDescription = "Price closes back inside consolidation range (below EMA20_5m)",
                Description = $"BREAKOUT BUY: volume surge {ind.VolumeRatio_5m:F1}x at {price:F2}, range mid={rangeMid:F2}",
            };
        }

        // SHORT breakout
        bool belowEma20 = ind.Ema20_5m > 0 && price < ind.Ema20_5m - atr * 0.3m;
        bool bearTrend = ind.TrendDirection_5m < 0;

        if (belowEma20 && bearTrend)
        {
            decimal rangeMid = ind.Ema20_5m;
            decimal stopLevel = rangeMid;
            decimal stopDistShort = stopLevel - price;
            decimal rangeHeight = rangeMid - price;
            decimal targetLevel = price - Math.Max(rangeHeight, stopDistShort * DayTradeConfig.MinRiskReward);

            decimal strength = 0.50m;
            if (ind.VolumeRatio_5m > 3.0m) strength += 0.15m;
            if (ind.TrendDirection_15m < 0) strength += 0.10m;
            strength = Math.Clamp(strength, 0, 1);

            return new SetupResult
            {
                Type = SetupType.Breakout,
                Direction = SignalType.Sell,
                Strength = strength,
                EntryZoneLow = price - atr * 0.2m,
                EntryZoneHigh = price + atr * 0.1m,
                StopLevel = Math.Round(stopLevel, 4),
                TargetLevel = Math.Round(targetLevel, 4),
                DetectionPrice = price,
                Atr = atr,
                ExpiresAt = DateTime.UtcNow.AddMinutes(DayTradeConfig.SetupExpiryMinutes),
                InvalidationDescription = "Price closes back inside consolidation range (above EMA20_5m)",
                Description = $"BREAKOUT SELL: volume surge {ind.VolumeRatio_5m:F1}x at {price:F2}",
            };
        }

        return null;
    }

    private SetupResult? DetectReversal(BarIndicators ind, decimal price)
    {
        if (ind.Atr14_5m <= 0 || ind.Rsi14_5m <= 0) return null;

        decimal atr = ind.Atr14_5m;

        // LONG reversal: RSI oversold + volume (potential bottom)
        if (ind.Rsi14_5m <= DayTradeConfig.ReversalRsiOversold + 10 // RSI < 30 zone
            && ind.Rsi14_5m > DayTradeConfig.ReversalRsiOversold     // But not extremely oversold (that's invalidation)
            && ind.VolumeRatio >= 1.2m)
        {
            decimal stopLevel = price - 1.5m * atr; // Below recent swing low
            decimal stopDist = price - stopLevel;
            decimal targetLevel = ind.Ema20_5m > 0 && ind.Ema20_5m > price ? ind.Ema20_5m : price + 2.0m * atr;
            // Ensure target meets minimum R:R
            decimal minTarget = price + stopDist * DayTradeConfig.MinRiskReward;
            if (targetLevel < minTarget) targetLevel = minTarget;

            // Reversals are inherently lower confidence
            decimal strength = 0.35m;
            if (ind.VolumeRatio > 1.5m) strength += 0.10m;
            if (ind.TrendDirection_15m >= 0) strength += 0.10m; // Higher TF not bearish = good
            strength = Math.Clamp(strength, 0, 1);

            return new SetupResult
            {
                Type = SetupType.Reversal,
                Direction = SignalType.Buy,
                Strength = strength,
                EntryZoneLow = price - atr * 0.1m,
                EntryZoneHigh = price + atr * 0.2m,
                StopLevel = Math.Round(stopLevel, 4),
                TargetLevel = Math.Round(targetLevel, 4),
                DetectionPrice = price,
                Atr = atr,
                ExpiresAt = DateTime.UtcNow.AddMinutes(DayTradeConfig.SetupExpiryMinutes),
                InvalidationDescription = "Price makes new low below divergence low (RSI < 20)",
                Description = $"REVERSAL BUY: RSI oversold={ind.Rsi14_5m:F1} at {price:F2}, target EMA20_5m={ind.Ema20_5m:F2}",
            };
        }

        // SHORT reversal: RSI overbought
        if (ind.Rsi14_5m >= DayTradeConfig.ReversalRsiOverbought - 10
            && ind.Rsi14_5m < DayTradeConfig.ReversalRsiOverbought
            && ind.VolumeRatio >= 1.2m)
        {
            decimal stopLevel = price + 1.5m * atr;
            decimal stopDistShort = stopLevel - price;
            decimal targetLevel = ind.Ema20_5m > 0 && ind.Ema20_5m < price ? ind.Ema20_5m : price - 2.0m * atr;
            decimal minTargetShort = price - stopDistShort * DayTradeConfig.MinRiskReward;
            if (targetLevel > minTargetShort) targetLevel = minTargetShort;

            decimal strength = 0.35m;
            if (ind.VolumeRatio > 1.5m) strength += 0.10m;
            if (ind.TrendDirection_15m <= 0) strength += 0.10m;
            strength = Math.Clamp(strength, 0, 1);

            return new SetupResult
            {
                Type = SetupType.Reversal,
                Direction = SignalType.Sell,
                Strength = strength,
                EntryZoneLow = price - atr * 0.2m,
                EntryZoneHigh = price + atr * 0.1m,
                StopLevel = Math.Round(stopLevel, 4),
                TargetLevel = Math.Round(targetLevel, 4),
                DetectionPrice = price,
                Atr = atr,
                ExpiresAt = DateTime.UtcNow.AddMinutes(DayTradeConfig.SetupExpiryMinutes),
                InvalidationDescription = "Price makes new high (RSI > 80)",
                Description = $"REVERSAL SELL: RSI overbought={ind.Rsi14_5m:F1} at {price:F2}",
            };
        }

        return null;
    }
}
