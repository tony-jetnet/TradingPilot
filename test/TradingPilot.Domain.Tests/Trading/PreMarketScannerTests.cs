using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TradingPilot.Trading;
using Xunit;

namespace TradingPilot.Trading;

public class PreMarketScannerTests
{
    private readonly PreMarketScanner _scanner = new(NullLogger<PreMarketScanner>.Instance);

    [Fact]
    public void Rank_ReturnsTop10_SortedByScoreDescending()
    {
        var candidates = Enumerable.Range(1, 20).Select(i => new ScannerInput
        {
            Symbol = $"SYM{i}",
            TickerId = i,
            GapPercent = 0.01m * i,    // SYM20 has highest gap (20%)
            PremarketVolumeRatio = 1.0m + i * 0.1m,
            AtrPct = 0.01m,
        }).ToList();

        var results = _scanner.Rank(candidates);

        results.Count.ShouldBe(10);
        results[0].Score.ShouldBeGreaterThanOrEqualTo(results[1].Score);
        results[0].Rank.ShouldBe(1);
        results[9].Rank.ShouldBe(10);
    }

    [Fact]
    public void Rank_HighGapStock_RanksHigher()
    {
        var candidates = new List<ScannerInput>
        {
            new() { Symbol = "LOW_GAP", TickerId = 1, GapPercent = 0.005m, AtrPct = 0.01m },
            new() { Symbol = "HIGH_GAP", TickerId = 2, GapPercent = 0.04m, AtrPct = 0.01m },
        };

        var results = _scanner.Rank(candidates, 2);

        results[0].Symbol.ShouldBe("HIGH_GAP");
    }

    [Fact]
    public void Rank_CatalystStock_RanksHigher()
    {
        var candidates = new List<ScannerInput>
        {
            new() { Symbol = "NO_CAT", TickerId = 1, GapPercent = 0.02m, AtrPct = 0.01m },
            new() { Symbol = "EARNINGS", TickerId = 2, GapPercent = 0.02m, AtrPct = 0.01m, CatalystType = "EARNINGS" },
        };

        var results = _scanner.Rank(candidates, 2);

        results[0].Symbol.ShouldBe("EARNINGS");
    }

    [Fact]
    public void Rank_LowAtr_FilteredToBottom()
    {
        var candidates = new List<ScannerInput>
        {
            new() { Symbol = "LOW_ATR", TickerId = 1, GapPercent = 0.03m, AtrPct = 0.003m }, // Below 0.8% min
            new() { Symbol = "GOOD_ATR", TickerId = 2, GapPercent = 0.02m, AtrPct = 0.015m },
        };

        var results = _scanner.Rank(candidates, 2);

        // LOW_ATR gets 0 for ATR score, should rank lower
        results[0].Symbol.ShouldBe("GOOD_ATR");
    }

    [Fact]
    public void Rank_AllFactorsCombined_ProducesReasonableRanking()
    {
        var candidates = new List<ScannerInput>
        {
            new() { Symbol = "PERFECT", TickerId = 1, GapPercent = 0.05m, PremarketVolumeRatio = 3.0m,
                     CatalystType = "EARNINGS", CapitalFlowNet = 0.5m, SetupQuality = 0.8m, AtrPct = 0.015m },
            new() { Symbol = "MEDIOCRE", TickerId = 2, GapPercent = 0.01m, PremarketVolumeRatio = 1.2m,
                     AtrPct = 0.01m },
            new() { Symbol = "TERRIBLE", TickerId = 3, GapPercent = 0.002m, PremarketVolumeRatio = 0.5m,
                     AtrPct = 0.005m },
        };

        var results = _scanner.Rank(candidates, 3);

        results[0].Symbol.ShouldBe("PERFECT");
        results[0].Score.ShouldBeGreaterThan(results[1].Score);
        results[1].Score.ShouldBeGreaterThan(results[2].Score);
    }

    [Fact]
    public void Rank_ResultContainsAllMetadata()
    {
        var candidates = new List<ScannerInput>
        {
            new() { Symbol = "NVDA", TickerId = 913256789, GapPercent = 0.03m,
                     PremarketVolumeRatio = 2.5m, CatalystType = "ANALYST",
                     CapitalFlowNet = 0.3m, SetupQuality = 0.6m, AtrPct = 0.012m },
        };

        var results = _scanner.Rank(candidates, 1);

        results.Count.ShouldBe(1);
        var r = results[0];
        r.Symbol.ShouldBe("NVDA");
        r.TickerId.ShouldBe(913256789);
        r.Rank.ShouldBe(1);
        r.Score.ShouldBeGreaterThan(0);
        r.Reason.ShouldContain("gap=");
        r.Reason.ShouldContain("vol=");
        r.Reason.ShouldContain("catalyst=ANALYST");
        r.GapPercent.ShouldBe(0.03m);
        r.CatalystType.ShouldBe("ANALYST");
    }

    [Fact]
    public void ScannerWeights_SumToOne()
    {
        decimal total = DayTradeConfig.ScannerWeightGap + DayTradeConfig.ScannerWeightVolume +
                        DayTradeConfig.ScannerWeightCatalyst + DayTradeConfig.ScannerWeightCapitalFlow +
                        DayTradeConfig.ScannerWeightSetupQuality + DayTradeConfig.ScannerWeightAtr;
        total.ShouldBe(1.0m);
    }
}
