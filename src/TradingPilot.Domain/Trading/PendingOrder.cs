namespace TradingPilot.Trading;

/// <summary>
/// Tracks an in-flight order that hasn't been confirmed by the broker yet.
/// Contains all metadata needed to create a PositionState upon fill confirmation.
/// Keyed by Symbol (ticker name), not broker-specific IDs.
/// </summary>
public class PendingOrder
{
    public string Symbol { get; set; } = "";
    public long TickerId { get; set; } // Webull-specific, used internally for signal correlation
    public string? OrderId { get; set; }
    public string Action { get; set; } = "BUY";
    public int Quantity { get; set; }
    public decimal LimitPrice { get; set; }
    public DateTime PlacedAt { get; set; }
    public OrderPurpose Purpose { get; set; }

    // Entry metadata (used to create PositionState on fill)
    public decimal EntryScore { get; set; }
    public decimal EntryImbalance { get; set; }
    public decimal EntrySpread { get; set; }
    public decimal EntrySpreadPercentile { get; set; }
    public int EntryTrendDirection { get; set; }
    public string? EntryRuleId { get; set; }
    public decimal RuleConfidence { get; set; }
    public int HoldSeconds { get; set; }
    public decimal StopLoss { get; set; }

    // Day trading setup context (set at entry, transferred to PositionState on fill)
    public SetupType EntrySetupType { get; set; }
    public decimal SetupStopLevel { get; set; }
    public decimal SetupTargetLevel { get; set; }
    public decimal SetupStrength { get; set; }
    public DateTime? SetupExpiryTime { get; set; }

    // Exit metadata (set when exit order is placed, read on fill confirmation)
    public string ExitReason { get; set; } = "";
}

public enum OrderPurpose
{
    Entry,
    Exit,
}
