using Shouldly;
using TradingPilot.Webull;
using Xunit;

namespace TradingPilot.Trading;

/// <summary>
/// Phase 3 verification: multi-timeframe indicator computation produces correct values.
/// Tests the internal static math methods directly (no DB, no DI, no ABP module needed).
/// </summary>
public class BarIndicatorServiceTests
{
    [Fact]
    public void ComputeEma_Period9_ReactsFasterThanPeriod20()
    {
        decimal[] prices = [100, 101, 102, 103, 104, 105, 106, 107, 108, 109,
                            110, 111, 112, 113, 114, 115, 116, 117, 118, 119];

        decimal ema9 = BarIndicatorService.ComputeEma(prices, 9);
        decimal ema20 = BarIndicatorService.ComputeEma(prices, 20);

        ema9.ShouldBeGreaterThan(ema20);
        ema9.ShouldBeGreaterThanOrEqualTo(115m);
        ema9.ShouldBeLessThan(119m);
    }

    [Fact]
    public void ComputeEma_Period50_ConvergesWithEnoughData()
    {
        // 120 bars — enough for EMA50 convergence (needed for 5m/15m indicators)
        decimal[] prices = new decimal[120];
        for (int i = 0; i < 120; i++)
            prices[i] = 100 + i * 0.1m;

        decimal ema50 = BarIndicatorService.ComputeEma(prices, 50);

        ema50.ShouldBeGreaterThan(106m);
        ema50.ShouldBeLessThan(112m);
    }

    [Fact]
    public void ComputeRsi_AllGains_NearHundred()
    {
        decimal[] prices = new decimal[20];
        prices[0] = 100;
        for (int i = 1; i < 20; i++)
            prices[i] = prices[i - 1] + 0.5m;

        BarIndicatorService.ComputeRsi(prices, 14).ShouldBeGreaterThan(90m);
    }

    [Fact]
    public void ComputeRsi_AllLosses_NearZero()
    {
        decimal[] prices = new decimal[20];
        prices[0] = 200;
        for (int i = 1; i < 20; i++)
            prices[i] = prices[i - 1] - 0.5m;

        BarIndicatorService.ComputeRsi(prices, 14).ShouldBeLessThan(10m);
    }

    [Fact]
    public void ComputeRsi_Alternating_NearFifty()
    {
        decimal[] prices = new decimal[30];
        prices[0] = 100;
        for (int i = 1; i < 30; i++)
            prices[i] = prices[i - 1] + (i % 2 == 0 ? 0.3m : -0.3m);

        var rsi = BarIndicatorService.ComputeRsi(prices, 14);
        rsi.ShouldBeGreaterThan(40m);
        rsi.ShouldBeLessThan(60m);
    }

    [Fact]
    public void ComputeAtr_HighVolatility_GreaterThanLow()
    {
        int n = 20;
        decimal[] highs = new decimal[n], lows = new decimal[n], closes = new decimal[n];

        for (int i = 0; i < n; i++)
        {
            closes[i] = 100 + i * 0.1m;
            highs[i] = closes[i] + 0.2m;
            lows[i] = closes[i] - 0.2m;
        }
        decimal atrLow = BarIndicatorService.ComputeAtr(highs, lows, closes, 14);

        for (int i = 0; i < n; i++)
        {
            highs[i] = closes[i] + 2.0m;
            lows[i] = closes[i] - 2.0m;
        }
        decimal atrHigh = BarIndicatorService.ComputeAtr(highs, lows, closes, 14);

        atrHigh.ShouldBeGreaterThan(atrLow * 5);
    }

    [Fact]
    public void ComputeVwap_WeightsByVolume()
    {
        decimal[] tp = [100m, 110m];
        long[] volumes = [100, 900];

        // VWAP = (100*100 + 110*900) / 1000 = 109
        BarIndicatorService.ComputeVwap(tp, volumes).ShouldBe(109m);
    }

    [Fact]
    public void TrendDirection_5m_BullishWhenEma20AboveEma50()
    {
        // Rising series → EMA20 leads EMA50
        decimal[] prices = new decimal[60];
        for (int i = 0; i < 60; i++)
            prices[i] = 100 + i * 0.5m;

        decimal ema20 = BarIndicatorService.ComputeEma(prices, 20);
        decimal ema50 = BarIndicatorService.ComputeEma(prices, 50);
        decimal lastClose = prices[^1];

        ema20.ShouldBeGreaterThan(ema50);

        int trend = (ema20 - ema50) > lastClose * 0.0005m ? 1 : -1;
        trend.ShouldBe(1);
    }

    [Fact]
    public void TrendDirection_5m_BearishWhenEma20BelowEma50()
    {
        // Falling series → EMA20 lags below EMA50
        decimal[] prices = new decimal[60];
        for (int i = 0; i < 60; i++)
            prices[i] = 200 - i * 0.5m;

        decimal ema20 = BarIndicatorService.ComputeEma(prices, 20);
        decimal ema50 = BarIndicatorService.ComputeEma(prices, 50);
        decimal lastClose = prices[^1];

        ema20.ShouldBeLessThan(ema50);

        int trend = (ema20 - ema50) < -(lastClose * 0.0005m) ? -1 : 1;
        trend.ShouldBe(-1);
    }
}
