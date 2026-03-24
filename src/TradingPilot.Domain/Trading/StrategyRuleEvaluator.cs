using System.Collections.Concurrent;

namespace TradingPilot.Trading;

/// <summary>
/// Pure math: evaluates AI-generated strategy rules against current market indicators.
/// No AI calls — just condition matching. Used in real-time by MarketMicrostructureAnalyzer.
/// Also tracks live performance per rule to auto-disable losing rules.
/// </summary>
public class StrategyRuleEvaluator
{
    private volatile StrategyConfig? _config;
    private DateTime _configLoadedAt;

    // Live performance tracking: auto-disable rules losing money in real-time
    private readonly ConcurrentDictionary<string, RuleLivePerformance> _livePerformance = new();

    public StrategyConfig? CurrentConfig => _config;

    public void SetConfig(StrategyConfig? config)
    {
        _config = config;
        _configLoadedAt = DateTime.UtcNow;
        // Reset live performance when new rules are loaded (nightly refresh)
        _livePerformance.Clear();
    }

    /// <summary>
    /// Record the outcome of a closed trade that was triggered by a rule.
    /// Called by PaperTradingExecutor when an exit fill is confirmed.
    /// </summary>
    public void RecordTradeOutcome(string ruleId, decimal pnl)
    {
        var perf = _livePerformance.GetOrAdd(ruleId, _ => new RuleLivePerformance());
        lock (perf)
        {
            if (pnl > 0) perf.Wins++;
            else perf.Losses++;
            perf.TotalPnl += pnl;
        }
    }

    /// <summary>
    /// Check if a rule should be disabled due to negative live P&amp;L.
    /// Requires at least 3 trades before disabling to avoid premature judgment.
    /// </summary>
    public bool IsRuleDisabledByLivePerformance(string ruleId)
    {
        if (!_livePerformance.TryGetValue(ruleId, out var perf))
            return false;

        // Need at least 3 trades before making a judgment
        if (perf.TotalTrades < 3) return false;

        // Disable if total P&L is negative after minimum sample
        return perf.TotalPnl < 0;
    }

    /// <summary>
    /// Get live performance stats for all tracked rules (for dashboard display).
    /// </summary>
    public IReadOnlyDictionary<string, RuleLivePerformance> GetLivePerformance()
        => _livePerformance;

    /// <summary>
    /// Find the best matching rule for the given ticker at the current hour with current indicators.
    /// Returns null if no rule matches (caller should fall back to default scoring).
    /// </summary>
    public (StrategyRule Rule, SymbolStrategy Symbol)? FindMatchingRule(
        long tickerId, string ticker, int etHour, IndicatorSnapshot indicators)
    {
        var config = _config;
        if (config == null) return null;

        // Look up by ticker symbol
        if (!config.Symbols.TryGetValue(ticker, out var symbolStrategy))
            return null;

        if (symbolStrategy.TickerId != tickerId)
            return null;

        // Check disabled hours
        if (symbolStrategy.DisabledHours.Contains(etHour))
            return null;

        StrategyRule? bestRule = null;

        foreach (var rule in symbolStrategy.Rules)
        {
            // Check hour filter
            if (rule.Hours.Count > 0 && !rule.Hours.Contains(etHour))
                continue;

            // Check minimum confidence
            if (rule.Confidence < config.GlobalRules.MinConfidence)
                continue;

            // Check minimum sample size
            if (rule.SampleSize < config.GlobalRules.MinSampleSize)
                continue;

            // Quality gate: skip rules that aren't worth trading
            if (!IsRuleTradeworthy(rule))
                continue;

            // Skip rules disabled by live performance tracking
            if (IsRuleDisabledByLivePerformance(rule.Id))
                continue;

            // Evaluate all conditions
            if (!EvaluateConditions(rule.Conditions, indicators))
                continue;

            // Pick highest confidence matching rule
            if (bestRule == null || rule.Confidence > bestRule.Confidence)
                bestRule = rule;
        }

        return bestRule != null ? (bestRule, symbolStrategy) : null;
    }

    /// <summary>
    /// Quality gate: reject rules that are not worth trading.
    /// Filters out low-confidence, negative expected PnL, or insufficient sample size rules.
    /// </summary>
    public static bool IsRuleTradeworthy(StrategyRule rule)
    {
        if (rule.Confidence < 0.55m) return false;
        if (rule.ExpectedPnlPer100Shares <= 0) return false;
        if (rule.SampleSize < 30) return false;
        return true;
    }

    private static bool EvaluateConditions(RuleConditions c, IndicatorSnapshot ind)
    {
        if (c.MinObi.HasValue && ind.Obi < c.MinObi.Value) return false;
        if (c.MaxObi.HasValue && ind.Obi > c.MaxObi.Value) return false;

        if (c.MinImbalanceVelocity.HasValue && ind.ImbalanceVelocity < c.MinImbalanceVelocity.Value) return false;
        if (c.MaxImbalanceVelocity.HasValue && ind.ImbalanceVelocity > c.MaxImbalanceVelocity.Value) return false;

        if (c.MinBidWallSize.HasValue && ind.BidWallSize < c.MinBidWallSize.Value) return false;
        if (c.MinAskWallSize.HasValue && ind.AskWallSize < c.MinAskWallSize.Value) return false;

        if (c.MinBookDepthRatio.HasValue && ind.BookDepthRatio < c.MinBookDepthRatio.Value) return false;
        if (c.MaxBookDepthRatio.HasValue && ind.BookDepthRatio > c.MaxBookDepthRatio.Value) return false;

        if (c.MinBidSweepCost.HasValue && ind.BidSweepCost < c.MinBidSweepCost.Value) return false;
        if (c.MinAskSweepCost.HasValue && ind.AskSweepCost < c.MinAskSweepCost.Value) return false;

        if (c.MinSpreadPercentile.HasValue && ind.SpreadPercentile < c.MinSpreadPercentile.Value) return false;
        if (c.MaxSpreadPercentile.HasValue && ind.SpreadPercentile > c.MaxSpreadPercentile.Value) return false;

        if (c.TrendDirection.HasValue && ind.TrendDirection != c.TrendDirection.Value) return false;

        if (c.MinTickMomentum.HasValue && ind.TickMomentum < c.MinTickMomentum.Value) return false;
        if (c.MaxTickMomentum.HasValue && ind.TickMomentum > c.MaxTickMomentum.Value) return false;

        if (c.RsiRange is { Length: 2 })
        {
            if (c.RsiRange[0].HasValue && ind.Rsi14 < c.RsiRange[0].Value) return false;
            if (c.RsiRange[1].HasValue && ind.Rsi14 > c.RsiRange[1].Value) return false;
        }

        if (c.MinVolumeRatio.HasValue && ind.VolumeRatio < c.MinVolumeRatio.Value) return false;

        if (c.AboveVwap.HasValue && ind.AboveVwap != c.AboveVwap.Value) return false;

        return true;
    }
}

/// <summary>
/// Current indicator values at the moment of evaluation.
/// Populated from TickDataCache, BarIndicatorCache, and L2 features.
/// </summary>
public class IndicatorSnapshot
{
    // L2-derived features
    public decimal Obi { get; set; }
    public decimal BookDepthRatio { get; set; }
    public decimal BidWallSize { get; set; }
    public decimal AskWallSize { get; set; }
    public decimal BidSweepCost { get; set; }
    public decimal AskSweepCost { get; set; }
    public decimal ImbalanceVelocity { get; set; }
    public decimal SpreadPercentile { get; set; }

    // Tick-derived
    public decimal TickMomentum { get; set; }

    // Bar-derived
    public int TrendDirection { get; set; }
    public decimal Rsi14 { get; set; }
    public decimal VolumeRatio { get; set; }
    public bool AboveVwap { get; set; }
}

/// <summary>
/// Tracks live trading performance for a specific AI rule within the current trading day.
/// Reset when new strategy_rules.json is loaded (nightly).
/// </summary>
public class RuleLivePerformance
{
    public int Wins { get; set; }
    public int Losses { get; set; }
    public decimal TotalPnl { get; set; }
    public int TotalTrades => Wins + Losses;
    public decimal WinRate => TotalTrades > 0 ? (decimal)Wins / TotalTrades : 0;
}
