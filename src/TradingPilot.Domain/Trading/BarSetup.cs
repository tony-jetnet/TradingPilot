using Volo.Abp.Domain.Entities;

namespace TradingPilot.Trading;

/// <summary>
/// A detected day-trade setup persisted to DB for training, backtesting, and dashboard display.
/// Created by SignalOrchestrator when SetupDetector finds a valid setup.
/// Outcomes verified nightly by NightlyStrategyOptimizer.
/// </summary>
public class BarSetup : Entity<Guid>
{
    public BarSetup() : base(Guid.NewGuid()) { }

    public string SymbolId { get; set; } = null!;
    public long TickerId { get; set; }
    public DateTime Timestamp { get; set; }

    // ── Setup definition ──
    /// <summary>Setup type (TrendFollow, VwapBounce, Breakout, Reversal).</summary>
    public byte SetupType { get; set; }
    /// <summary>BUY=1, SELL=2 (matches SignalType enum).</summary>
    public byte Direction { get; set; }
    /// <summary>Quality score [0, 1].</summary>
    public decimal Strength { get; set; }
    public decimal EntryZoneLow { get; set; }
    public decimal EntryZoneHigh { get; set; }
    public decimal StopLevel { get; set; }
    public decimal TargetLevel { get; set; }
    public DateTime ExpiresAt { get; set; }

    // ── Price at detection ──
    public decimal Price { get; set; }

    // ── Indicators at detection (1m) ──
    public decimal Ema9 { get; set; }
    public decimal Ema20 { get; set; }
    public decimal Rsi14 { get; set; }
    public decimal Vwap { get; set; }
    public decimal Atr14 { get; set; }
    public decimal VolumeRatio { get; set; }
    public int TrendDirection { get; set; }

    // ── Indicators at detection (5m) ──
    public decimal Ema20_5m { get; set; }
    public decimal Ema50_5m { get; set; }
    public decimal Rsi14_5m { get; set; }
    public decimal Atr14_5m { get; set; }
    public int TrendDirection_5m { get; set; }

    // ── Indicators at detection (15m) ──
    public decimal Ema20_15m { get; set; }
    public decimal Ema50_15m { get; set; }
    public decimal Rsi14_15m { get; set; }
    public int TrendDirection_15m { get; set; }

    // ── Context at detection ──
    public decimal? CapitalFlowScore { get; set; }
    public decimal? NewsSentiment { get; set; }
    public bool HasCatalyst { get; set; }
    public string? CatalystType { get; set; }
    public int NewsCount2Hr { get; set; }

    // ── Outcome verification (filled by nightly job) ──
    public decimal? PriceAfter1Hr { get; set; }
    public decimal? PriceAfter2Hr { get; set; }
    public decimal? PriceAfter4Hr { get; set; }
    /// <summary>Best price in setup direction within 4 hours.</summary>
    public decimal? MaxFavorable { get; set; }
    /// <summary>Worst price against setup within 4 hours.</summary>
    public decimal? MaxAdverse { get; set; }
    public bool? WasCorrect1Hr { get; set; }
    public bool? WasCorrect2Hr { get; set; }
    public bool? WasCorrect4Hr { get; set; }
    /// <summary>Did L2 timing ever confirm during the setup's active window?</summary>
    public bool? WasTradeable { get; set; }
    public DateTime? VerifiedAt { get; set; }
}
