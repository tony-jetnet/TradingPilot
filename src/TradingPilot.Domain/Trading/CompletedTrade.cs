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

    // ── Day trading fields ──
    /// <summary>Signal source (SignalSource enum as byte).</summary>
    public byte? DayTradeSource { get; set; }
    /// <summary>Setup type (SetupType enum as byte).</summary>
    public byte? DayTradeSetupType { get; set; }
    /// <summary>Bar-based setup strength at entry [0, 1].</summary>
    public decimal? SetupScore { get; set; }
    /// <summary>L2 timing score at entry [-1, +1].</summary>
    public decimal? TimingScore { get; set; }
    /// <summary>Planned hold time at entry in seconds.</summary>
    public int? HoldSeconds { get; set; }
    /// <summary>Effective stop distance at entry in dollars.</summary>
    public decimal? StopDistance { get; set; }
    /// <summary>Did the setup thesis get invalidated before exit?</summary>
    public bool? SetupInvalidated { get; set; }
}
