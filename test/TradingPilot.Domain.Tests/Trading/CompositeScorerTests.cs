using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TradingPilot.Trading;
using Xunit;

namespace TradingPilot.Trading;

public class CompositeScorerTests
{
    private readonly CompositeScorer _scorer = new(NullLogger<CompositeScorer>.Instance);
    private readonly ScoringWeights _weights = ScoringWeights.Default();

    [Fact]
    public void Score_AllPositive_ReturnsPositive()
    {
        var (score, breakdown) = _scorer.Score(
            setupStrength: 0.70m, setupDirection: 1,
            timingScore: 0.40m, contextScore: 0.30m,
            _weights, indicators: null);

        // 0.70*0.50 + 0.40*0.30 + 0.30*0.20 = 0.35 + 0.12 + 0.06 = 0.53
        score.ShouldBeGreaterThan(0.50m);
        score.ShouldBeLessThanOrEqualTo(1.0m);
        breakdown.ShouldContain("setup=");
        breakdown.ShouldContain("timing=");
        breakdown.ShouldContain("context=");
    }

    [Fact]
    public void Score_WeightedBlend_MatchesFormula()
    {
        var (score, _) = _scorer.Score(
            setupStrength: 0.60m, setupDirection: 1,
            timingScore: 0.50m, contextScore: 0.40m,
            _weights, indicators: null);

        // Expected: 0.60*0.50 + 0.50*0.30 + 0.40*0.20 = 0.30 + 0.15 + 0.08 = 0.53
        // No filters applied (indicators=null)
        score.ShouldBe(0.53m, tolerance: 0.01m);
    }

    [Fact]
    public void Score_SellDirection_ReturnsNegative()
    {
        var (score, _) = _scorer.Score(
            setupStrength: 0.70m, setupDirection: -1,
            timingScore: -0.40m, contextScore: -0.30m,
            _weights, indicators: null);

        score.ShouldBeLessThan(0);
    }

    [Fact]
    public void Score_TrendFilter_ReducesByHalf()
    {
        var ind = new BarIndicators { TrendDirection_15m = -1, Vwap = 0 }; // Bearish 15m trend

        var (unfiltered, _) = _scorer.Score(
            setupStrength: 0.70m, setupDirection: 1,
            timingScore: 0.40m, contextScore: 0.30m,
            _weights, indicators: null);

        var (filtered, _) = _scorer.Score(
            setupStrength: 0.70m, setupDirection: 1,
            timingScore: 0.40m, contextScore: 0.30m,
            _weights, indicators: ind);

        // Buying against bearish 15m trend → × 0.5
        filtered.ShouldBeLessThan(unfiltered);
    }

    [Fact]
    public void Score_FloorProtection_NeverBelowThirtyPercent()
    {
        // All filters stacking: trend against + VWAP against + RSI extreme
        var ind = new BarIndicators
        {
            TrendDirection_15m = -1, // Against buy signal → ×0.5
            Vwap = 200m, AboveVwap = false, // Against buy signal → ×0.7
            Rsi14 = 86, // Overbought → ×0.30
        };

        var (score, _) = _scorer.Score(
            setupStrength: 0.80m, setupDirection: 1,
            timingScore: 0.50m, contextScore: 0.40m,
            _weights, indicators: ind);

        // Raw = 0.80*0.50 + 0.50*0.30 + 0.40*0.20 = 0.63
        // Worst case: 0.63 × 0.5 × 0.7 × 0.30 = 0.066
        // Floor: 0.63 × 0.30 = 0.189
        // Score should be at or above the 30% floor
        decimal rawScore = 0.80m * 0.50m + 0.50m * 0.30m + 0.40m * 0.20m;
        decimal floor = rawScore * DayTradeConfig.FilterFloorPercent;
        score.ShouldBeGreaterThanOrEqualTo(floor - 0.01m); // Small tolerance for rounding
    }

    [Fact]
    public void Score_VolumeBoost_WhenAligned()
    {
        var indNoVolume = new BarIndicators
        {
            TrendDirection_15m = 1,
            HighVolume = false,
        };
        var indHighVolume = new BarIndicators
        {
            TrendDirection_15m = 1,
            HighVolume = true,
        };

        var (scoreNoVol, _) = _scorer.Score(
            0.60m, 1, 0.40m, 0.30m, _weights, indNoVolume);

        var (scoreHighVol, _) = _scorer.Score(
            0.60m, 1, 0.40m, 0.30m, _weights, indHighVolume);

        // High volume + not against trend → × 1.3 boost
        scoreHighVol.ShouldBeGreaterThan(scoreNoVol);
    }

    [Fact]
    public void Score_NoSetup_TimingOnly()
    {
        // setupStrength = 0 → only timing and context contribute
        var (score, _) = _scorer.Score(
            setupStrength: 0, setupDirection: 0,
            timingScore: 0.60m, contextScore: 0.40m,
            _weights, indicators: null);

        // 0*0.50 + 0.60*0.30 + 0.40*0.20 = 0 + 0.18 + 0.08 = 0.26
        score.ShouldBe(0.26m, tolerance: 0.01m);
    }

    [Fact]
    public void Score_ClampedToRange()
    {
        var (score, _) = _scorer.Score(
            setupStrength: 1.0m, setupDirection: 1,
            timingScore: 1.0m, contextScore: 1.0m,
            _weights, indicators: new BarIndicators { HighVolume = true });

        // Even with boost: 1.0 × 1.3 = 1.3 → clamped to 1.0
        score.ShouldBeLessThanOrEqualTo(1.0m);
    }

    [Fact]
    public void ScoringWeights_Default_SumToOne()
    {
        var w = ScoringWeights.Default();
        (w.SetupWeight + w.TimingWeight + w.ContextWeight).ShouldBe(1.0m);
    }

    [Fact]
    public void ScoringWeights_Normalize_FixesInvalidWeights()
    {
        var w = new ScoringWeights { SetupWeight = 2.0m, TimingWeight = 1.0m, ContextWeight = 1.0m };
        w.Normalize();

        (w.SetupWeight + w.TimingWeight + w.ContextWeight).ShouldBe(1.0m, tolerance: 0.001m);
        w.SetupWeight.ShouldBe(0.50m);
        w.TimingWeight.ShouldBe(0.25m);
        w.ContextWeight.ShouldBe(0.25m);
    }

    [Fact]
    public void ScoringWeights_Normalize_NegativeWeights_ResetsToDefaults()
    {
        var w = new ScoringWeights { SetupWeight = -1.0m, TimingWeight = 0.5m, ContextWeight = 0.5m };
        w.Normalize();

        w.SetupWeight.ShouldBe(DayTradeConfig.DefaultSetupWeight);
        w.TimingWeight.ShouldBe(DayTradeConfig.DefaultTimingWeight);
        w.ContextWeight.ShouldBe(DayTradeConfig.DefaultContextWeight);
    }
}
