namespace TradingPilot.Trading;

public class TradingSignal
{
    public long TickerId { get; set; }
    public string Ticker { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public SignalType Type { get; set; }
    public SignalStrength Strength { get; set; }
    public decimal Price { get; set; }
    public string Reason { get; set; } = "";
    public Dictionary<string, decimal> Indicators { get; set; } = new();

    // ── Day trading enrichment (populated by SignalOrchestrator) ──
    /// <summary>What generated this signal.</summary>
    public SignalSource Source { get; set; } = SignalSource.L2Micro;
    /// <summary>Setup type if bar-based, None if L2-only.</summary>
    public SetupType SignalSetupType { get; set; } = SetupType.None;
    /// <summary>Setup quality [0, 1]. 0 if no setup.</summary>
    public decimal SetupStrength { get; set; }
    /// <summary>L2 timing score [-1, +1].</summary>
    public decimal TimingScore { get; set; }
    /// <summary>Context score [-1, +1].</summary>
    public decimal ContextScore { get; set; }
    /// <summary>Final composite score [-1, +1] after filters.</summary>
    public decimal CompositeScore { get; set; }
    /// <summary>The full setup result if bar-based, null if L2-only.</summary>
    public SetupResult? Setup { get; set; }
    /// <summary>Full indicator snapshot at signal time (for DB persistence).</summary>
    public IndicatorSnapshot? Snapshot { get; set; }
}

public enum SignalType { Hold, Buy, Sell }

public enum SignalStrength { Weak, Moderate, Strong }
