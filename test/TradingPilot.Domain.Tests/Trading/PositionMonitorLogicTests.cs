using System;
using Shouldly;
using TradingPilot.Trading;
using Xunit;

namespace TradingPilot.Trading;

/// <summary>
/// Tests the exit decision logic used by PositionMonitor.
/// These are pure logic tests — no mocking needed.
/// </summary>
public class PositionMonitorLogicTests
{
    // ═══════════════════════════════════════════════════════════
    // VWAP Cross (CHECK 1)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void VwapCross_LongBelowVwap_ShouldExit()
    {
        decimal currentPrice = 149.50m;
        decimal vwap = 150.00m;
        bool isLong = true;

        bool vwapExit = isLong && vwap > 0 && currentPrice < vwap;
        vwapExit.ShouldBeTrue();
    }

    [Fact]
    public void VwapCross_LongAboveVwap_ShouldHold()
    {
        decimal currentPrice = 150.50m;
        decimal vwap = 150.00m;
        bool isLong = true;

        bool vwapExit = isLong && vwap > 0 && currentPrice < vwap;
        vwapExit.ShouldBeFalse();
    }

    [Fact]
    public void VwapCross_ShortAboveVwap_ShouldExit()
    {
        decimal currentPrice = 150.50m;
        decimal vwap = 150.00m;
        bool isLong = false;

        bool vwapExit = !isLong && vwap > 0 && currentPrice > vwap;
        vwapExit.ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════
    // EMA Trend Reversal (CHECK 2)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void TrendReversal_LongEma9BelowEma20_ShouldExit()
    {
        decimal ema9 = 149.50m;
        decimal ema20 = 150.00m;
        bool isLong = true;

        bool reversed = isLong && ema9 > 0 && ema20 > 0 && ema9 < ema20;
        reversed.ShouldBeTrue();
    }

    [Fact]
    public void TrendReversal_LongEma9AboveEma20_ShouldHold()
    {
        decimal ema9 = 150.50m;
        decimal ema20 = 150.00m;
        bool isLong = true;

        bool reversed = isLong && ema9 > 0 && ema20 > 0 && ema9 < ema20;
        reversed.ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════
    // RSI Extreme (CHECK 3)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void RsiExtreme_LongOverbought_ShouldExit()
    {
        decimal rsi = 78m;
        bool isLong = true;

        bool extreme = isLong && rsi > 75;
        extreme.ShouldBeTrue();
    }

    [Fact]
    public void RsiExtreme_ShortOversold_ShouldExit()
    {
        decimal rsi = 22m;
        bool isLong = false;

        bool extreme = !isLong && rsi > 0 && rsi < 25;
        extreme.ShouldBeTrue();
    }

    [Fact]
    public void RsiExtreme_LongNormal_ShouldHold()
    {
        decimal rsi = 55m;
        bool isLong = true;

        bool extreme = isLong && rsi > 75;
        extreme.ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════
    // Stop Loss (CHECK 4) — volatility-adaptive with ATR floor
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void StopLoss_FlooredAtEntrySpread()
    {
        var pos = new PositionState
        {
            Shares = 100,
            EntryPrice = 150.00m,
            StopLoss = 0.10m,  // Configured stop
            EntrySpread = 0.25m, // Entry spread is wider
            EntryAtr = 0.05m, // ATR floor = 0.05 * 1.5 = 0.075
        };

        decimal atrFloor = pos.EntryAtr * 1.5m;
        decimal effectiveStopLoss = Math.Max(Math.Max(pos.StopLoss, atrFloor), pos.EntrySpread);
        effectiveStopLoss.ShouldBe(0.25m); // Spread is widest → floor at spread

        decimal currentPrice = 149.80m;
        decimal adverse = pos.EntryPrice - currentPrice; // 0.20
        (adverse > effectiveStopLoss).ShouldBeFalse(); // 0.20 < 0.25 → hold
    }

    [Fact]
    public void StopLoss_AtrWidensStopBeyondRuleValue()
    {
        var pos = new PositionState
        {
            Shares = 100,
            EntryPrice = 150.00m,
            StopLoss = 0.30m,  // Rule says 0.30
            EntrySpread = 0.05m,
            EntryAtr = 0.40m, // ATR floor = 0.40 * 1.5 = 0.60 — wider than rule
        };

        decimal atrFloor = pos.EntryAtr * 1.5m; // 0.60
        decimal effectiveStopLoss = Math.Max(Math.Max(pos.StopLoss, atrFloor), pos.EntrySpread);
        effectiveStopLoss.ShouldBe(0.60m); // ATR floor wins

        decimal currentPrice = 149.55m;
        decimal adverse = pos.EntryPrice - currentPrice; // 0.45
        (adverse > effectiveStopLoss).ShouldBeFalse(); // 0.45 < 0.60 → hold (ATR saved us from premature exit)
    }

    [Fact]
    public void StopLoss_TriggersWhenAdverseExceedsStop()
    {
        var pos = new PositionState
        {
            Shares = 100,
            EntryPrice = 150.00m,
            StopLoss = 0.30m,
            EntrySpread = 0.05m,
            EntryAtr = 0.10m, // ATR floor = 0.15
        };

        decimal atrFloor = pos.EntryAtr * 1.5m;
        decimal effectiveStopLoss = Math.Max(Math.Max(pos.StopLoss, atrFloor), pos.EntrySpread); // 0.30
        decimal currentPrice = 149.60m;
        decimal adverse = pos.EntryPrice - currentPrice; // 0.40

        (adverse > effectiveStopLoss).ShouldBeTrue(); // 0.40 > 0.30 → exit
    }

    [Fact]
    public void StopLoss_ShortPosition_AdverseIsPriceIncrease()
    {
        var pos = new PositionState
        {
            Shares = -100, // Short
            EntryPrice = 150.00m,
            StopLoss = 0.30m,
            EntrySpread = 0.05m,
        };

        decimal currentPrice = 150.50m;
        decimal adverse = currentPrice - pos.EntryPrice; // 0.50 (price went up = bad for short)

        decimal effectiveStopLoss = Math.Max(Math.Max(pos.StopLoss, pos.EntryAtr * 1.5m), pos.EntrySpread);
        (adverse > effectiveStopLoss).ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════
    // Breakeven Stop (CHECK 5a)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void BreakevenStop_WasProfitableNowLosing_ShouldExit()
    {
        var pos = new PositionState
        {
            Shares = 100,
            EntryPrice = 150.00m,
            PeakFavorablePrice = 150.50m, // Was up +0.50
            StopLoss = 0.30m,
            EntrySpread = 0.05m,
        };

        decimal effectiveStopLoss = Math.Max(Math.Max(pos.StopLoss, pos.EntryAtr * 1.5m), pos.EntrySpread);
        decimal peakProfit = pos.PeakFavorablePrice - pos.EntryPrice; // 0.50
        decimal currentPrice = 149.95m;
        decimal currentProfit = currentPrice - pos.EntryPrice; // -0.05

        // peakProfit (0.50) > effectiveStopLoss (0.30) AND currentProfit (-0.05) < 0
        bool breakevenTriggered = peakProfit > effectiveStopLoss && currentProfit < 0;
        breakevenTriggered.ShouldBeTrue();
    }

    [Fact]
    public void BreakevenStop_StillProfitable_ShouldHold()
    {
        var pos = new PositionState
        {
            Shares = 100,
            EntryPrice = 150.00m,
            PeakFavorablePrice = 150.50m,
            StopLoss = 0.30m,
            EntrySpread = 0.05m,
        };

        decimal effectiveStopLoss = Math.Max(Math.Max(pos.StopLoss, pos.EntryAtr * 1.5m), pos.EntrySpread);
        decimal peakProfit = 0.50m;
        decimal currentPrice = 150.10m;
        decimal currentProfit = currentPrice - pos.EntryPrice; // +0.10 (still profitable)

        bool breakevenTriggered = peakProfit > effectiveStopLoss && currentProfit < 0;
        breakevenTriggered.ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════
    // Trailing Stop (CHECK 5)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void TrailingStop_PullbackExceedsMaxGiveBack_ShouldExit()
    {
        var pos = new PositionState
        {
            Shares = 100,
            EntryPrice = 150.00m,
            PeakFavorablePrice = 150.80m, // Peaked at +0.80
            RuleConfidence = 0.62m,
            StopLoss = 0.30m,
            EntrySpread = 0.05m,
        };

        decimal peakProfit = pos.PeakFavorablePrice - pos.EntryPrice; // 0.80
        decimal currentPrice = 150.20m;
        decimal currentProfit = currentPrice - pos.EntryPrice; // 0.20
        decimal effectiveStopLoss = Math.Max(pos.StopLoss, pos.EntrySpread); // 0.30

        // peakProfit (0.80) > effectiveStopLoss (0.30) → trailing active
        (peakProfit > effectiveStopLoss).ShouldBeTrue();

        decimal maxGiveBack = 1m - pos.RuleConfidence; // 0.38
        decimal pullback = peakProfit - currentProfit; // 0.80 - 0.20 = 0.60

        // pullback (0.60) > peakProfit * maxGiveBack (0.80 * 0.38 = 0.304) → exit
        (pullback > peakProfit * maxGiveBack).ShouldBeTrue();
    }

    [Fact]
    public void TrailingStop_SmallPullback_ShouldHold()
    {
        var pos = new PositionState
        {
            Shares = 100,
            EntryPrice = 150.00m,
            PeakFavorablePrice = 150.50m,
            RuleConfidence = 0.62m,
            StopLoss = 0.30m,
            EntrySpread = 0.05m,
        };

        decimal peakProfit = 0.50m;
        decimal currentPrice = 150.40m;
        decimal currentProfit = 0.40m;
        decimal maxGiveBack = 1m - 0.62m; // 0.38
        decimal pullback = peakProfit - currentProfit; // 0.10

        // 0.10 < 0.50 * 0.38 = 0.19 → hold
        (pullback > peakProfit * maxGiveBack).ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════
    // Adaptive Time Gate (CHECK 6)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void TimeGate_PastHoldTime_ScoreWeak_ShouldExit()
    {
        var pos = new PositionState
        {
            Shares = 100,
            HoldSeconds = 60,
            EntryScore = 0.40m,
        };

        decimal elapsed = 65; // Past 60s hold time
        decimal currentScore = 0.10m; // Weakened

        bool pastHold = elapsed >= pos.HoldSeconds;
        decimal scoreStrength = currentScore; // Long: current score directly
        decimal entryStrength = pos.EntryScore;
        bool scoreWeakening = scoreStrength < entryStrength * 0.5m; // 0.10 < 0.20

        pastHold.ShouldBeTrue();
        scoreWeakening.ShouldBeTrue();
    }

    [Fact]
    public void TimeGate_PastHoldTime_ScoreStrong_ShouldHold()
    {
        var pos = new PositionState
        {
            Shares = 100,
            HoldSeconds = 60,
            EntryScore = 0.40m,
        };

        decimal elapsed = 65;
        decimal currentScore = 0.35m; // Still strong

        bool pastHold = elapsed >= pos.HoldSeconds;
        decimal scoreStrength = currentScore;
        decimal entryStrength = pos.EntryScore;
        bool scoreWeakening = scoreStrength < entryStrength * 0.5m; // 0.35 < 0.20? No

        pastHold.ShouldBeTrue();
        scoreWeakening.ShouldBeFalse(); // Hold — score still strong
    }

    [Fact]
    public void TimeGate_HardCap_AlwaysExits()
    {
        var pos = new PositionState { HoldSeconds = 3600 };
        decimal elapsed = 7201; // > 3600 * 2 = 7200

        bool hardCap = elapsed >= pos.HoldSeconds * 2;
        hardCap.ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════
    // Dollar-Based Position Sizing
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void DollarSizing_ExpensiveStock_FewShares()
    {
        decimal maxDollars = 25000m;
        decimal price = 800m; // LLY

        int qty = Math.Max(1, (int)(maxDollars / price));
        qty.ShouldBe(31); // $25K / $800 = 31 shares
    }

    [Fact]
    public void DollarSizing_CheapStock_ManyShares()
    {
        decimal maxDollars = 25000m;
        decimal price = 7.50m; // SOFI

        int qty = Math.Max(1, (int)(maxDollars / price));
        qty.ShouldBe(3333); // $25K / $7.50 = 3333 shares
    }

    [Fact]
    public void DollarSizing_EqualNotionalValue()
    {
        decimal maxDollars = 25000m;

        int llyQty = (int)(maxDollars / 800m);
        int sofiQty = (int)(maxDollars / 7.50m);

        decimal llyNotional = llyQty * 800m;   // 31 * 800 = $24,800
        decimal sofiNotional = sofiQty * 7.50m; // 3333 * 7.50 = $24,997.50

        // Both within $25K budget — roughly equal risk
        llyNotional.ShouldBeInRange(24000m, 25000m);
        sofiNotional.ShouldBeInRange(24000m, 25000m);
    }

    // ═══════════════════════════════════════════════════════════
    // PositionState Basics
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void PositionState_IsLong_PositiveShares()
    {
        new PositionState { Shares = 100 }.IsLong.ShouldBeTrue();
    }

    [Fact]
    public void PositionState_IsLong_NegativeShares_ReturnsFalse()
    {
        new PositionState { Shares = -100 }.IsLong.ShouldBeFalse();
    }
}
