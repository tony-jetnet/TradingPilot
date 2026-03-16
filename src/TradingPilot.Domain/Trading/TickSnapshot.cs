using Volo.Abp.Domain.Entities;

namespace TradingPilot.Trading;

/// <summary>
/// Periodic snapshot (every ~10 seconds) of tick/quote data and computed indicators
/// for a ticker. Used for historical analysis of signal quality.
/// </summary>
public class TickSnapshot : Entity<Guid>
{
    public Guid SymbolId { get; set; }
    public long TickerId { get; set; }
    public DateTime Timestamp { get; set; }

    // Quote data
    public decimal Price { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public long Volume { get; set; }

    // Computed indicators
    public decimal Vwap { get; set; }
    public decimal Ema9 { get; set; }
    public decimal Ema20 { get; set; }
    public decimal Rsi14 { get; set; }
    public decimal VolumeRatio { get; set; }

    // Tick-derived metrics
    public int UptickCount { get; set; }
    public int DowntickCount { get; set; }
    public decimal TickMomentum { get; set; }

    // L2-derived features (computed from order book snapshots)
    public decimal BookDepthRatio { get; set; }
    public decimal BidWallSize { get; set; }
    public decimal AskWallSize { get; set; }
    public decimal BidSweepCost { get; set; }
    public decimal AskSweepCost { get; set; }
    public decimal ImbalanceVelocity { get; set; }
    public decimal SpreadPercentile { get; set; }
}
