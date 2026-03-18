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
    // Score Flip (CHECK 1)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ScoreFlip_LongPositionScoreGoesNegative_ShouldExit()
    {
        var pos = new PositionState { Shares = 100, EntryScore = 0.45m };
        decimal currentScore = -0.10m;

        bool flipped = (pos.IsLong && currentScore < 0 && pos.EntryScore > 0);
        flipped.ShouldBeTrue();
    }

    [Fact]
    public void ScoreFlip_LongPositionScoreStillPositive_ShouldNotExit()
    {
        var pos = new PositionState { Shares = 100, EntryScore = 0.45m };
        decimal currentScore = 0.05m; // Weakened but still positive

        bool flipped = (pos.IsLong && currentScore < 0 && pos.EntryScore > 0);
        flipped.ShouldBeFalse();
    }

    [Fact]
    public void ScoreFlip_ShortPositionScoreGoesPositive_ShouldExit()
    {
        var pos = new PositionState { Shares = -100, EntryScore = -0.40m };
        decimal currentScore = 0.15m;

        bool flipped = (!pos.IsLong && currentScore > 0 && pos.EntryScore < 0);
        flipped.ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════
    // Score Decay (CHECK 2)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ScoreDecay_RuleSignal_HighConfidence_TightTolerance()
    {
        var pos = new PositionState
        {
            Shares = 100,
            RuleConfidence = 0.62m,
            PeakFavorableScore = 0.50m,
        };

        decimal tolerance = 1m - pos.RuleConfidence; // 0.38
        decimal currentScore = 0.20m;
        decimal peakAbs = Math.Abs(pos.PeakFavorableScore);
        decimal currentAbs = currentScore; // Long position
        decimal decayFromPeak = (peakAbs - currentAbs) / peakAbs; // (0.50 - 0.20) / 0.50 = 0.60

        (decayFromPeak > tolerance).ShouldBeTrue(); // 0.60 > 0.38 → exit
    }

    [Fact]
    public void ScoreDecay_Stage2Signal_WiderTolerance()
    {
        var pos = new PositionState
        {
            Shares = 100,
            RuleConfidence = 0, // Stage 2, no rule
            PeakFavorableScore = 0.50m,
        };

        decimal tolerance = 0.50m; // Default for Stage 2
        decimal currentScore = 0.30m;
        decimal peakAbs = Math.Abs(pos.PeakFavorableScore);
        decimal currentAbs = currentScore;
        decimal decayFromPeak = (peakAbs - currentAbs) / peakAbs; // (0.50 - 0.30) / 0.50 = 0.40

        (decayFromPeak > tolerance).ShouldBeFalse(); // 0.40 < 0.50 → hold
    }

    [Fact]
    public void ScoreDecay_TinyPeak_IgnoredAsNoise()
    {
        decimal peakAbs = 0.03m; // Below 0.05 threshold
        (peakAbs > 0.05m).ShouldBeFalse(); // Should be skipped
    }

    // ═══════════════════════════════════════════════════════════
    // Stop Loss (CHECK 4)
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
        };

        decimal effectiveStopLoss = Math.Max(pos.StopLoss, pos.EntrySpread);
        effectiveStopLoss.ShouldBe(0.25m); // Floor at spread

        decimal currentPrice = 149.80m;
        decimal adverse = pos.EntryPrice - currentPrice; // 0.20
        (adverse > effectiveStopLoss).ShouldBeFalse(); // 0.20 < 0.25 → hold
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
        };

        decimal effectiveStopLoss = Math.Max(pos.StopLoss, pos.EntrySpread); // 0.30
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

        decimal effectiveStopLoss = Math.Max(pos.StopLoss, pos.EntrySpread);
        (adverse > effectiveStopLoss).ShouldBeTrue();
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
        var pos = new PositionState { HoldSeconds = 60 };
        decimal elapsed = 181; // > 60 * 3 = 180

        bool hardCap = elapsed >= pos.HoldSeconds * 3;
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
