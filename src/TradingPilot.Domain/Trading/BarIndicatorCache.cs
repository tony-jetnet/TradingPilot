using System.Collections.Concurrent;

namespace TradingPilot.Trading;

/// <summary>
/// In-memory cache for pre-computed technical indicators derived from historical bar data.
/// Refreshed periodically (every 30 seconds) by BarIndicatorService.
/// </summary>
public class BarIndicatorCache
{
    private readonly ConcurrentDictionary<long, BarIndicators> _indicators = new();

    public void Update(long tickerId, BarIndicators indicators)
    {
        _indicators[tickerId] = indicators;
    }

    public BarIndicators? GetIndicators(long tickerId)
    {
        return _indicators.TryGetValue(tickerId, out var indicators) ? indicators : null;
    }
}

public class BarIndicators
{
    // From 1-min bars
    /// <summary>9-period Exponential Moving Average of close prices.</summary>
    public decimal Ema9 { get; set; }
    /// <summary>20-period Exponential Moving Average of close prices.</summary>
    public decimal Ema20 { get; set; }
    /// <summary>Volume-Weighted Average Price (intraday, resets at market open).</summary>
    public decimal Vwap { get; set; }
    /// <summary>14-period Relative Strength Index.</summary>
    public decimal Rsi14 { get; set; }
    /// <summary>20-bar average volume.</summary>
    public decimal AvgVolume20 { get; set; }
    /// <summary>Most recent bar volume.</summary>
    public decimal CurrentVolume { get; set; }
    /// <summary>CurrentVolume / AvgVolume20 ratio.</summary>
    public decimal VolumeRatio { get; set; }

    // Volatility
    /// <summary>14-period Average True Range (from 1-min bars). Used for position sizing.</summary>
    public decimal Atr14 { get; set; }
    /// <summary>ATR14 as a percentage of the last close price. Used for volatility-normalized sizing.</summary>
    public decimal Atr14Pct { get; set; }

    // Trend signals
    /// <summary>+1 = bullish (EMA9 > EMA20), -1 = bearish, 0 = neutral.</summary>
    public int TrendDirection { get; set; }
    /// <summary>True if last close is above VWAP.</summary>
    public bool AboveVwap { get; set; }
    /// <summary>True if RSI > 70.</summary>
    public bool OverboughtRsi { get; set; }
    /// <summary>True if RSI &lt; 30.</summary>
    public bool OversoldRsi { get; set; }
    /// <summary>True if VolumeRatio > 1.5.</summary>
    public bool HighVolume { get; set; }

    public DateTime ComputedAt { get; set; }

    /// <summary>
    /// UTC time when this snapshot was last refreshed from the database.
    /// Used by MarketMicrostructureAnalyzer to detect stale bar data and skip signal generation.
    /// </summary>
    public DateTime LastRefreshTime { get; set; }
}
