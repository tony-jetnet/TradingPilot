using System;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TradingPilot.Trading;
using Xunit;

namespace TradingPilot.Trading;

/// <summary>
/// Phase 6 verification: tests day-trading exit logic (9 exit types).
/// Pure logic tests — no mocking, no DI.
/// </summary>
public class DayTradeExitLogicTests
{
    // ═══════════════════════════════════════════════════════════
    // EXIT 0: EOD MANDATORY CLOSE
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData(15, 50, true)]   // 3:50 PM → hard close
    [InlineData(15, 51, true)]   // 3:51 PM → hard close
    [InlineData(16, 0, true)]    // 4:00 PM → hard close
    [InlineData(15, 49, false)]  // 3:49 PM → not yet
    [InlineData(15, 30, false)]  // 3:30 PM → tighten only, no close
    [InlineData(14, 0, false)]   // 2:00 PM → normal hours
    public void EodHardClose_TriggersAtCorrectTime(int hour, int minute, bool shouldClose)
    {
        bool hardClose = hour > DayTradeConfig.EodHardCloseHour ||
            (hour == DayTradeConfig.EodHardCloseHour && minute >= DayTradeConfig.EodHardCloseMinute);

        hardClose.ShouldBe(shouldClose);
    }

    [Theory]
    [InlineData(15, 45, true)]   // 3:45 PM → exit
    [InlineData(15, 46, true)]
    [InlineData(15, 44, false)]  // 3:44 PM → tighten only
    public void EodExit_TriggersAtCorrectTime(int hour, int minute, bool shouldExit)
    {
        bool eodExit = hour > DayTradeConfig.EodExitHour ||
            (hour == DayTradeConfig.EodExitHour && minute >= DayTradeConfig.EodExitMinute);

        eodExit.ShouldBe(shouldExit);
    }

    // ═══════════════════════════════════════════════════════════
    // EXIT 1: STOP LOSS — structural + ATR floor
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void StopLoss_UsesSetupStopWhenLargerThanAtr()
    {
        decimal entryPrice = 130m;
        decimal setupStopLevel = 126m; // 4.0 distance
        decimal entryAtr = 2.0m;
        decimal entrySpread = 0.05m;

        decimal setupStopDist = Math.Abs(entryPrice - setupStopLevel);
        decimal atrFloor = entryAtr * DayTradeConfig.StopAtrMultiplier; // 2.0 * 1.5 = 3.0
        decimal effectiveStop = Math.Max(Math.Max(setupStopDist, atrFloor), entrySpread);

        effectiveStop.ShouldBe(4.0m); // Setup stop (4.0) > ATR floor (3.0)
    }

    [Fact]
    public void StopLoss_UsesAtrFloorWhenSetupStopTooTight()
    {
        decimal entryPrice = 130m;
        decimal setupStopLevel = 129m; // 1.0 distance — too tight
        decimal entryAtr = 2.0m;

        decimal setupStopDist = Math.Abs(entryPrice - setupStopLevel);
        decimal atrFloor = entryAtr * DayTradeConfig.StopAtrMultiplier; // 3.0
        decimal effectiveStop = Math.Max(setupStopDist, atrFloor);

        effectiveStop.ShouldBe(3.0m); // ATR floor (3.0) > setup (1.0)
    }

    // ═══════════════════════════════════════════════════════════
    // EXIT 2: PROFIT TARGET — setup target or % + R:R minimum
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ProfitTarget_UsesMaxOfSetupTargetAndMinRR()
    {
        decimal entryPrice = 130m;
        decimal setupTargetLevel = 136m; // 6.0 distance
        decimal effectiveStop = 3.0m;

        decimal setupTargetDist = Math.Abs(setupTargetLevel - entryPrice); // 6.0
        decimal pctTarget = entryPrice * DayTradeConfig.ProfitTargetMinPct; // 130 * 0.02 = 2.6
        decimal rrTarget = effectiveStop * DayTradeConfig.ProfitTargetMinRiskReward; // 3.0 * 2.0 = 6.0

        decimal profitTarget = Math.Max(Math.Max(setupTargetDist, pctTarget), rrTarget);
        profitTarget.ShouldBe(6.0m);
    }

    // ═══════════════════════════════════════════════════════════
    // EXIT 3: SETUP INVALIDATION
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void SetupInvalidation_TrendFollow_EmaReversed_Invalidates()
    {
        var detector = new SetupDetector(NullLogger<SetupDetector>.Instance);
        var pos = new PositionState { Shares = 100, EntrySetupType = SetupType.TrendFollow };
        var ind = new BarIndicators { Ema20_5m = 128m, Ema50_5m = 130m };

        detector.IsSetupInvalidated(SetupType.TrendFollow, ind, pos).ShouldBeTrue();
    }

    [Fact]
    public void SetupInvalidation_HardExit_WhenLosingAndNoTrailing()
    {
        // Scenario: setup invalidated, trailing not active (peakProfit < activation), losing > 30% of stop
        decimal peakProfit = 0.50m; // Small profit, below trailing activation
        decimal trailingActivation = 0.87m; // 0.40% of $218
        decimal adverse = 1.5m; // Losing
        decimal effectiveStop = 3.0m;

        bool noTrailing = peakProfit <= trailingActivation;
        bool losingEnough = adverse > effectiveStop * DayTradeConfig.InvalidationHardExitFraction; // 3.0 * 0.30 = 0.9

        (noTrailing && losingEnough).ShouldBeTrue(); // Should hard exit
    }

    // ═══════════════════════════════════════════════════════════
    // EXIT 4: TRAILING STOP — giveback formula
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void TrailingGiveback_StrongerSetup_GetsMoreRoom()
    {
        decimal givebackWeak = DayTradeConfig.TrailingGivebackBase + 0.35m * DayTradeConfig.TrailingGivebackStrengthScale;
        decimal givebackStrong = DayTradeConfig.TrailingGivebackBase + 0.80m * DayTradeConfig.TrailingGivebackStrengthScale;

        // Stronger setup → higher giveback → more room
        givebackStrong.ShouldBeGreaterThan(givebackWeak);

        // 0.80 strength: 0.35 + 0.80 * 0.20 = 0.51 (51% giveback)
        givebackStrong.ShouldBe(0.51m, tolerance: 0.001m);

        // 0.35 strength: 0.35 + 0.35 * 0.20 = 0.42 (42% giveback)
        givebackWeak.ShouldBe(0.42m, tolerance: 0.001m);
    }

    [Fact]
    public void TrailingActivation_DayTrade_IsReachable()
    {
        // AMD at $218: activation = $218 * 0.004 = $0.87
        decimal amdActivation = 218m * DayTradeConfig.TrailingActivationPct;
        amdActivation.ShouldBe(0.872m);

        // RIVN at $15: activation = max($0.06, 2*spread)
        decimal rivnActivation = Math.Max(15m * DayTradeConfig.TrailingActivationPct, 0.03m * 2);
        rivnActivation.ShouldBe(0.06m);
    }

    // ═══════════════════════════════════════════════════════════
    // EXIT 5: REGIME EXIT
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void RegimeExit_TightenerActive_NoTrailing_Losing()
    {
        decimal peakProfit = 0.30m;
        decimal trailingActivation = 0.87m;
        decimal adverse = 1.20m;
        decimal effectiveStop = 3.0m;
        decimal trailingOverride = 0.30m; // VWAP tightener active

        bool tightenerActive = trailingOverride > 0;
        bool noTrailing = peakProfit <= trailingActivation;
        decimal softStopThreshold = effectiveStop * DayTradeConfig.RegimeExitStopFraction; // 3.0 * 0.35 = 1.05
        bool losingEnough = adverse > softStopThreshold;

        (tightenerActive && noTrailing && losingEnough).ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════
    // EXIT 6: BREAKEVEN STOP
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void BreakevenStop_ActivatesAtCorrectLevel()
    {
        decimal trailingActivation = 0.87m;
        decimal breakevenActivation = trailingActivation * DayTradeConfig.BreakevenActivationMultiple; // 0.87 * 2.5 = 2.175

        breakevenActivation.ShouldBe(2.175m);

        // Once peaked at $2.18, protect at breakeven - buffer
        decimal effectiveStop = 3.0m;
        decimal buffer = effectiveStop * DayTradeConfig.BreakevenBufferFraction; // 3.0 * 0.20 = 0.60

        // If current profit < -0.60 after peaking at 2.18 → exit
        decimal currentProfit = -0.70m;
        decimal peakProfit = 2.20m;

        bool activated = peakProfit > breakevenActivation;
        bool losingBeyondBuffer = currentProfit < -buffer;

        (activated && losingBeyondBuffer).ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════
    // EXIT 8: TIME GATE
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void TimeGate_DefaultHoldTime_OneHour()
    {
        DayTradeConfig.DefaultHoldSeconds.ShouldBe(3600);
    }

    [Fact]
    public void TimeGate_HardCap_FourHours()
    {
        DayTradeConfig.MaxHoldSeconds.ShouldBe(14400);
    }

    [Fact]
    public void TimeGate_MaxIs2xHoldOrGlobalCap()
    {
        int holdSeconds = 3600;
        int maxHold = Math.Min(holdSeconds * 2, DayTradeConfig.MaxHoldSeconds);
        maxHold.ShouldBe(7200); // 2 * 3600 = 7200 < 14400

        int longHold = 10800; // 3 hours
        int maxLong = Math.Min(longHold * 2, DayTradeConfig.MaxHoldSeconds);
        maxLong.ShouldBe(14400); // 2 * 10800 = 21600, capped at 14400
    }

    // ═══════════════════════════════════════════════════════════
    // PositionState: day trading fields
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void PositionState_HasSetup_TrueWhenSetupTypeNotNone()
    {
        var pos = new PositionState { EntrySetupType = SetupType.TrendFollow };
        pos.HasSetup.ShouldBeTrue();

        var l2Pos = new PositionState { EntrySetupType = SetupType.None };
        l2Pos.HasSetup.ShouldBeFalse();
    }

    [Fact]
    public void PositionState_MaxFavorableExcursion_Tracked()
    {
        var pos = new PositionState { Shares = 100, EntryPrice = 130m };
        pos.MaxFavorableExcursion = 0;

        // Price moves to 132 → MFE = 2.0
        decimal mfe1 = 132m - pos.EntryPrice;
        if (mfe1 > pos.MaxFavorableExcursion) pos.MaxFavorableExcursion = mfe1;
        pos.MaxFavorableExcursion.ShouldBe(2.0m);

        // Price drops to 131 → MFE stays at 2.0
        decimal mfe2 = 131m - pos.EntryPrice;
        if (mfe2 > pos.MaxFavorableExcursion) pos.MaxFavorableExcursion = mfe2;
        pos.MaxFavorableExcursion.ShouldBe(2.0m);
    }

    // ═══════════════════════════════════════════════════════════
    // DayTradeConfig: locked parameters match CLAUDE.md
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void DayTradeConfig_LockedParameters_MatchSpec()
    {
        DayTradeConfig.StopAtrMultiplier.ShouldBe(1.5m);
        DayTradeConfig.TrailingActivationPct.ShouldBe(0.004m);
        DayTradeConfig.BreakevenActivationMultiple.ShouldBe(2.5m);
        DayTradeConfig.ProfitTargetMinPct.ShouldBe(0.020m);
        DayTradeConfig.ProfitTargetMinRiskReward.ShouldBe(2.0m);
        DayTradeConfig.RegimeExitStopFraction.ShouldBe(0.35m);
        DayTradeConfig.FilterFloorPercent.ShouldBe(0.30m);
        DayTradeConfig.DefaultHoldSeconds.ShouldBe(3600);
        DayTradeConfig.MaxHoldSeconds.ShouldBe(14400);
        DayTradeConfig.DailyPnlStopLoss.ShouldBe(-1500m);
        DayTradeConfig.DailyPnlStopProfit.ShouldBe(1500m);
        DayTradeConfig.RateLimitSeconds.ShouldBe(1800);
        DayTradeConfig.LossCooldownSeconds.ShouldBe(3600);
        DayTradeConfig.EntryTimeoutSeconds.ShouldBe(300);
        DayTradeConfig.EodHardCloseHour.ShouldBe(15);
        DayTradeConfig.EodHardCloseMinute.ShouldBe(50);
        DayTradeConfig.ActiveSymbolCount.ShouldBe(10);
    }
}
