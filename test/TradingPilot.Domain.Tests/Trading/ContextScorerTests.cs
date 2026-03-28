using System;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TradingPilot.Trading;
using Xunit;

namespace TradingPilot.Trading;

public class ContextScorerTests
{
    private readonly ContextScorer _scorer = new(NullLogger<ContextScorer>.Instance);

    [Fact]
    public void ScoreContext_AllPositive_ReturnsPositive()
    {
        decimal score = _scorer.ScoreContext(
            newsSentiment: 0.80m,
            catalystType: "EARNINGS",
            capitalFlowScore: 0.60m,
            shortFloat: 0.20m,
            daysToEarnings: null,
            etHour: 10,
            trendDirection15m: 1);

        score.ShouldBeGreaterThan(0);
        score.ShouldBeLessThanOrEqualTo(1.0m);
    }

    [Fact]
    public void ScoreContext_AllNegative_ReturnsNegative()
    {
        decimal score = _scorer.ScoreContext(
            newsSentiment: -0.80m,
            catalystType: null,
            capitalFlowScore: -0.60m,
            shortFloat: null,
            daysToEarnings: null,
            etHour: 10,
            trendDirection15m: -1);

        score.ShouldBeLessThan(0);
        score.ShouldBeGreaterThanOrEqualTo(-1.0m);
    }

    [Fact]
    public void ScoreContext_NoData_ReturnsNeutral()
    {
        // All nulls except required params
        decimal score = _scorer.ScoreContext(
            newsSentiment: null,
            catalystType: null,
            capitalFlowScore: null,
            shortFloat: null,
            daysToEarnings: null,
            etHour: 11,
            trendDirection15m: 0);

        // With no data and neutral trend, should be near zero
        Math.Abs(score).ShouldBeLessThan(0.10m);
    }

    [Fact]
    public void ScoreContext_EarningsProximity_DampensScore()
    {
        decimal withoutEarnings = _scorer.ScoreContext(
            newsSentiment: 0.80m, catalystType: null,
            capitalFlowScore: 0.60m, shortFloat: null,
            daysToEarnings: null, etHour: 10, trendDirection15m: 1);

        decimal withEarnings = _scorer.ScoreContext(
            newsSentiment: 0.80m, catalystType: null,
            capitalFlowScore: 0.60m, shortFloat: null,
            daysToEarnings: 1, etHour: 10, trendDirection15m: 1);

        // Earnings proximity dampens by 50%
        withEarnings.ShouldBeLessThan(withoutEarnings);
        Math.Abs(withEarnings).ShouldBeLessThanOrEqualTo(Math.Abs(withoutEarnings) * 0.6m); // ~50% dampened
    }

    [Fact]
    public void ScoreContext_TimeOfDay_OpenHourDampened()
    {
        decimal primeHour = _scorer.ScoreContext(
            newsSentiment: 0.50m, catalystType: null,
            capitalFlowScore: 0.50m, shortFloat: null,
            daysToEarnings: null, etHour: 10, trendDirection15m: 1);

        decimal openHour = _scorer.ScoreContext(
            newsSentiment: 0.50m, catalystType: null,
            capitalFlowScore: 0.50m, shortFloat: null,
            daysToEarnings: null, etHour: 9, trendDirection15m: 1);

        // Hour 9 (open) should be dampened vs hour 10 (prime)
        Math.Abs(openHour).ShouldBeLessThan(Math.Abs(primeHour));
    }

    [Fact]
    public void ScoreContext_CatalystBoost_AmplifiesScore()
    {
        decimal withoutCatalyst = _scorer.ScoreContext(
            newsSentiment: 0.50m, catalystType: null,
            capitalFlowScore: 0.50m, shortFloat: null,
            daysToEarnings: null, etHour: 10, trendDirection15m: 1);

        decimal withCatalyst = _scorer.ScoreContext(
            newsSentiment: 0.50m, catalystType: "ANALYST",
            capitalFlowScore: 0.50m, shortFloat: null,
            daysToEarnings: null, etHour: 10, trendDirection15m: 1);

        withCatalyst.ShouldBeGreaterThan(withoutCatalyst);
    }

    [Fact]
    public void ScoreContext_ResultClamped()
    {
        // Extreme inputs should not exceed [-1, +1]
        decimal score = _scorer.ScoreContext(
            newsSentiment: 1.0m, catalystType: "EARNINGS",
            capitalFlowScore: 1.0m, shortFloat: 0.50m,
            daysToEarnings: null, etHour: 10, trendDirection15m: 1);

        score.ShouldBeLessThanOrEqualTo(1.0m);
        score.ShouldBeGreaterThanOrEqualTo(-1.0m);
    }
}
