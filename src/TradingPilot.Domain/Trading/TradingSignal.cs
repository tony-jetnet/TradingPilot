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
}

public enum SignalType { Hold, Buy, Sell }

public enum SignalStrength { Weak, Moderate, Strong }
