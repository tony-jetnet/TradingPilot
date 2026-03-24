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
}
