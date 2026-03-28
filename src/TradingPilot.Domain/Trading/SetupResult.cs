namespace TradingPilot.Trading;

/// <summary>
/// Ephemeral DTO produced by SetupDetector describing a detected day-trade setup.
/// Consumed by SignalOrchestrator to build composite signals and by PaperTradingExecutor
/// to populate PositionState with thesis-aware stop/target/invalidation context.
/// Not persisted directly — BarSetup entity captures this for DB storage.
/// </summary>
public class SetupResult
{
    /// <summary>Which of the 4 setup patterns matched.</summary>
    public SetupType Type { get; set; }

    /// <summary>BUY or SELL direction.</summary>
    public SignalType Direction { get; set; }

    /// <summary>Setup quality score [0, 1]. Higher = tighter EMA alignment, stronger volume, cleaner pattern.</summary>
    public decimal Strength { get; set; }

    /// <summary>Lower bound of the ideal entry price range.</summary>
    public decimal EntryZoneLow { get; set; }

    /// <summary>Upper bound of the ideal entry price range.</summary>
    public decimal EntryZoneHigh { get; set; }

    /// <summary>Structural stop price based on the setup's logic (e.g., below EMA50, below VWAP).</summary>
    public decimal StopLevel { get; set; }

    /// <summary>Projected target price based on the setup's logic (e.g., ATR projection, measured move).</summary>
    public decimal TargetLevel { get; set; }

    /// <summary>When this setup expires if no entry is taken. Typically 30-60 minutes after detection.</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Human-readable description of what would invalidate this setup's thesis.
    /// Used for logging and dashboard display. The actual invalidation check is in SetupDetector.IsSetupInvalidated().
    /// </summary>
    public string InvalidationDescription { get; set; } = "";

    /// <summary>Human-readable setup description for logging (e.g., "TREND_FOLLOW BUY: pullback to EMA20 at $128.50").</summary>
    public string Description { get; set; } = "";

    /// <summary>Price at the time the setup was detected.</summary>
    public decimal DetectionPrice { get; set; }

    /// <summary>ATR14 on 5-min bars at detection time. Used for stop/target distance calculation.</summary>
    public decimal Atr { get; set; }

    /// <summary>Stop distance in dollars (|DetectionPrice - StopLevel|). Pre-computed for convenience.</summary>
    public decimal StopDistance => Math.Abs(DetectionPrice - StopLevel);

    /// <summary>Target distance in dollars (|TargetLevel - DetectionPrice|). Pre-computed for convenience.</summary>
    public decimal TargetDistance => Math.Abs(TargetLevel - DetectionPrice);

    /// <summary>Risk/reward ratio (TargetDistance / StopDistance). Must be ≥ 2.0 for day trades.</summary>
    public decimal RiskReward => StopDistance > 0 ? TargetDistance / StopDistance : 0;
}
