using System;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TradingPilot.Trading;
using Xunit;

namespace TradingPilot.Trading;

public class SetupDetectorTests
{
    private readonly SetupDetector _detector = new(NullLogger<SetupDetector>.Instance);

    private static BarIndicators MakeBullishTrend5m(decimal price) => new()
    {
        Ema9 = price - 0.2m, Ema20 = price - 0.5m, Vwap = price - 1m,
        Rsi14 = 55, Atr14 = 1.5m, Atr14Pct = 0.01m, VolumeRatio = 1.3m,
        TrendDirection = 1, AboveVwap = true,
        // 5m: bullish trend, price near EMA20
        Ema20_5m = price - 0.3m, Ema50_5m = price - 2.0m,
        Rsi14_5m = 55, Atr14_5m = 2.0m, VolumeRatio_5m = 1.3m,
        TrendDirection_5m = 1, AboveVwap_5m = true,
        // 15m: aligned
        Ema20_15m = price - 1.0m, Ema50_15m = price - 3.0m,
        Rsi14_15m = 55, TrendDirection_15m = 1,
        ComputedAt = DateTime.UtcNow, LastRefreshTime = DateTime.UtcNow,
    };

    // ── TREND_FOLLOW ──

    [Fact]
    public void DetectSetups_TrendFollow_BullishPullback_Detected()
    {
        decimal price = 130m;
        var ind = MakeBullishTrend5m(price);

        var setups = _detector.DetectSetups(1, ind, price);

        setups.ShouldContain(s => s.Type == SetupType.TrendFollow);
        var setup = setups.First(s => s.Type == SetupType.TrendFollow);
        setup.Direction.ShouldBe(SignalType.Buy);
        setup.Strength.ShouldBeGreaterThanOrEqualTo(DayTradeConfig.MinSetupStrength);
        setup.StopLevel.ShouldBeLessThan(price);
        setup.TargetLevel.ShouldBeGreaterThan(price);
        setup.RiskReward.ShouldBeGreaterThanOrEqualTo(DayTradeConfig.MinRiskReward);
    }

    [Fact]
    public void DetectSetups_TrendFollow_NoTrend_NotDetected()
    {
        decimal price = 130m;
        var ind = MakeBullishTrend5m(price);
        ind.Ema20_5m = price + 2.0m; // EMA20 above price = not pulling back
        ind.TrendDirection_5m = 0;     // No trend

        var setups = _detector.DetectSetups(1, ind, price);

        setups.ShouldNotContain(s => s.Type == SetupType.TrendFollow);
    }

    [Fact]
    public void DetectSetups_TrendFollow_BearishPullback_Detected()
    {
        decimal price = 130m;
        var ind = new BarIndicators
        {
            Ema9 = price + 0.2m, Ema20 = price + 0.5m, Vwap = price + 1m,
            Rsi14 = 45, Atr14 = 1.5m, VolumeRatio = 1.3m,
            TrendDirection = -1, AboveVwap = false,
            Ema20_5m = price + 0.3m, Ema50_5m = price + 2.0m,
            Rsi14_5m = 45, Atr14_5m = 2.0m, VolumeRatio_5m = 1.3m,
            TrendDirection_5m = -1,
            Ema20_15m = price + 1.0m, Ema50_15m = price + 3.0m,
            Rsi14_15m = 45, TrendDirection_15m = -1,
            ComputedAt = DateTime.UtcNow, LastRefreshTime = DateTime.UtcNow,
        };

        var setups = _detector.DetectSetups(1, ind, price);

        setups.ShouldContain(s => s.Type == SetupType.TrendFollow && s.Direction == SignalType.Sell);
    }

    // ── VWAP_BOUNCE ──

    [Fact]
    public void DetectSetups_VwapBounce_NearVwapWithVolume_Detected()
    {
        decimal price = 130m;
        var ind = new BarIndicators
        {
            Ema9 = price, Ema20 = price - 0.5m, Vwap = price - 0.2m, // Price just above VWAP
            Rsi14 = 50, Rsi14_5m = 50, Atr14 = 1.0m, Atr14_5m = 1.5m,
            VolumeRatio = 1.8m, VolumeRatio_5m = 1.0m, // Volume > 1.5x
            TrendDirection = 0, TrendDirection_5m = 1, AboveVwap = true,
            Ema20_5m = price - 1m, Ema50_5m = price - 2m,
            ComputedAt = DateTime.UtcNow, LastRefreshTime = DateTime.UtcNow,
        };

        var setups = _detector.DetectSetups(1, ind, price);

        setups.ShouldContain(s => s.Type == SetupType.VwapBounce);
        var setup = setups.First(s => s.Type == SetupType.VwapBounce);
        setup.Direction.ShouldBe(SignalType.Buy);
        setup.StopLevel.ShouldBeLessThan(ind.Vwap); // Stop below VWAP
    }

    [Fact]
    public void DetectSetups_VwapBounce_FarFromVwap_NotDetected()
    {
        decimal price = 130m;
        var ind = new BarIndicators
        {
            Vwap = 125m, // 3.8% away — too far
            Rsi14_5m = 50, Atr14_5m = 1.5m, VolumeRatio = 2.0m,
            ComputedAt = DateTime.UtcNow, LastRefreshTime = DateTime.UtcNow,
        };

        var setups = _detector.DetectSetups(1, ind, price);

        setups.ShouldNotContain(s => s.Type == SetupType.VwapBounce);
    }

    // ── BREAKOUT ──

    [Fact]
    public void DetectSetups_Breakout_VolumeSurge_Detected()
    {
        decimal price = 135m;
        var ind = new BarIndicators
        {
            Ema20_5m = 132m, Ema50_5m = 130m, // Price well above EMA20
            Atr14_5m = 2.0m,
            VolumeRatio_5m = 2.5m, // > 2× volume surge
            TrendDirection_5m = 1, TrendDirection_15m = 1,
            Rsi14_5m = 60,
            ComputedAt = DateTime.UtcNow, LastRefreshTime = DateTime.UtcNow,
        };

        var setups = _detector.DetectSetups(1, ind, price);

        setups.ShouldContain(s => s.Type == SetupType.Breakout);
        var setup = setups.First(s => s.Type == SetupType.Breakout);
        setup.Direction.ShouldBe(SignalType.Buy);
        setup.StopLevel.ShouldBeLessThan(price);
    }

    [Fact]
    public void DetectSetups_Breakout_LowVolume_NotDetected()
    {
        decimal price = 135m;
        var ind = new BarIndicators
        {
            Ema20_5m = 132m, Ema50_5m = 130m,
            Atr14_5m = 2.0m,
            VolumeRatio_5m = 1.2m, // Below 2× threshold
            TrendDirection_5m = 1,
            ComputedAt = DateTime.UtcNow, LastRefreshTime = DateTime.UtcNow,
        };

        var setups = _detector.DetectSetups(1, ind, price);

        setups.ShouldNotContain(s => s.Type == SetupType.Breakout);
    }

    // ── REVERSAL ──

    [Fact]
    public void DetectSetups_Reversal_OversoldWithVolume_Detected()
    {
        decimal price = 120m;
        var ind = new BarIndicators
        {
            Rsi14_5m = 25m, // Between 20-30 (oversold zone but not extreme)
            Atr14_5m = 2.0m,
            VolumeRatio = 1.5m, VolumeRatio_5m = 1.0m,
            Ema20_5m = 125m, Ema50_5m = 127m, // EMA20 is above price → reversal target
            TrendDirection_15m = 0,
            ComputedAt = DateTime.UtcNow, LastRefreshTime = DateTime.UtcNow,
        };

        var setups = _detector.DetectSetups(1, ind, price);

        setups.ShouldContain(s => s.Type == SetupType.Reversal);
        var setup = setups.First(s => s.Type == SetupType.Reversal);
        setup.Direction.ShouldBe(SignalType.Buy);
        setup.TargetLevel.ShouldBeGreaterThan(price); // Target is EMA20_5m
    }

    [Fact]
    public void DetectSetups_Reversal_NeutralRsi_NotDetected()
    {
        decimal price = 120m;
        var ind = new BarIndicators
        {
            Rsi14_5m = 50m, // Not oversold
            Atr14_5m = 2.0m, VolumeRatio = 1.5m,
            ComputedAt = DateTime.UtcNow, LastRefreshTime = DateTime.UtcNow,
        };

        var setups = _detector.DetectSetups(1, ind, price);

        setups.ShouldNotContain(s => s.Type == SetupType.Reversal);
    }

    // ── SETUP INVALIDATION ──

    [Fact]
    public void IsSetupInvalidated_TrendFollow_TrendReversed_True()
    {
        var pos = new PositionState { Shares = 100, EntrySetupType = SetupType.TrendFollow };
        var ind = new BarIndicators
        {
            Ema20_5m = 128m, Ema50_5m = 130m, // EMA20 below EMA50 → trend broken
        };

        _detector.IsSetupInvalidated(SetupType.TrendFollow, ind, pos).ShouldBeTrue();
    }

    [Fact]
    public void IsSetupInvalidated_TrendFollow_TrendIntact_False()
    {
        var pos = new PositionState { Shares = 100, EntrySetupType = SetupType.TrendFollow };
        var ind = new BarIndicators
        {
            Ema20_5m = 132m, Ema50_5m = 130m, // EMA20 still above EMA50
        };

        _detector.IsSetupInvalidated(SetupType.TrendFollow, ind, pos).ShouldBeFalse();
    }

    [Fact]
    public void IsSetupInvalidated_VwapBounce_PriceBelowVwap_True()
    {
        var pos = new PositionState { Shares = 100, EntrySetupType = SetupType.VwapBounce };
        var ind = new BarIndicators { AboveVwap_5m = false }; // Price fell below VWAP

        _detector.IsSetupInvalidated(SetupType.VwapBounce, ind, pos).ShouldBeTrue();
    }

    // ── FILTER: Min strength and risk/reward ──

    [Fact]
    public void DetectSetups_FiltersByMinStrengthAndRiskReward()
    {
        // All setups must have Strength >= MinSetupStrength and R:R >= MinRiskReward
        decimal price = 130m;
        var ind = MakeBullishTrend5m(price);

        var setups = _detector.DetectSetups(1, ind, price);

        foreach (var setup in setups)
        {
            setup.Strength.ShouldBeGreaterThanOrEqualTo(DayTradeConfig.MinSetupStrength);
            setup.RiskReward.ShouldBeGreaterThanOrEqualTo(DayTradeConfig.MinRiskReward);
        }
    }
}
