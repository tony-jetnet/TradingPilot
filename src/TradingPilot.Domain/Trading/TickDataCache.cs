using System.Collections.Concurrent;
using TradingPilot.Symbols;

namespace TradingPilot.Trading;

/// <summary>
/// In-memory cache for real-time tick and quote data per ticker.
/// Follows the same singleton pattern as L2BookCache.
/// Also computes L2-derived features from order book snapshots.
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

    /// <summary>
    /// Compute and cache L2-derived features from a fresh order book snapshot.
    /// Called on every L2 depth update from MqttMessageProcessor.
    /// </summary>
    public void UpdateL2Features(long tickerId, SymbolBookSnapshot snapshot)
    {
        var data = _data.GetOrAdd(tickerId, _ => new TickerLiveData());

        lock (data)
        {
            ComputeL2Features(data, snapshot);
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

    /// <summary>
    /// Compute L2-derived features from an order book snapshot.
    /// </summary>
    private static void ComputeL2Features(TickerLiveData data, SymbolBookSnapshot snapshot)
    {
        // 1. BookDepthRatio: sum(top5 bid+ask size) / sum(all bid+ask size)
        decimal top5BidSize = snapshot.BidSizes.Take(5).Sum();
        decimal top5AskSize = snapshot.AskSizes.Take(5).Sum();
        decimal totalBidSize = snapshot.BidSizes.Sum();
        decimal totalAskSize = snapshot.AskSizes.Sum();
        decimal totalSize = totalBidSize + totalAskSize;

        data.BookDepthRatio = totalSize > 0
            ? (top5BidSize + top5AskSize) / totalSize
            : 0;

        // 2. BidWallSize: max(bid size) / avg(bid size)
        if (snapshot.BidSizes.Length > 0)
        {
            decimal avgBid = totalBidSize / snapshot.BidSizes.Length;
            data.BidWallSize = avgBid > 0 ? snapshot.BidSizes.Max() / avgBid : 0;
        }
        else
        {
            data.BidWallSize = 0;
        }

        // 3. AskWallSize: max(ask size) / avg(ask size)
        if (snapshot.AskSizes.Length > 0)
        {
            decimal avgAsk = totalAskSize / snapshot.AskSizes.Length;
            data.AskWallSize = avgAsk > 0 ? snapshot.AskSizes.Max() / avgAsk : 0;
        }
        else
        {
            data.AskWallSize = 0;
        }

        // 4. BidSweepCost: shares needed to move price down $0.10
        data.BidSweepCost = ComputeSweepCost(snapshot.BidPrices, snapshot.BidSizes, snapshot.MidPrice, -0.10m);

        // 5. AskSweepCost: shares needed to move price up $0.10
        data.AskSweepCost = ComputeSweepCost(snapshot.AskPrices, snapshot.AskSizes, snapshot.MidPrice, 0.10m);

        // 6. ImbalanceVelocity: (current OBI - OBI ~30s ago) normalized to 30s rate
        //    Uses closest-sample interpolation instead of rigid 25-35s window
        decimal currentObi = snapshot.Imbalance;
        var now = DateTime.UtcNow;
        data.RecentObis.Enqueue((currentObi, now));
        while (data.RecentObis.Count > 120) // keep ~120 samples for 60s of history at ~2/sec
            data.RecentObis.Dequeue();

        // Find the OBI sample closest to 30 seconds ago (must be at least 10s old)
        (decimal Obi, double Age) closest = default;
        double bestDelta = double.MaxValue;

        foreach (var (obi, ts) in data.RecentObis)
        {
            double age = (now - ts).TotalSeconds;
            double delta = Math.Abs(age - 30.0);
            if (delta < bestDelta && age >= 10) // at least 10s old to avoid noise
            {
                bestDelta = delta;
                closest = (obi, age);
            }
        }

        if (closest.Age > 0)
            data.ImbalanceVelocity = (currentObi - closest.Obi) / (decimal)closest.Age * 30m; // normalize to 30s rate
        else
            data.ImbalanceVelocity = 0;

        // 7. SpreadPercentile: rank of current spread in last 5 min (time-based window)
        data.RecentSpreads.Enqueue((snapshot.Spread, now));
        while (data.RecentSpreads.Count > 600) // max 600 samples (~5 min at 2 updates/sec)
            data.RecentSpreads.Dequeue();

        var spreadCutoff = now.AddMinutes(-5);
        var recentSpreads = data.RecentSpreads
            .Where(s => s.Time >= spreadCutoff)
            .Select(s => s.Spread)
            .ToList();

        if (recentSpreads.Count >= 10)
        {
            int countBelow = recentSpreads.Count(s => s < snapshot.Spread);
            data.SpreadPercentile = (decimal)countBelow / recentSpreads.Count;
        }
        else
        {
            data.SpreadPercentile = 0.50m; // not enough data, assume median
        }
    }

    /// <summary>
    /// Calculate how many shares it takes to sweep through the book by $priceMove.
    /// For bids (priceMove negative): sweep from best bid downward.
    /// For asks (priceMove positive): sweep from best ask upward.
    /// </summary>
    private static decimal ComputeSweepCost(decimal[] prices, decimal[] sizes, decimal midPrice, decimal priceMove)
    {
        if (prices.Length == 0 || sizes.Length == 0) return 0;

        decimal target = midPrice + priceMove;
        decimal totalShares = 0;

        for (int i = 0; i < Math.Min(prices.Length, sizes.Length); i++)
        {
            totalShares += sizes[i];

            if (priceMove > 0 && prices[i] >= target)
                break;
            if (priceMove < 0 && prices[i] <= target)
                break;
        }

        return totalShares;
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

    // L2-derived features (computed from order book snapshots)
    /// <summary>sum(top5 bid+ask size) / sum(all bid+ask size). Higher = liquidity concentrated near top.</summary>
    public decimal BookDepthRatio { get; set; }
    /// <summary>max(bid size) / avg(bid size). High = institutional bid support.</summary>
    public decimal BidWallSize { get; set; }
    /// <summary>max(ask size) / avg(ask size). High = institutional sell wall.</summary>
    public decimal AskWallSize { get; set; }
    /// <summary>Shares needed to move price down $0.10. True bid liquidity.</summary>
    public decimal BidSweepCost { get; set; }
    /// <summary>Shares needed to move price up $0.10. True ask liquidity.</summary>
    public decimal AskSweepCost { get; set; }
    /// <summary>(current OBI - OBI 30s ago) / 30. Speed of pressure change.</summary>
    public decimal ImbalanceVelocity { get; set; }
    /// <summary>Rank of current spread in last 5 min. 0=tightest, 1=widest.</summary>
    public decimal SpreadPercentile { get; set; }

    // Rolling buffers for L2 feature computation
    public Queue<(decimal Obi, DateTime Timestamp)> RecentObis { get; } = new();
    public Queue<(decimal Spread, DateTime Time)> RecentSpreads { get; } = new();

    public const int MaxTicks = 500;
}
