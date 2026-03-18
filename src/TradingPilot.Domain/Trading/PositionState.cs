namespace TradingPilot.Trading;

/// <summary>
/// Consolidated state for an open position, tracked by PaperTradingExecutor
/// and monitored by PositionMonitor for continuous exit evaluation.
/// </summary>
public class PositionState
{
    public long TickerId { get; set; }
    public string Ticker { get; set; } = "";
    public string? SymbolId { get; set; }
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

    public bool IsLong => Shares > 0;
}
