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
    /// <summary>ATR at entry time — used for volatility-adaptive stop loss and breakeven threshold.</summary>
    public decimal EntryAtr { get; set; }
    /// <summary>When PeakFavorablePrice was last updated. Anti-wick: trail only from sustained peaks.</summary>
    public DateTime PeakPriceSetAt { get; set; }

    /// <summary>Tracks in-flight exit order. Null = no pending exit.</summary>
    public string? PendingExitOrderId { get; set; }

    // ── Day trading setup context (populated from SetupResult at entry) ──
    /// <summary>Setup type that triggered entry (None for L2-only).</summary>
    public SetupType EntrySetupType { get; set; }
    /// <summary>Structural stop price from setup logic. 0 if L2-only (uses ATR fallback).</summary>
    public decimal SetupStopLevel { get; set; }
    /// <summary>Projected target price from setup logic. 0 if L2-only.</summary>
    public decimal SetupTargetLevel { get; set; }
    /// <summary>Setup quality strength [0, 1] at entry.</summary>
    public decimal SetupStrength { get; set; }
    /// <summary>When the setup expires. Null if L2-only or no expiry.</summary>
    public DateTime? SetupExpiryTime { get; set; }
    /// <summary>Best price in favorable direction during position lifetime (for analytics).</summary>
    public decimal MaxFavorableExcursion { get; set; }

    public bool IsLong => Shares > 0;
    public bool HasSetup => EntrySetupType != SetupType.None;
}
