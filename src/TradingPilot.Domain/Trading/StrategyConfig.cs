using System.Text.Json.Serialization;

namespace TradingPilot.Trading;

/// <summary>
/// AI-generated strategy configuration from nightly Bedrock Sonnet 4.6 optimization.
/// Contains conditional trading rules per symbol discovered from historical data patterns.
/// Consumed by StrategyRuleEvaluator at runtime as pure condition matching — no AI calls.
/// </summary>
public class StrategyConfig
{
    [JsonPropertyName("generatedAt")]
    public DateTime GeneratedAt { get; set; }

    [JsonPropertyName("generatedBy")]
    public string GeneratedBy { get; set; } = "bedrock-sonnet-4.6";

    [JsonPropertyName("lookbackDays")]
    public int LookbackDays { get; set; }

    [JsonPropertyName("symbols")]
    public Dictionary<string, SymbolStrategy> Symbols { get; set; } = new();

    [JsonPropertyName("globalRules")]
    public GlobalRules GlobalRules { get; set; } = new();
}

public class SymbolStrategy
{
    [JsonPropertyName("tickerId")]
    public long TickerId { get; set; }

    [JsonPropertyName("overallWinRate")]
    public decimal OverallWinRate { get; set; }

    [JsonPropertyName("rules")]
    public List<StrategyRule> Rules { get; set; } = new();

    [JsonPropertyName("disabledHours")]
    public List<int> DisabledHours { get; set; } = new();

    [JsonPropertyName("maxDailyTrades")]
    public int MaxDailyTrades { get; set; } = 20;

    [JsonPropertyName("maxPositionShares")]
    public int MaxPositionShares { get; set; } = 500;
}

public class StrategyRule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("hours")]
    public List<int> Hours { get; set; } = new();

    [JsonPropertyName("direction")]
    public string Direction { get; set; } = "BUY"; // "BUY" or "SELL"

    [JsonPropertyName("conditions")]
    public RuleConditions Conditions { get; set; } = new();

    [JsonPropertyName("confidence")]
    public decimal Confidence { get; set; }

    [JsonPropertyName("expectedPnlPer100Shares")]
    public decimal ExpectedPnlPer100Shares { get; set; }

    [JsonPropertyName("sampleSize")]
    public int SampleSize { get; set; }

    [JsonPropertyName("holdSeconds")]
    public int HoldSeconds { get; set; } = 60;

    [JsonPropertyName("stopLoss")]
    public decimal StopLoss { get; set; } = 0.30m;
}

public class RuleConditions
{
    // OBI thresholds
    [JsonPropertyName("minObi")]
    public decimal? MinObi { get; set; }

    [JsonPropertyName("maxObi")]
    public decimal? MaxObi { get; set; }

    // Imbalance velocity
    [JsonPropertyName("minImbalanceVelocity")]
    public decimal? MinImbalanceVelocity { get; set; }

    [JsonPropertyName("maxImbalanceVelocity")]
    public decimal? MaxImbalanceVelocity { get; set; }

    // L2 features
    [JsonPropertyName("minBidWallSize")]
    public decimal? MinBidWallSize { get; set; }

    [JsonPropertyName("minAskWallSize")]
    public decimal? MinAskWallSize { get; set; }

    [JsonPropertyName("minBookDepthRatio")]
    public decimal? MinBookDepthRatio { get; set; }

    [JsonPropertyName("maxBookDepthRatio")]
    public decimal? MaxBookDepthRatio { get; set; }

    [JsonPropertyName("minBidSweepCost")]
    public decimal? MinBidSweepCost { get; set; }

    [JsonPropertyName("minAskSweepCost")]
    public decimal? MinAskSweepCost { get; set; }

    [JsonPropertyName("minSpreadPercentile")]
    public decimal? MinSpreadPercentile { get; set; }

    [JsonPropertyName("maxSpreadPercentile")]
    public decimal? MaxSpreadPercentile { get; set; }

    // Trend / momentum
    [JsonPropertyName("trendDirection")]
    public int? TrendDirection { get; set; } // 1, -1, or null

    [JsonPropertyName("minTickMomentum")]
    public decimal? MinTickMomentum { get; set; }

    [JsonPropertyName("maxTickMomentum")]
    public decimal? MaxTickMomentum { get; set; }

    // RSI range
    [JsonPropertyName("rsiRange")]
    public decimal[]? RsiRange { get; set; } // [min, max]

    // Volume
    [JsonPropertyName("minVolumeRatio")]
    public decimal? MinVolumeRatio { get; set; }

    // VWAP
    [JsonPropertyName("aboveVwap")]
    public bool? AboveVwap { get; set; }
}

public class GlobalRules
{
    [JsonPropertyName("minConfidence")]
    public decimal MinConfidence { get; set; } = 0.55m;

    [JsonPropertyName("minSampleSize")]
    public int MinSampleSize { get; set; } = 20;

    [JsonPropertyName("commissionPerTrade")]
    public decimal CommissionPerTrade { get; set; } = 2.99m;
}
