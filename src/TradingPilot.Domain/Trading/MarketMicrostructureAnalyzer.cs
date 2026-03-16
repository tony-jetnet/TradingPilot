using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using TradingPilot.Symbols;

namespace TradingPilot.Trading;

/// <summary>
/// Analyzes L2 order book data in real time to generate buy/sell trading signals.
/// Combines multiple microstructure indicators: order book imbalance, weighted imbalance,
/// pressure rate of change, spread analysis, and large order detection.
/// </summary>
public class MarketMicrostructureAnalyzer
{
    private readonly L2BookCache _l2Cache;
    private readonly ILogger<MarketMicrostructureAnalyzer> _logger;

    private readonly ConcurrentDictionary<long, TickerAnalysisState> _state = new();

    // Indicator weights for composite score
    private const decimal WeightObi = 0.30m;
    private const decimal WeightWobi = 0.25m;
    private const decimal WeightPressureRoc = 0.20m;
    private const decimal WeightSpread = 0.15m;
    private const decimal WeightLargeOrder = 0.10m;

    // Signal thresholds
    private const decimal StrongBuyThreshold = 0.40m;
    private const decimal ModerateBuyThreshold = 0.20m;
    private const decimal WeakBuyThreshold = 0.10m;
    private const decimal StrongSellThreshold = -0.40m;
    private const decimal ModerateSellThreshold = -0.20m;
    private const decimal WeakSellThreshold = -0.10m;

    // Minimum interval between signals to avoid spam
    private static readonly TimeSpan MinSignalInterval = TimeSpan.FromSeconds(5);

    // Window sizes for rolling calculations
    private const int ShortWindow = 10;
    private const int LongWindow = 30;
    private const int HistorySize = 100;
    private const int TopLevels = 5;
    private const decimal LargeOrderMultiplier = 3.0m;

    public MarketMicrostructureAnalyzer(
        L2BookCache l2Cache,
        ILogger<MarketMicrostructureAnalyzer> logger)
    {
        _l2Cache = l2Cache;
        _logger = logger;
    }

    /// <summary>
    /// Analyze a new L2 book snapshot and optionally produce a trading signal.
    /// Called on every new snapshot; returns null if no actionable signal.
    /// </summary>
    public TradingSignal? AnalyzeSnapshot(long tickerId, string ticker, SymbolBookSnapshot snapshot)
    {
        var state = _state.GetOrAdd(tickerId, _ => new TickerAnalysisState());

        // Update rolling state
        UpdateState(state, snapshot);

        // Need minimum history to produce meaningful signals
        if (state.RecentImbalances.Count < ShortWindow)
            return null;

        // Compute individual indicators (all return values in [-1, +1])
        decimal obiScore = ComputeSmoothedObi(state);
        decimal wobiScore = ComputeWeightedObi(snapshot);
        decimal pressureRocScore = ComputePressureRoc(state);
        decimal spreadScore = ComputeSpreadSignal(state, snapshot);
        decimal largeOrderScore = ComputeLargeOrderSignal(snapshot, state);

        // Composite score
        decimal compositeScore =
            obiScore * WeightObi +
            wobiScore * WeightWobi +
            pressureRocScore * WeightPressureRoc +
            spreadScore * WeightSpread +
            largeOrderScore * WeightLargeOrder;

        // Determine signal type and strength
        var (signalType, strength) = ClassifySignal(compositeScore);

        // Hold signals are not emitted
        if (signalType == SignalType.Hold)
            return null;

        // Throttle: enforce minimum interval between signals
        var now = DateTime.UtcNow;
        if (state.LastSignalTime != default && (now - state.LastSignalTime) < MinSignalInterval)
            return null;

        var indicators = new Dictionary<string, decimal>
        {
            ["OBI"] = Math.Round(obiScore, 4),
            ["WOBI"] = Math.Round(wobiScore, 4),
            ["PressureROC"] = Math.Round(pressureRocScore, 4),
            ["SpreadSignal"] = Math.Round(spreadScore, 4),
            ["LargeOrderSignal"] = Math.Round(largeOrderScore, 4),
            ["CompositeScore"] = Math.Round(compositeScore, 4),
        };

        string reason = BuildReason(signalType, strength, indicators);

        var signal = new TradingSignal
        {
            TickerId = tickerId,
            Ticker = ticker,
            Timestamp = now,
            Type = signalType,
            Strength = strength,
            Price = snapshot.MidPrice,
            Reason = reason,
            Indicators = indicators,
        };

        state.LastSignal = signal;
        state.LastSignalTime = now;

        // Log strong signals at Warning level for visibility
        if (strength == SignalStrength.Strong)
        {
            _logger.LogWarning(
                "STRONG {SignalType} signal for {Ticker} (tickerId={TickerId}) at {Price:F4} | " +
                "Score={Score:F4} OBI={OBI:F4} WOBI={WOBI:F4} PressureROC={PROCI:F4} " +
                "Spread={Spread:F4} LargeOrder={LargeOrder:F4}",
                signalType, ticker, tickerId, snapshot.MidPrice,
                compositeScore, obiScore, wobiScore, pressureRocScore,
                spreadScore, largeOrderScore);
        }
        else
        {
            _logger.LogInformation(
                "{Strength} {SignalType} signal for {Ticker} at {Price:F4} | Score={Score:F4}",
                strength, signalType, ticker, snapshot.MidPrice, compositeScore);
        }

        return signal;
    }

    private static void UpdateState(TickerAnalysisState state, SymbolBookSnapshot snapshot)
    {
        EnqueueCapped(state.RecentImbalances, snapshot.Imbalance, HistorySize);
        EnqueueCapped(state.RecentSpreads, snapshot.Spread, HistorySize);
        EnqueueCapped(state.RecentMidPrices, snapshot.MidPrice, HistorySize);

        // Track average bid/ask sizes for large order detection
        decimal avgBidSize = snapshot.BidSizes.Length > 0 ? snapshot.BidSizes.Average() : 0;
        decimal avgAskSize = snapshot.AskSizes.Length > 0 ? snapshot.AskSizes.Average() : 0;
        decimal avgLevelSize = (avgBidSize + avgAskSize) / 2;
        EnqueueCapped(state.RecentAvgLevelSizes, avgLevelSize, HistorySize);
    }

    /// <summary>
    /// A. Order Book Imbalance smoothed over last 30 snapshots.
    /// </summary>
    private static decimal ComputeSmoothedObi(TickerAnalysisState state)
    {
        var recent = state.RecentImbalances.TakeLast(LongWindow);
        return recent.Any() ? recent.Average() : 0;
    }

    /// <summary>
    /// B. Weighted Order Book Imbalance: levels closer to mid price
    /// are weighted more heavily (1/distance weighting on top N levels).
    /// </summary>
    private static decimal ComputeWeightedObi(SymbolBookSnapshot snapshot)
    {
        if (snapshot.MidPrice <= 0) return 0;

        decimal weightedBid = 0, weightedAsk = 0;
        decimal totalWeight = 0;

        int bidLevels = Math.Min(TopLevels, snapshot.BidPrices.Length);
        for (int i = 0; i < bidLevels; i++)
        {
            decimal distance = snapshot.MidPrice - snapshot.BidPrices[i];
            if (distance <= 0) distance = 0.0001m; // avoid division by zero
            decimal weight = 1.0m / distance;
            weightedBid += snapshot.BidSizes[i] * weight;
            totalWeight += weight;
        }

        int askLevels = Math.Min(TopLevels, snapshot.AskPrices.Length);
        for (int i = 0; i < askLevels; i++)
        {
            decimal distance = snapshot.AskPrices[i] - snapshot.MidPrice;
            if (distance <= 0) distance = 0.0001m;
            decimal weight = 1.0m / distance;
            weightedAsk += snapshot.AskSizes[i] * weight;
            totalWeight += weight;
        }

        decimal total = weightedBid + weightedAsk;
        if (total == 0) return 0;

        return (weightedBid - weightedAsk) / total;
    }

    /// <summary>
    /// C. Book Pressure Rate of Change: compares short-term (10) average imbalance
    /// to long-term (30) average. Positive = accelerating buy pressure.
    /// </summary>
    private static decimal ComputePressureRoc(TickerAnalysisState state)
    {
        if (state.RecentImbalances.Count < LongWindow) return 0;

        var items = state.RecentImbalances.ToArray();
        decimal shortAvg = items.TakeLast(ShortWindow).Average();
        decimal longAvg = items.TakeLast(LongWindow).Average();

        // Difference is already in [-2, +2] range, normalize to [-1, +1]
        decimal roc = shortAvg - longAvg;
        return Math.Clamp(roc * 2, -1, 1);
    }

    /// <summary>
    /// D. Spread Analysis: narrow spread = bullish confidence, widening = bearish.
    /// Compares current spread to its percentile position in recent history.
    /// Returns negative if spread is unusually wide (uncertainty), positive if narrow.
    /// </summary>
    private static decimal ComputeSpreadSignal(TickerAnalysisState state, SymbolBookSnapshot snapshot)
    {
        if (state.RecentSpreads.Count < ShortWindow) return 0;

        var spreads = state.RecentSpreads.ToArray();
        decimal currentSpread = snapshot.Spread;
        int countBelow = spreads.Count(s => s < currentSpread);
        decimal percentile = (decimal)countBelow / spreads.Length;

        // percentile near 1.0 = spread is wider than most recent values = bearish
        // percentile near 0.0 = spread is tighter than most = bullish
        // Map to [-1, +1]: tight spread = +1, wide spread = -1
        return 1.0m - 2.0m * percentile;
    }

    /// <summary>
    /// E. Large Order Detection: scan bid/ask for size spikes > 3x the rolling
    /// average level size. Large bids = bullish, large asks = bearish.
    /// </summary>
    private static decimal ComputeLargeOrderSignal(SymbolBookSnapshot snapshot, TickerAnalysisState state)
    {
        if (state.RecentAvgLevelSizes.Count < ShortWindow) return 0;

        decimal rollingAvg = state.RecentAvgLevelSizes.Average();
        if (rollingAvg <= 0) return 0;

        decimal threshold = rollingAvg * LargeOrderMultiplier;

        int largeBids = 0, largeAsks = 0;
        foreach (var size in snapshot.BidSizes)
            if (size > threshold) largeBids++;
        foreach (var size in snapshot.AskSizes)
            if (size > threshold) largeAsks++;

        int total = largeBids + largeAsks;
        if (total == 0) return 0;

        // Normalize: all large bids = +1, all large asks = -1
        return (decimal)(largeBids - largeAsks) / total;
    }

    private static (SignalType Type, SignalStrength Strength) ClassifySignal(decimal score)
    {
        if (score >= StrongBuyThreshold) return (SignalType.Buy, SignalStrength.Strong);
        if (score >= ModerateBuyThreshold) return (SignalType.Buy, SignalStrength.Moderate);
        if (score >= WeakBuyThreshold) return (SignalType.Buy, SignalStrength.Weak);
        if (score <= StrongSellThreshold) return (SignalType.Sell, SignalStrength.Strong);
        if (score <= ModerateSellThreshold) return (SignalType.Sell, SignalStrength.Moderate);
        if (score <= WeakSellThreshold) return (SignalType.Sell, SignalStrength.Weak);
        return (SignalType.Hold, SignalStrength.Weak);
    }

    private static string BuildReason(
        SignalType type, SignalStrength strength,
        Dictionary<string, decimal> indicators)
    {
        var sb = new StringBuilder();
        sb.Append($"{strength} {type} signal (score={indicators["CompositeScore"]:F3}). ");

        // Highlight dominant contributors
        var dominant = indicators
            .Where(kv => kv.Key != "CompositeScore")
            .OrderByDescending(kv => Math.Abs(kv.Value))
            .Take(2);

        sb.Append("Drivers: ");
        sb.Append(string.Join(", ", dominant.Select(kv =>
            $"{kv.Key}={kv.Value:+0.000;-0.000}")));

        return sb.ToString();
    }

    private static void EnqueueCapped(Queue<decimal> queue, decimal value, int maxSize)
    {
        queue.Enqueue(value);
        while (queue.Count > maxSize)
            queue.Dequeue();
    }
}

internal class TickerAnalysisState
{
    public Queue<decimal> RecentImbalances { get; } = new();
    public Queue<decimal> RecentSpreads { get; } = new();
    public Queue<decimal> RecentMidPrices { get; } = new();
    public Queue<decimal> RecentAvgLevelSizes { get; } = new();
    public TradingSignal? LastSignal { get; set; }
    public DateTime LastSignalTime { get; set; }
}
