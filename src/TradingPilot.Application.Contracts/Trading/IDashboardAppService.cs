using Volo.Abp.Application.Services;

namespace TradingPilot.Trading;

public interface IDashboardAppService : IApplicationService
{
    Task<DashboardDto> GetAsync();
}

public class DashboardDto
{
    public List<SymbolLiveDto> Symbols { get; set; } = new();
    public List<PositionDto> OpenPositions { get; set; } = new();
    public List<TradeDto> RecentTrades { get; set; } = new();
    public List<SignalDto> RecentSignals { get; set; } = new();
    public PnlSummaryDto PnlSummary { get; set; } = new();
    public StrategyStatusDto StrategyStatus { get; set; } = new();
    public string HookStatus { get; set; } = "Unknown";
    public StreamingHealthDto StreamingHealth { get; set; } = new();
    public List<SymbolHealthDto> SymbolHealth { get; set; } = new();
}

public class PositionDto
{
    public string Ticker { get; set; } = "";
    public string Side { get; set; } = ""; // Long / Short
    public int Quantity { get; set; }
    public decimal AvgPrice { get; set; }
    public decimal CurrentPrice { get; set; }
    public decimal MarketValue { get; set; }
    public decimal UnrealizedPnl { get; set; }
    public decimal UnrealizedPnlPercent { get; set; }
    public decimal EntryScore { get; set; }
    public int HoldSeconds { get; set; }
    public string? EntryRuleId { get; set; }
}

public class SymbolLiveDto
{
    public string Ticker { get; set; } = "";
    public long TickerId { get; set; }
    public decimal Price { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public long Volume { get; set; }
    public decimal Change { get; set; }
    public decimal ChangePercent { get; set; }
    public decimal Ema9 { get; set; }
    public decimal Ema20 { get; set; }
    public decimal Rsi14 { get; set; }
    public decimal Vwap { get; set; }
    public decimal TickMomentum { get; set; }
    public decimal BookDepthRatio { get; set; }
    public decimal BidWallSize { get; set; }
    public decimal AskWallSize { get; set; }
    public decimal ImbalanceVelocity { get; set; }
    public int SignalCount { get; set; }
    public string? LastSignalType { get; set; }
    public decimal LastSignalScore { get; set; }
    public int CurrentPosition { get; set; }
    public DateTime LastUpdate { get; set; }
}

public class TradeDto
{
    public string Ticker { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string Action { get; set; } = "";
    public int Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal Score { get; set; }
    public string Reason { get; set; } = "";
    public string Source { get; set; } = ""; // RULE, SWIN, WEIGHTED
    public string? Status { get; set; }
}

public class SourcePnlDto
{
    public string Source { get; set; } = ""; // RULE, SWIN, WEIGHTED
    public int Trades { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public decimal WinRate { get; set; }
    public decimal TotalPnl { get; set; }
    public decimal AvgPnl { get; set; }
}

public class SignalDto
{
    public string Ticker { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string Type { get; set; } = "";
    public string Strength { get; set; } = "";
    public decimal Price { get; set; }
    public decimal Score { get; set; }
    public string Reason { get; set; } = "";
}

public class PnlSummaryDto
{
    public int TotalTrades { get; set; }
    public int WinningTrades { get; set; }
    public int LosingTrades { get; set; }
    public decimal TotalPnl { get; set; }
    public decimal TotalCommissions { get; set; }
    public decimal NetPnl { get; set; }
    public decimal WinRate { get; set; }
    public decimal AvgWin { get; set; }
    public decimal AvgLoss { get; set; }
    public decimal BestTrade { get; set; }
    public decimal WorstTrade { get; set; }
    public int TodayTrades { get; set; }
    public decimal TodayPnl { get; set; }
    public List<SourcePnlDto> SourceBreakdown { get; set; } = new();
}

public class StrategyStatusDto
{
    public bool IsLoaded { get; set; }
    public DateTime? GeneratedAt { get; set; }
    public int SymbolCount { get; set; }
    public int TotalRules { get; set; }
    public List<SymbolRuleSummaryDto> SymbolRules { get; set; } = new();
}

public class SymbolRuleSummaryDto
{
    public string Ticker { get; set; } = "";
    public int RuleCount { get; set; }
    public decimal OverallWinRate { get; set; }
    public List<int> DisabledHours { get; set; } = new();
}

public class StreamingHealthDto
{
    public int TotalMqttMessages { get; set; }
    public int L2DepthMessages { get; set; }
    public int QuoteMessages { get; set; }
    public int TickMessages { get; set; }
    public int TopicPatterns { get; set; }
    public Dictionary<long, double> TickerStalenessSeconds { get; set; } = new();
}

public class SymbolHealthDto
{
    public string Ticker { get; set; } = "";
    public double L2AgeSec { get; set; }
    public double QuoteAgeSec { get; set; }
    public double TickSnapshotAgeSec { get; set; }
    public string Status { get; set; } = "Unknown"; // Live, Stale, Offline
}
