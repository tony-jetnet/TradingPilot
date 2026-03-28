using Volo.Abp.Domain.Entities;

namespace TradingPilot.Trading;

/// <summary>
/// A completed round-trip trade with entry/exit metadata. Persisted to DB for dashboard display.
/// DISPLAY ONLY — this table is NOT used for trading decisions. Broker API is the sole source of
/// truth for P&amp;L, positions, and order status. This table exists only to show entry source,
/// entry score, and exit reason in the Recent Trades UI (data not available from broker API).
/// </summary>
public class CompletedTrade : Entity<Guid>
{
    public CompletedTrade() : base(Guid.NewGuid()) { }

    public string Ticker { get; set; } = null!;
    public long TickerId { get; set; }
    public bool IsLong { get; set; }
    public int Quantity { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal ExitPrice { get; set; }
    public DateTime EntryTime { get; set; }
    public DateTime ExitTime { get; set; }
    public decimal Pnl { get; set; }
    public string EntrySource { get; set; } = ""; // RULE, SWIN, WEIGHTED
    public decimal EntryScore { get; set; }
    public string ExitReason { get; set; } = ""; // Full reason from PositionMonitor (e.g. "TRAILING STOP peak=0.16...")
}
