namespace TradingPilot.Trading;

/// <summary>
/// Nightly ML training output: learned weights, thresholds, and per-hour adjustments
/// for each ticker. Consumed by MarketMicrostructureAnalyzer and PaperTradingExecutor
/// at runtime as pure math — no ML libraries needed during live trading.
/// </summary>
public class ModelConfig
{
    public DateTime TrainedAt { get; set; }
    public int TrainingRows { get; set; }
    public int LookbackDays { get; set; }
    public Dictionary<long, TickerModelConfig> Tickers { get; set; } = new();
}

public class TickerModelConfig
{
    public long TickerId { get; set; }
    public string Ticker { get; set; } = "";
    public int TrainingSamples { get; set; }
    public decimal OverallWinRate { get; set; }

    // Learned indicator weights (replace fixed weights in analyzer)
    public decimal WeightObi { get; set; }
    public decimal WeightWobi { get; set; }
    public decimal WeightPressureRoc { get; set; }
    public decimal WeightSpread { get; set; }
    public decimal WeightLargeOrder { get; set; }
    public decimal WeightTickMomentum { get; set; }
    public decimal WeightTrend { get; set; }
    public decimal WeightVwap { get; set; }
    public decimal WeightVolume { get; set; }
    public decimal WeightRsi { get; set; }

    // Learned thresholds
    public decimal MinScoreToBuy { get; set; } = 0.35m;
    public decimal MinScoreToSell { get; set; } = -0.35m;
    public decimal MinScoreToExit { get; set; } = 0.20m;
    public bool EnableBuy { get; set; } = true;
    public bool EnableSell { get; set; } = true;

    // Optimal trade parameters
    // Default 1200s (20 min) matches weighted training horizon:
    // 0.20×5min + 0.40×15min + 0.40×30min = ~19 min average outcome.
    // With 2× time gate cap → max 40 min hold. Winners with strong scores extend via adaptive gate.
    // Previously 3600s (60 min) — caused all trades to exit via TIME+WEAK because
    // score naturally decays over 60 min and no other exit mechanism could fire.
    public int OptimalHoldSeconds { get; set; } = 1200;
    public decimal StopLossAmount { get; set; } = 1.50m;

    // Walk-forward validation metrics (out-of-sample)
    public int ValidationSamples { get; set; }
    public decimal ValidationWinRate { get; set; }
    public decimal ValidationPnl { get; set; }
    public decimal TrainingPnl { get; set; }
    public bool UsedDefaultWeights { get; set; }

    // Per time-of-day adjustments (hour 9-16 ET)
    public Dictionary<int, HourlyAdjustment> HourlyAdjustments { get; set; } = new();

    // ── Day trading: composite scoring weights (learned by nightly trainer) ──
    /// <summary>Weight for bar-based setup strength in composite score. Default 0.50.</summary>
    public decimal WeightSetup { get; set; }
    /// <summary>Weight for L2 timing score in composite score. Default 0.30.</summary>
    public decimal WeightTiming { get; set; }
    /// <summary>Weight for context (news/fundamentals) in composite score. Default 0.20.</summary>
    public decimal WeightContext { get; set; }
    /// <summary>Optimal hold time for day trades. Default 3600s (1 hour).</summary>
    public int OptimalHoldSecondsDay { get; set; } = DayTradeConfig.DefaultHoldSeconds;
}

public class HourlyAdjustment
{
    public decimal ScoreMultiplier { get; set; } = 1.0m;
    public bool EnableTrading { get; set; } = true;
    public decimal WinRate { get; set; }
}
