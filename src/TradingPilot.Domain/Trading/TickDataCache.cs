using System.Collections.Concurrent;

namespace TradingPilot.Trading;

/// <summary>
/// In-memory cache for real-time tick and quote data per ticker.
/// Follows the same singleton pattern as L2BookCache.
/// </summary>
public class TickDataCache
{
    private readonly ConcurrentDictionary<long, TickerLiveData> _data = new();

    /// <summary>
    /// Record a new tick (price trade) and recompute tick-derived metrics.
    /// </summary>
    public void AddTick(long tickerId, decimal price, long timestampMs)
    {
        var data = _data.GetOrAdd(tickerId, _ => new TickerLiveData());

        lock (data)
        {
            // Track direction relative to previous tick
            if (data.RecentTicks.Count > 0)
            {
                var (prevPrice, _) = data.RecentTicks.Last();
                if (price > prevPrice) data.UptickCount++;
                else if (price < prevPrice) data.DowntickCount++;
            }

            data.RecentTicks.Enqueue((price, timestampMs));
            while (data.RecentTicks.Count > TickerLiveData.MaxTicks)
                data.RecentTicks.Dequeue();

            // Prune old uptick/downtick counts and recompute from ticks in last 30s
            RecomputeTickMetrics(data, timestampMs);
        }
    }

    /// <summary>
    /// Record a quote update (OHLCV snapshot).
    /// </summary>
    public void AddQuote(long tickerId, decimal price, decimal open, decimal high, decimal low,
                         long volume, decimal changeRatio, string? tradeTime)
    {
        var data = _data.GetOrAdd(tickerId, _ => new TickerLiveData());

        lock (data)
        {
            data.LastPrice = price;
            data.Open = open;
            data.High = high;
            data.Low = low;
            data.Volume = volume;
            data.ChangeRatio = changeRatio;
            data.LastQuoteTime = DateTime.UtcNow;
        }
    }

    public TickerLiveData? GetData(long tickerId)
    {
        return _data.TryGetValue(tickerId, out var data) ? data : null;
    }

    /// <summary>
    /// Recompute tick momentum, uptick/downtick counts, and velocity
    /// using only ticks within the last 30 seconds.
    /// </summary>
    private static void RecomputeTickMetrics(TickerLiveData data, long currentTimestampMs)
    {
        const long window30s = 30_000;
        const long window10s = 10_000;

        long cutoff30s = currentTimestampMs - window30s;
        long cutoff10s = currentTimestampMs - window10s;

        int upticks = 0, downticks = 0;
        int ticksIn10s = 0;
        decimal? prevPrice = null;

        foreach (var (price, ts) in data.RecentTicks)
        {
            if (ts >= cutoff30s && prevPrice.HasValue)
            {
                if (price > prevPrice.Value) upticks++;
                else if (price < prevPrice.Value) downticks++;
            }

            if (ts >= cutoff10s)
                ticksIn10s++;

            prevPrice = price;
        }

        data.UptickCount = upticks;
        data.DowntickCount = downticks;

        int total = upticks + downticks;
        data.TickMomentum = total > 0
            ? (decimal)(upticks - downticks) / total
            : 0m;

        data.TickVelocity = ticksIn10s / 10.0m;
    }
}

public class TickerLiveData
{
    /// <summary>Last N ticks: (Price, TimestampMs).</summary>
    public Queue<(decimal Price, long TimestampMs)> RecentTicks { get; } = new();

    // Latest quote snapshot
    public decimal LastPrice { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public long Volume { get; set; }
    public decimal ChangeRatio { get; set; }
    public DateTime LastQuoteTime { get; set; }

    // Computed from ticks
    /// <summary>Weighted ratio of (upticks - downticks) / total in last 30s. Range: -1 to +1.</summary>
    public decimal TickMomentum { get; set; }
    /// <summary>Number of upticks in the last 30 seconds.</summary>
    public int UptickCount { get; set; }
    /// <summary>Number of downticks in the last 30 seconds.</summary>
    public int DowntickCount { get; set; }
    /// <summary>Ticks per second (measured over last 10 seconds).</summary>
    public decimal TickVelocity { get; set; }

    public const int MaxTicks = 500;
}
