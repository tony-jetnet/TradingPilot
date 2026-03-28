using System;
using Shouldly;
using TradingPilot.Trading;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;

namespace TradingPilot.Trading;

/// <summary>
/// Phase 5 verification: tests the signal enrichment and composition logic.
/// Tests TradingSignal fields, CompositeScorer integration with setup data,
/// and the orchestration contract (what goes in → what comes out).
/// </summary>
public class SignalOrchestrationTests
{
    [Fact]
    public void TradingSignal_DefaultSource_IsL2Micro()
    {
        var signal = new TradingSignal();
        signal.Source.ShouldBe(SignalSource.L2Micro);
        signal.SignalSetupType.ShouldBe(SetupType.None);
        signal.SetupStrength.ShouldBe(0);
    }

    [Fact]
    public void TradingSignal_WithSetup_CarriesFullContext()
    {
        var setup = new SetupResult
        {
            Type = SetupType.TrendFollow,
            Direction = SignalType.Buy,
            Strength = 0.72m,
            StopLevel = 127m,
            TargetLevel = 136m,
            DetectionPrice = 130m,
            Atr = 2.0m,
            Description = "TREND_FOLLOW BUY",
        };

        var signal = new TradingSignal
        {
            TickerId = 123,
            Ticker = "NVDA",
            Price = 130m,
            Type = SignalType.Buy,
            Source = SignalSource.Composite,
            SignalSetupType = setup.Type,
            SetupStrength = setup.Strength,
            TimingScore = 0.35m,
            ContextScore = 0.20m,
            CompositeScore = 0.52m,
            Setup = setup,
        };

        signal.Source.ShouldBe(SignalSource.Composite);
        signal.SignalSetupType.ShouldBe(SetupType.TrendFollow);
        signal.SetupStrength.ShouldBe(0.72m);
        signal.Setup.ShouldNotBeNull();
        signal.Setup!.StopLevel.ShouldBe(127m);
        signal.Setup.TargetLevel.ShouldBe(136m);
    }

    [Fact]
    public void CompositeScorer_WithSetup_ProducesStrongerSignalThanL2Only()
    {
        var scorer = new CompositeScorer(NullLogger<CompositeScorer>.Instance);
        var weights = ScoringWeights.Default();

        // With setup: strength 0.70 + timing 0.30 + context 0.20
        var (withSetup, _) = scorer.Score(0.70m, 1, 0.30m, 0.20m, weights, null);

        // Without setup: timing only 0.30 + context 0.20
        var (withoutSetup, _) = scorer.Score(0, 0, 0.30m, 0.20m, weights, null);

        withSetup.ShouldBeGreaterThan(withoutSetup);
        withSetup.ShouldBeGreaterThan(DayTradeConfig.MinCompositeScoreEntry);
    }

    [Fact]
    public void ContextScorer_FeedsIntoComposite_AffectsResult()
    {
        var context = new ContextScorer(NullLogger<ContextScorer>.Instance);
        var composite = new CompositeScorer(NullLogger<CompositeScorer>.Instance);
        var weights = ScoringWeights.Default();

        // Positive context
        decimal posCtx = context.ScoreContext(0.80m, "ANALYST", 0.60m, null, null, 10, 1);
        var (posScore, _) = composite.Score(0.60m, 1, 0.30m, posCtx, weights, null);

        // Negative context
        decimal negCtx = context.ScoreContext(-0.80m, null, -0.60m, null, null, 10, -1);
        var (negScore, _) = composite.Score(0.60m, 1, 0.30m, negCtx, weights, null);

        // Positive context should boost the composite, negative should dampen
        posScore.ShouldBeGreaterThan(negScore);
    }

    [Fact]
    public void SetupDetector_Invalidation_IntegratesWithPositionState()
    {
        var detector = new SetupDetector(NullLogger<SetupDetector>.Instance);

        // Position entered on TrendFollow long
        var pos = new PositionState
        {
            Shares = 100,
            EntryPrice = 130m,
            EntrySetupType = SetupType.TrendFollow,
            SetupStopLevel = 127m,
            SetupTargetLevel = 136m,
        };

        // Trend still intact
        var intactInd = new BarIndicators { Ema20_5m = 131m, Ema50_5m = 129m };
        detector.IsSetupInvalidated(SetupType.TrendFollow, intactInd, pos).ShouldBeFalse();

        // Trend broken (EMA20 crossed below EMA50)
        var brokenInd = new BarIndicators { Ema20_5m = 128m, Ema50_5m = 129m };
        detector.IsSetupInvalidated(SetupType.TrendFollow, brokenInd, pos).ShouldBeTrue();
    }

    [Fact]
    public void IndicatorSnapshot_FillFromBarIndicators_PopulatesAllTimeframes()
    {
        var bars = new BarIndicators
        {
            Ema9 = 100, Ema20 = 99, Rsi14 = 55, Vwap = 98, Atr14 = 1.5m,
            Ema20_5m = 97, Ema50_5m = 95, Rsi14_5m = 52, Atr14_5m = 2.0m,
            TrendDirection_5m = 1, AboveVwap_5m = true,
            Ema20_15m = 96, Ema50_15m = 94, Rsi14_15m = 50, TrendDirection_15m = 1,
        };

        var snap = new IndicatorSnapshot();
        snap.FillFromBarIndicators(bars);

        // 1m
        snap.Ema9.ShouldBe(100);
        snap.Rsi14.ShouldBe(55);
        // 5m
        snap.Ema20_5m.ShouldBe(97);
        snap.Ema50_5m.ShouldBe(95);
        snap.TrendDirection_5m.ShouldBe(1);
        // 15m
        snap.Ema20_15m.ShouldBe(96);
        snap.TrendDirection_15m.ShouldBe(1);
    }

    [Fact]
    public void ModelConfig_NewWeightFields_DefaultToZero()
    {
        // Existing model configs (from JSON) won't have these fields → default to 0
        // The orchestrator checks > 0 before using them, falls back to DayTradeConfig defaults
        var config = new TickerModelConfig();
        config.WeightSetup.ShouldBe(0);
        config.WeightTiming.ShouldBe(0);
        config.WeightContext.ShouldBe(0);
        config.OptimalHoldSecondsDay.ShouldBe(DayTradeConfig.DefaultHoldSeconds);
    }
}
