using Volo.Abp.Domain.Entities;

namespace TradingPilot.Trading;

public class TradingSignalRecord : Entity<Guid>
{
    public string SymbolId { get; set; } = null!;
    public long TickerId { get; set; }
    public DateTime Timestamp { get; set; }

    // Signal
    public SignalType Type { get; set; }
    public SignalStrength Strength { get; set; }
    public decimal Price { get; set; }
    public decimal Score { get; set; }
    public string Reason { get; set; } = "";

    // Individual indicator values for analysis
    public decimal ObiSmoothed { get; set; }
    public decimal Wobi { get; set; }
    public decimal PressureRoc { get; set; }
    public decimal SpreadSignal { get; set; }
    public decimal LargeOrderSignal { get; set; }

    // Market context at signal time
    public decimal Spread { get; set; }
    public decimal Imbalance { get; set; }
    public int BidLevels { get; set; }
    public int AskLevels { get; set; }

    // Technical indicators (from BarIndicatorCache at signal time)
    public decimal Ema9 { get; set; }
    public decimal Ema20 { get; set; }
    public decimal Rsi14 { get; set; }
    public decimal Vwap { get; set; }
    public decimal VolumeRatio { get; set; }

    // Tick metrics (from TickDataCache at signal time)
    public decimal TickMomentum { get; set; }

    // L2-derived features (from TickDataCache at signal time)
    public decimal BookDepthRatio { get; set; }
    public decimal BidWallSize { get; set; }
    public decimal AskWallSize { get; set; }
    public decimal BidSweepCost { get; set; }
    public decimal AskSweepCost { get; set; }
    public decimal ImbalanceVelocity { get; set; }
    public decimal SpreadPercentile { get; set; }

    // Verification fields (filled in later when we check if signal was correct)
    public decimal? PriceAfter1Min { get; set; }
    public decimal? PriceAfter5Min { get; set; }
    public bool? WasCorrect1Min { get; set; }
    public bool? WasCorrect5Min { get; set; }
    public decimal? PriceAfter15Min { get; set; }
    public decimal? PriceAfter30Min { get; set; }
    public bool? WasCorrect15Min { get; set; }
    public bool? WasCorrect30Min { get; set; }
    public DateTime? VerifiedAt { get; set; }

    // ── Day trading: signal source and setup context ──
    /// <summary>What generated this signal (L2Micro, BarSetup, AiRule, Composite).</summary>
    public byte? Source { get; set; }
    /// <summary>Setup type if bar-based (TrendFollow, VwapBounce, Breakout, Reversal).</summary>
    public byte? SignalSetupType { get; set; }

    // ── Day trading: composite score breakdown ──
    /// <summary>Bar-based setup strength [0, 1].</summary>
    public decimal? SetupScore { get; set; }
    /// <summary>L2 timing score [-1, +1].</summary>
    public decimal? TimingScore { get; set; }
    /// <summary>News/fundamental context score [-1, +1].</summary>
    public decimal? ContextScore { get; set; }

    // ── Day trading: higher-timeframe indicators at signal time ──
    public decimal? Ema50 { get; set; }
    public decimal? Ema20_5m { get; set; }
    public decimal? Ema50_5m { get; set; }
    public decimal? Rsi14_5m { get; set; }
    public int? TrendDirection_5m { get; set; }
    public decimal? Ema20_15m { get; set; }
    public decimal? Ema50_15m { get; set; }
    public decimal? Rsi14_15m { get; set; }
    public int? TrendDirection_15m { get; set; }
    /// <summary>Multi-TF trend alignment [-1, +1].</summary>
    public decimal? TrendStrength { get; set; }
    /// <summary>% distance from VWAP, clamped [-1, +1].</summary>
    public decimal? VwapDeviation { get; set; }
    /// <summary>Net institutional flow [-1, +1].</summary>
    public decimal? CapitalFlowScore { get; set; }
    /// <summary>Volume vs 20-day time-of-day average.</summary>
    public decimal? RelativeVolume { get; set; }

    // ── Day trading: news context at signal time ──
    /// <summary>Avg news sentiment at signal time [-1, +1].</summary>
    public decimal? NewsSentiment { get; set; }
    /// <summary>Was there a catalyst article today?</summary>
    public bool? HasCatalyst { get; set; }
    /// <summary>Catalyst type (EARNINGS, ANALYST, etc.) or null.</summary>
    public string? SignalCatalystType { get; set; }
    /// <summary>Number of news articles in last 2 hours.</summary>
    public int? NewsCount2Hr { get; set; }

    // ── Day trading: longer horizon verification ──
    public decimal? PriceAfter1Hr { get; set; }
    public decimal? PriceAfter2Hr { get; set; }
    public decimal? PriceAfter4Hr { get; set; }
    public bool? WasCorrect1Hr { get; set; }
    public bool? WasCorrect2Hr { get; set; }
    public bool? WasCorrect4Hr { get; set; }
}
