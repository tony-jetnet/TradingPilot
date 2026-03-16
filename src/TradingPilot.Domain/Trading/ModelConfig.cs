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
    public int OptimalHoldSeconds { get; set; } = 60;
    public decimal StopLossAmount { get; set; } = 0.30m;

    // Per time-of-day adjustments (hour 9-16 ET)
    public Dictionary<int, HourlyAdjustment> HourlyAdjustments { get; set; } = new();
}

public class HourlyAdjustment
{
    public decimal ScoreMultiplier { get; set; } = 1.0m;
    public bool EnableTrading { get; set; } = true;
    public decimal WinRate { get; set; }
}
