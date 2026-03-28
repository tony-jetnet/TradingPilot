using Microsoft.Extensions.Logging;

namespace TradingPilot.Trading;

/// <summary>
/// Scores the trading context from news, fundamentals, and time-of-day.
/// Returns [-1, +1]. Positive = favorable context for BUY, negative = favorable for SELL.
/// Pure logic — all data passed in, no DB access.
/// </summary>
public class ContextScorer
{
    private readonly ILogger<ContextScorer> _logger;

    public ContextScorer(ILogger<ContextScorer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Compute context score from news, capital flow, fundamentals, time of day, and trend.
    /// All inputs nullable — missing data contributes 0 (neutral).
    /// </summary>
    /// <param name="newsSentiment">Average news sentiment [-1, +1]. Null if no scored articles.</param>
    /// <param name="catalystType">EARNINGS, ANALYST, etc. Null if no catalyst today.</param>
    /// <param name="capitalFlowScore">Net institutional flow [-1, +1]. Null if no data.</param>
    /// <param name="shortFloat">Short float as decimal (e.g., 0.15 = 15%). Null if unknown.</param>
    /// <param name="daysToEarnings">Days until next earnings. Null if unknown.</param>
    /// <param name="etHour">Current ET hour (9-16).</param>
    /// <param name="trendDirection15m">+1 bullish, -1 bearish, 0 neutral on 15m timeframe.</param>
    public decimal ScoreContext(
        decimal? newsSentiment,
        string? catalystType,
        decimal? capitalFlowScore,
        decimal? shortFloat,
        int? daysToEarnings,
        int etHour,
        int trendDirection15m)
    {
        decimal score = 0;
        decimal totalWeight = 0;

        // ── Capital flow direction (weight 0.25) ──
        if (capitalFlowScore.HasValue)
        {
            score += capitalFlowScore.Value * DayTradeConfig.ContextWeightCapitalFlow;
            totalWeight += DayTradeConfig.ContextWeightCapitalFlow;
        }

        // ── News sentiment (weight 0.30) ──
        if (newsSentiment.HasValue)
        {
            score += newsSentiment.Value * DayTradeConfig.ContextWeightNewsSentiment;
            totalWeight += DayTradeConfig.ContextWeightNewsSentiment;
        }

        // ── Short float pressure (weight 0.15) ──
        // High short float adds pressure in both directions (squeeze potential for longs,
        // but also bearish sentiment). We score it as directional: positive when aligned with trend.
        if (shortFloat.HasValue && shortFloat.Value > 0.10m) // Only meaningful above 10%
        {
            decimal shortPressure = Math.Clamp(shortFloat.Value * 2m, 0, 0.30m); // Max +0.30
            // Short squeeze potential: high short + bullish trend = positive
            score += shortPressure * trendDirection15m * DayTradeConfig.ContextWeightShortFloat;
            totalWeight += DayTradeConfig.ContextWeightShortFloat;
        }

        // ── Higher-timeframe trend alignment (weight 0.15) ──
        score += trendDirection15m * DayTradeConfig.ContextWeightTrendAlignment;
        totalWeight += DayTradeConfig.ContextWeightTrendAlignment;

        // Normalize by actual weight used (missing data → neutral, doesn't bias)
        if (totalWeight > 0)
            score /= totalWeight;

        // ── Time of day factor (multiplier, not weighted) ──
        decimal timeFactor = GetTimeOfDayFactor(etHour);
        score *= timeFactor;

        // ── Catalyst boost (flat add, not weighted) ──
        if (!string.IsNullOrEmpty(catalystType))
        {
            // Catalyst adds directional pressure (positive for news-driven moves)
            // Direction comes from the news sentiment or trend
            decimal catalystBoost = 0.15m;
            if (catalystType is "EARNINGS")
                catalystBoost = 0.20m; // Earnings are strongest catalyst
            score += Math.Sign(score) * catalystBoost; // Amplify existing direction
        }

        // ── Earnings proximity penalty (flat subtract) ──
        if (daysToEarnings.HasValue && daysToEarnings.Value <= DayTradeConfig.EarningsProximityDays)
        {
            // Dampen score by 50% when earnings are imminent
            score *= 0.50m;
        }

        return Math.Clamp(score, -1m, 1m);
    }

    /// <summary>
    /// Time-of-day scaling factor for context score.
    /// Avoids open volatility and close illiquidity.
    /// </summary>
    private static decimal GetTimeOfDayFactor(int etHour)
    {
        return etHour switch
        {
            9 => 0.7m,   // First 30 min: high volatility, reduced signal quality
            10 => 1.0m,  // Prime trading hours
            11 => 1.0m,
            12 => 0.9m,  // Lunch — slightly reduced
            13 => 1.0m,
            14 => 1.0m,
            15 => 0.8m,  // Last hour — approaching close, reduced liquidity
            _ => 0.5m,   // Outside market hours
        };
    }
}
