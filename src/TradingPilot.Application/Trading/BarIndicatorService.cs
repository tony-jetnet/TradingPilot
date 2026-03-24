using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingPilot.Symbols;
using TradingPilot.Trading;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Linq;
using Volo.Abp.Uow;

namespace TradingPilot.Webull;

/// <summary>
/// Computes technical indicators (EMA, RSI, VWAP, volume stats) from SymbolBars
/// and updates the BarIndicatorCache. Should be called every ~30 seconds.
/// </summary>
public class BarIndicatorService
{
    private readonly BarIndicatorCache _cache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BarIndicatorService> _logger;

    public BarIndicatorService(
        BarIndicatorCache cache,
        IServiceScopeFactory scopeFactory,
        ILogger<BarIndicatorService> logger)
    {
        _cache = cache;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Query the most recent 1-min bars from DB for the given symbol and compute indicators.
    /// </summary>
    public async Task RefreshAsync(long tickerId, string symbolId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            var barRepo = scope.ServiceProvider.GetRequiredService<IRepository<SymbolBar, Guid>>();
            var asyncExecuter = scope.ServiceProvider.GetRequiredService<IAsyncQueryableExecuter>();

            using var uow = uowManager.Begin();

            // Get last 60 1-min bars (Timeframe == Minute1 == 2) for this symbol
            // 60 bars gives EMA/RSI more history for stability, and allows VWAP to filter to today's session
            var queryable = await barRepo.GetQueryableAsync();
            var bars = await asyncExecuter.ToListAsync(
                queryable
                    .Where(b => b.SymbolId == symbolId && b.Timeframe == BarTimeframe.Minute1)
                    .OrderByDescending(b => b.Timestamp)
                    .Take(60));

            await uow.CompleteAsync();

            if (bars.Count < 5)
            {
                _logger.LogDebug("Not enough 1-min bars ({Count}) for tickerId={TickerId}, skipping indicator computation",
                    bars.Count, tickerId);
                return;
            }

            // Reverse to chronological order (oldest first)
            bars.Reverse();

            var closes = bars.Select(b => b.Close).ToArray();
            var volumes = bars.Select(b => b.Volume).ToArray();
            var highs = bars.Select(b => b.High).ToArray();
            var lows = bars.Select(b => b.Low).ToArray();

            // Filter bars to today's trading session for VWAP only
            // EMA/RSI benefit from more history, but VWAP should reset daily
            var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            var todayET = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, eastern).Date;
            var todayBars = bars.Where(b => TimeZoneInfo.ConvertTimeFromUtc(b.Timestamp, eastern).Date == todayET).ToList();

            // Use today's bars for VWAP if we have enough; fall back to all bars if too few
            var vwapBars = todayBars.Count >= 3 ? todayBars : bars;

            // Compute typical price for VWAP: (H+L+C)/3 — using today's session bars
            var vwapTypicalPrices = new decimal[vwapBars.Count];
            var vwapVolumes = new long[vwapBars.Count];
            for (int i = 0; i < vwapBars.Count; i++)
            {
                vwapTypicalPrices[i] = (vwapBars[i].High + vwapBars[i].Low + vwapBars[i].Close) / 3m;
                vwapVolumes[i] = vwapBars[i].Volume;
            }

            decimal ema9 = ComputeEma(closes, 9);
            decimal ema20 = ComputeEma(closes, 20);
            decimal rsi14 = ComputeRsi(closes, 14);
            decimal vwap = ComputeVwap(vwapTypicalPrices, vwapVolumes);
            decimal atr14 = ComputeAtr(highs, lows, closes, 14);

            // Volume stats
            decimal avgVolume20 = volumes.Length >= 20
                ? (decimal)volumes.Take(volumes.Length - 1).TakeLast(20).Average()
                : (decimal)volumes.Take(volumes.Length - 1).DefaultIfEmpty(1).Average();
            decimal currentVolume = volumes[^1];
            decimal volumeRatio = avgVolume20 > 0 ? currentVolume / avgVolume20 : 1m;

            decimal lastClose = closes[^1];

            int trendDirection = 0;
            decimal emaDiff = ema9 - ema20;
            decimal threshold = lastClose * 0.0005m; // 0.05% threshold for neutral zone
            if (emaDiff > threshold) trendDirection = 1;
            else if (emaDiff < -threshold) trendDirection = -1;

            var indicators = new BarIndicators
            {
                Ema9 = Math.Round(ema9, 4),
                Ema20 = Math.Round(ema20, 4),
                Vwap = Math.Round(vwap, 4),
                Rsi14 = Math.Round(rsi14, 2),
                Atr14 = Math.Round(atr14, 4),
                Atr14Pct = lastClose > 0 ? Math.Round(atr14 / lastClose, 6) : 0,
                AvgVolume20 = Math.Round(avgVolume20, 0),
                CurrentVolume = currentVolume,
                VolumeRatio = Math.Round(volumeRatio, 2),
                TrendDirection = trendDirection,
                AboveVwap = lastClose > vwap,
                OverboughtRsi = rsi14 > 70,
                OversoldRsi = rsi14 < 30,
                HighVolume = volumeRatio > 1.5m,
                ComputedAt = DateTime.UtcNow,
                LastRefreshTime = DateTime.UtcNow,
            };

            _cache.Update(tickerId, indicators);

            _logger.LogDebug(
                "Bar indicators updated for tickerId={TickerId}: EMA9={Ema9:F2} EMA20={Ema20:F2} " +
                "RSI={Rsi:F1} VWAP={Vwap:F2} Trend={Trend} VolRatio={VolRatio:F2}",
                tickerId, indicators.Ema9, indicators.Ema20, indicators.Rsi14,
                indicators.Vwap, indicators.TrendDirection, indicators.VolumeRatio);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh bar indicators for tickerId={TickerId}", tickerId);
        }
    }

    /// <summary>
    /// EMA: EMA_today = price * k + EMA_yesterday * (1 - k), where k = 2 / (period + 1).
    /// </summary>
    internal static decimal ComputeEma(decimal[] prices, int period)
    {
        if (prices.Length == 0) return 0;
        if (prices.Length == 1) return prices[0];

        int effectivePeriod = Math.Min(period, prices.Length);
        decimal k = 2.0m / (effectivePeriod + 1);

        // Seed EMA with SMA of first N values
        decimal ema = prices.Take(effectivePeriod).Average();

        for (int i = effectivePeriod; i < prices.Length; i++)
        {
            ema = prices[i] * k + ema * (1 - k);
        }

        return ema;
    }

    /// <summary>
    /// RSI = 100 - 100 / (1 + avgGain / avgLoss) over N periods.
    /// </summary>
    internal static decimal ComputeRsi(decimal[] prices, int period)
    {
        if (prices.Length < 2) return 50; // neutral default

        int effectivePeriod = Math.Min(period, prices.Length - 1);

        decimal sumGain = 0, sumLoss = 0;

        // Initial average gain/loss
        for (int i = 1; i <= effectivePeriod; i++)
        {
            decimal change = prices[i] - prices[i - 1];
            if (change > 0) sumGain += change;
            else sumLoss += Math.Abs(change);
        }

        decimal avgGain = sumGain / effectivePeriod;
        decimal avgLoss = sumLoss / effectivePeriod;

        // Smoothed RSI for remaining periods
        for (int i = effectivePeriod + 1; i < prices.Length; i++)
        {
            decimal change = prices[i] - prices[i - 1];
            decimal gain = change > 0 ? change : 0;
            decimal loss = change < 0 ? Math.Abs(change) : 0;

            avgGain = (avgGain * (effectivePeriod - 1) + gain) / effectivePeriod;
            avgLoss = (avgLoss * (effectivePeriod - 1) + loss) / effectivePeriod;
        }

        if (avgLoss == 0) return 100;
        decimal rs = avgGain / avgLoss;
        return 100m - 100m / (1m + rs);
    }

    /// <summary>
    /// ATR (Average True Range) = SMA of True Range over N periods.
    /// True Range = Max(High - Low, |High - PrevClose|, |Low - PrevClose|).
    /// Used for volatility-based position sizing.
    /// </summary>
    internal static decimal ComputeAtr(decimal[] highs, decimal[] lows, decimal[] closes, int period)
    {
        if (highs.Length < 2 || lows.Length < 2 || closes.Length < 2) return 0;

        int count = Math.Min(Math.Min(highs.Length, lows.Length), closes.Length);
        int effectivePeriod = Math.Min(period, count - 1);
        if (effectivePeriod <= 0) return 0;

        decimal sumTr = 0;
        int trCount = 0;

        for (int i = 1; i < count; i++)
        {
            decimal hl = highs[i] - lows[i];
            decimal hpc = Math.Abs(highs[i] - closes[i - 1]);
            decimal lpc = Math.Abs(lows[i] - closes[i - 1]);
            decimal tr = Math.Max(hl, Math.Max(hpc, lpc));

            sumTr += tr;
            trCount++;

            // Only keep the last N true ranges for the average
            if (trCount > effectivePeriod)
            {
                // Simple approach: recompute from the last N periods
                // For a streaming ATR we'd use exponential smoothing,
                // but for 30 bars this is fine
            }
        }

        // Use last N true ranges
        if (trCount <= effectivePeriod)
            return trCount > 0 ? sumTr / trCount : 0;

        // Recompute using only the last effectivePeriod TRs
        sumTr = 0;
        for (int i = count - effectivePeriod; i < count; i++)
        {
            decimal hl = highs[i] - lows[i];
            decimal hpc = Math.Abs(highs[i] - closes[i - 1]);
            decimal lpc = Math.Abs(lows[i] - closes[i - 1]);
            sumTr += Math.Max(hl, Math.Max(hpc, lpc));
        }

        return sumTr / effectivePeriod;
    }

    /// <summary>
    /// VWAP = sum(typicalPrice * volume) / sum(volume).
    /// Uses today's bars only (all passed-in bars are assumed to be today's).
    /// </summary>
    internal static decimal ComputeVwap(decimal[] typicalPrices, long[] volumes)
    {
        if (typicalPrices.Length == 0 || volumes.Length == 0) return 0;

        decimal sumPV = 0;
        long sumV = 0;

        int count = Math.Min(typicalPrices.Length, volumes.Length);
        for (int i = 0; i < count; i++)
        {
            sumPV += typicalPrices[i] * volumes[i];
            sumV += volumes[i];
        }

        return sumV > 0 ? sumPV / sumV : 0;
    }
}
