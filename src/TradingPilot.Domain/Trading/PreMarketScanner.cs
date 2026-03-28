using Microsoft.Extensions.Logging;

namespace TradingPilot.Trading;

/// <summary>
/// Pure ranking logic for pre-market symbol selection.
/// Scores and ranks watched symbols by day-trading attractiveness.
/// No DB access — all data passed in. Called by PreMarketScannerJob.
/// </summary>
public class PreMarketScanner
{
    private readonly ILogger<PreMarketScanner> _logger;

    public PreMarketScanner(ILogger<PreMarketScanner> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Rank candidates by day-trading attractiveness. Returns top N sorted by score descending.
    /// </summary>
    public List<ScannerResult> Rank(List<ScannerInput> candidates, int topN = DayTradeConfig.ActiveSymbolCount)
    {
        var scored = new List<ScannerResult>();

        foreach (var c in candidates)
        {
            decimal gapScore = ScoreGap(c.GapPercent);
            decimal volumeScore = ScoreVolume(c.PremarketVolumeRatio);
            decimal catalystScore = ScoreCatalyst(c.CatalystType);
            decimal capitalFlowScore = ScoreCapitalFlow(c.CapitalFlowNet);
            decimal setupQualityScore = ScoreSetupQuality(c.SetupQuality);
            decimal atrScore = ScoreAtr(c.AtrPct);

            decimal totalScore =
                gapScore * DayTradeConfig.ScannerWeightGap +
                volumeScore * DayTradeConfig.ScannerWeightVolume +
                catalystScore * DayTradeConfig.ScannerWeightCatalyst +
                capitalFlowScore * DayTradeConfig.ScannerWeightCapitalFlow +
                setupQualityScore * DayTradeConfig.ScannerWeightSetupQuality +
                atrScore * DayTradeConfig.ScannerWeightAtr;

            string reason = $"gap={c.GapPercent:P1}({gapScore:F2}) vol={c.PremarketVolumeRatio:F1}x({volumeScore:F2}) " +
                            $"catalyst={c.CatalystType ?? "none"}({catalystScore:F2}) flow={c.CapitalFlowNet:F2}({capitalFlowScore:F2}) " +
                            $"setup={c.SetupQuality:F2}({setupQualityScore:F2}) atr={c.AtrPct:P2}({atrScore:F2})";

            scored.Add(new ScannerResult
            {
                Symbol = c.Symbol,
                TickerId = c.TickerId,
                Score = Math.Round(totalScore, 4),
                Reason = reason,
                GapPercent = c.GapPercent,
                PremarketVolumeRatio = c.PremarketVolumeRatio,
                CatalystType = c.CatalystType,
                SetupQuality = c.SetupQuality,
                AtrPct = c.AtrPct,
            });
        }

        var ranked = scored
            .OrderByDescending(s => s.Score)
            .Take(topN)
            .ToList();

        for (int i = 0; i < ranked.Count; i++)
        {
            ranked[i].Rank = i + 1;
            _logger.LogInformation("Scanner #{Rank}: {Symbol} score={Score:F3} | {Reason}",
                ranked[i].Rank, ranked[i].Symbol, ranked[i].Score, ranked[i].Reason);
        }

        return ranked;
    }

    /// <summary>Overnight gap: |gap| > 2% = high interest. Normalized to [0, 1].</summary>
    private static decimal ScoreGap(decimal gapPct)
    {
        decimal absGap = Math.Abs(gapPct);
        if (absGap >= 0.05m) return 1.0m;    // 5%+ gap = max score
        if (absGap >= 0.02m) return 0.7m;    // 2-5% gap = high
        if (absGap >= 0.01m) return 0.4m;    // 1-2% gap = moderate
        return 0.1m;                          // < 1% = low
    }

    /// <summary>Pre-market volume vs 20-day average. > 2× = high interest.</summary>
    private static decimal ScoreVolume(decimal volumeRatio)
    {
        if (volumeRatio >= 3.0m) return 1.0m;
        if (volumeRatio >= 2.0m) return 0.7m;
        if (volumeRatio >= 1.5m) return 0.4m;
        return 0.1m;
    }

    /// <summary>News catalyst present today.</summary>
    private static decimal ScoreCatalyst(string? catalystType)
    {
        if (string.IsNullOrEmpty(catalystType)) return 0;
        return catalystType switch
        {
            "EARNINGS" => 1.0m,
            "ANALYST" => 0.7m,
            "REGULATORY" => 0.8m,
            "CORPORATE" => 0.6m,
            "SECTOR" => 0.4m,
            _ => 0.3m,
        };
    }

    /// <summary>Capital flow: strong directional flow = high interest.</summary>
    private static decimal ScoreCapitalFlow(decimal netFlow)
    {
        decimal abs = Math.Abs(netFlow);
        if (abs >= 0.5m) return 1.0m;
        if (abs >= 0.3m) return 0.6m;
        if (abs >= 0.1m) return 0.3m;
        return 0;
    }

    /// <summary>Setup quality from prior day's close analysis [0, 1].</summary>
    private static decimal ScoreSetupQuality(decimal quality) => Math.Clamp(quality, 0, 1m);

    /// <summary>ATR: minimum 0.8% needed. Higher = more opportunity (up to a point).</summary>
    private static decimal ScoreAtr(decimal atrPct)
    {
        if (atrPct < DayTradeConfig.ScannerMinAtrPct) return 0; // Below minimum — not tradeable
        if (atrPct >= 0.03m) return 0.5m;  // Very high ATR = risky, cap score
        if (atrPct >= 0.015m) return 1.0m; // Sweet spot
        return 0.6m;                        // Moderate
    }
}

/// <summary>Input data for scanner ranking. Populated by PreMarketScannerJob from caches + DB.</summary>
public class ScannerInput
{
    public string Symbol { get; set; } = "";
    public long TickerId { get; set; }
    public decimal GapPercent { get; set; }
    public decimal PremarketVolumeRatio { get; set; }
    public string? CatalystType { get; set; }
    public decimal CapitalFlowNet { get; set; }
    public decimal SetupQuality { get; set; }
    public decimal AtrPct { get; set; }
}

/// <summary>Output from scanner ranking. Persisted to DailyWatchlists as JSON.</summary>
public class ScannerResult
{
    public string Symbol { get; set; } = "";
    public long TickerId { get; set; }
    public int Rank { get; set; }
    public decimal Score { get; set; }
    public string Reason { get; set; } = "";
    public decimal GapPercent { get; set; }
    public decimal PremarketVolumeRatio { get; set; }
    public string? CatalystType { get; set; }
    public decimal SetupQuality { get; set; }
    public decimal AtrPct { get; set; }
}
