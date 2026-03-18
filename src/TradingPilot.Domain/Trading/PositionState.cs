namespace TradingPilot.Trading;

/// <summary>
/// Consolidated state for an open position, tracked by PaperTradingExecutor
/// and monitored by PositionMonitor for continuous exit evaluation.
/// Keyed by Symbol (ticker name) in the positions dictionary.
/// </summary>
public class PositionState
{
    public string Symbol { get; set; } = "";
    public long TickerId { get; set; } // Webull-specific, kept for signal correlation with L2/tick caches
    public int Shares { get; set; }
    public decimal EntryPrice { get; set; }
    public DateTime EntryTime { get; set; }
    public decimal EntryScore { get; set; }
    public decimal PeakFavorableScore { get; set; }
    public decimal EntryImbalance { get; set; }
    public decimal EntrySpreadPercentile { get; set; }
    public int EntryTrendDirection { get; set; }
    public decimal NotionalValue { get; set; }
    public string? EntryRuleId { get; set; }
    public decimal RuleConfidence { get; set; }
    public int HoldSeconds { get; set; }
    public decimal StopLoss { get; set; }
    public decimal EntrySpread { get; set; }
    public decimal PeakFavorablePrice { get; set; }

    /// <summary>Tracks in-flight exit order. Null = no pending exit.</summary>
    public string? PendingExitOrderId { get; set; }

    public bool IsLong => Shares > 0;
}
