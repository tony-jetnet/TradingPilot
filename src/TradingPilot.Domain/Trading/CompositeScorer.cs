using Microsoft.Extensions.Logging;

namespace TradingPilot.Trading;

/// <summary>
/// Blends setup strength + L2 timing score + context score into a single composite score.
/// Applies contextual filters (trend, VWAP, volume, RSI) and floor protection.
/// Formula: composite = setup × 0.50 + timing × 0.30 + context × 0.20
/// </summary>
public class CompositeScorer
{
    private readonly ILogger<CompositeScorer> _logger;

    public CompositeScorer(ILogger<CompositeScorer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Compute composite score from three layers.
    /// </summary>
    /// <param name="setupStrength">Setup quality [0, 1]. 0 if no setup (L2-only fallback).</param>
    /// <param name="setupDirection">BUY (+1) or SELL (-1) from setup. 0 if no setup.</param>
    /// <param name="timingScore">L2 timing score [-1, +1].</param>
    /// <param name="contextScore">News/fundamental context [-1, +1].</param>
    /// <param name="weights">Blend weights (setup, timing, context).</param>
    /// <param name="indicators">Current bar indicators for contextual filters.</param>
    /// <returns>Composite score [-1, +1] with sign indicating direction. Breakdown string for logging.</returns>
    public (decimal Score, string Breakdown) Score(
        decimal setupStrength,
        int setupDirection,
        decimal timingScore,
        decimal contextScore,
        ScoringWeights weights,
        BarIndicators? indicators)
    {
        // Setup is directional: strength is always positive, direction is +1/-1
        decimal directionalSetup = setupStrength * setupDirection;

        // Weighted blend
        decimal raw = directionalSetup * weights.SetupWeight
                    + timingScore * weights.TimingWeight
                    + contextScore * weights.ContextWeight;

        // ── Contextual Filters ── (same concept as existing, applied to composite)
        decimal preFilter = Math.Abs(raw);
        decimal filtered = raw;
        bool trendFilterApplied = false;

        if (indicators != null)
        {
            // Trend filter: signal against 15m EMA trend → × 0.5
            if (indicators.TrendDirection_15m != 0)
            {
                bool signalAgainstTrend = (filtered > 0 && indicators.TrendDirection_15m < 0)
                                       || (filtered < 0 && indicators.TrendDirection_15m > 0);
                if (signalAgainstTrend)
                {
                    filtered *= 0.5m;
                    trendFilterApplied = true;
                }
            }

            // VWAP filter: signal against VWAP position → × 0.7
            if (indicators.Vwap > 0)
            {
                bool signalAgainstVwap = (filtered > 0 && !indicators.AboveVwap)
                                      || (filtered < 0 && indicators.AboveVwap);
                if (signalAgainstVwap)
                    filtered *= 0.7m;
            }

            // Volume boost: high volume + aligned → × 1.3
            if (indicators.HighVolume && !trendFilterApplied)
                filtered *= 1.3m;

            // RSI filter: graduated
            if (indicators.Rsi14 > 0)
            {
                if ((filtered > 0 && indicators.Rsi14 > 85) || (filtered < 0 && indicators.Rsi14 < 15))
                    filtered *= 0.30m;
                else if ((filtered > 0 && indicators.Rsi14 > 80) || (filtered < 0 && indicators.Rsi14 < 20))
                    filtered *= 0.50m;
                else if ((filtered > 0 && indicators.Rsi14 > 75) || (filtered < 0 && indicators.Rsi14 < 25))
                    filtered *= 0.70m;
            }
        }

        // Floor protection: no filter chain can reduce score below 30% of pre-filter value
        if (preFilter > 0)
        {
            decimal floor = preFilter * DayTradeConfig.FilterFloorPercent;
            if (Math.Abs(filtered) < floor)
                filtered = Math.Sign(filtered) * floor;
        }

        filtered = Math.Clamp(filtered, -1m, 1m);

        string breakdown = $"setup={directionalSetup:F3}×{weights.SetupWeight:F2} " +
                           $"+ timing={timingScore:F3}×{weights.TimingWeight:F2} " +
                           $"+ context={contextScore:F3}×{weights.ContextWeight:F2} " +
                           $"= raw={raw:F3} → filtered={filtered:F3}";

        return (filtered, breakdown);
    }
}
