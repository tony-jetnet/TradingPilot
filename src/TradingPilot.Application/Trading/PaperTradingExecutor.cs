using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingPilot.EntityFrameworkCore;
using TradingPilot.Symbols;
using TradingPilot.Trading;

namespace TradingPilot.Trading;

/// <summary>
/// Trading executor that uses IBrokerClient as source of truth for order fills and positions.
/// Orders are tracked as pending until the broker confirms fill. Position state is reconciled
/// with the broker account every 30s. Exit logic is handled by PositionMonitor (continuous) —
/// OnSignalAsync only handles entries.
/// </summary>
public class PaperTradingExecutor
{
    // Configuration (from IConfiguration)
    private readonly decimal _maxPositionDollars;
    private readonly int _maxConcurrentPositions;
    private readonly decimal _dailyLossLimit;
    private readonly decimal _dailyPnlStopLoss;    // daily realized P&L floor (e.g. -500)
    private readonly decimal _dailyPnlStopProfit;   // daily realized P&L ceiling (e.g. 500)
    private readonly int _rateLimitSeconds;
    private readonly int _orderTimeoutSeconds;

    // Momentum as % of mid price — adaptive to any price level
    private const decimal MomentumThresholdPct = 0.0005m; // 0.05% of mid price

    private readonly IBrokerClient _broker;
    private readonly SignalStore _signalStore;
    private readonly L2BookCache _l2Cache;
    private readonly BarIndicatorCache _barCache;
    private readonly MarketMicrostructureAnalyzer _analyzer;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PaperTradingExecutor> _logger;

    // State: keyed by Symbol (ticker name)
    private readonly ConcurrentDictionary<string, PendingOrder> _pendingEntries = new();
    private readonly ConcurrentDictionary<string, PositionState> _positions = new();
    private readonly ConcurrentDictionary<string, PendingOrder> _pendingExits = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastTradeTime = new();
    // Track last trade outcome per symbol — used for loss cooldown (30 min after a losing trade)
    private readonly ConcurrentDictionary<string, (DateTime ExitTime, decimal Pnl)> _lastTradeOutcome = new();
    private const int LossCooldownSeconds = 1800; // 30 minutes after a losing trade on same symbol

    // Cached broker account (5s TTL) — guarded by SemaphoreSlim for thread safety
    private BrokerAccount? _cachedAccount;
    private DateTime _accountCacheTime;
    private readonly SemaphoreSlim _accountLock = new(1, 1);

    public PaperTradingExecutor(
        IBrokerClient broker,
        SignalStore signalStore,
        L2BookCache l2Cache,
        BarIndicatorCache barCache,
        MarketMicrostructureAnalyzer analyzer,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<PaperTradingExecutor> logger)
    {
        _broker = broker;
        _signalStore = signalStore;
        _l2Cache = l2Cache;
        _barCache = barCache;
        _analyzer = analyzer;
        _scopeFactory = scopeFactory;
        _logger = logger;

        // Read config from the active broker's section (Broker or BrokerQuestrade)
        var brokerType = configuration.GetValue<string>("Broker:Type") ?? "WebullPaper";
        var section = brokerType == "Questrade" ? "BrokerQuestrade" : "Broker";

        _maxPositionDollars = configuration.GetValue<decimal>($"{section}:MaxPositionDollars", 25000m);
        _maxConcurrentPositions = configuration.GetValue<int>($"{section}:MaxConcurrentPositions", 3);
        _dailyLossLimit = configuration.GetValue<decimal>($"{section}:DailyLossLimit", -2000m);
        _dailyPnlStopLoss = configuration.GetValue<decimal>($"{section}:DailyPnlStopLoss", -500m);
        _dailyPnlStopProfit = configuration.GetValue<decimal>($"{section}:DailyPnlStopProfit", 500m);
        _rateLimitSeconds = configuration.GetValue<int>($"{section}:RateLimitSeconds", 300); // 5min cooldown
        _orderTimeoutSeconds = configuration.GetValue<int>($"{section}:OrderTimeoutSeconds", 90); // aggressive timeout for passive limits

        _logger.LogInformation("Trading executor using broker={Broker} section={Section}: maxPosition=${MaxPos} maxPositions={MaxCount} dailyLimit=${DailyLimit}",
            brokerType, section, _maxPositionDollars, _maxConcurrentPositions, _dailyLossLimit);
    }

    /// <summary>
    /// Expose open positions for PositionMonitor to evaluate.
    /// </summary>
    public IReadOnlyDictionary<string, PositionState> GetOpenPositions()
        => _positions;

    /// <summary>
    /// Get today's completed round-trip trades from DB for dashboard display.
    /// DISPLAY ONLY — not used for trading decisions.
    /// </summary>
    public List<CompletedTrade> GetCompletedTrades()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TradingPilotDbContext>();
            var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            var todayEt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, eastern).Date;
            var todayUtcStart = TimeZoneInfo.ConvertTimeToUtc(todayEt, eastern);

            return dbContext.CompletedTrades
                .Where(ct => ct.ExitTime >= todayUtcStart)
                .OrderByDescending(ct => ct.ExitTime)
                .Take(20)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load completed trades from DB");
            return [];
        }
    }

    /// <summary>
    /// Check if an exit order is already in flight for this symbol.
    /// </summary>
    public bool HasPendingExit(string symbol)
        => _pendingExits.ContainsKey(symbol);

    /// <summary>
    /// Get cached broker account (5s TTL).
    /// </summary>
    public async Task<BrokerAccount?> GetCachedAccountAsync()
    {
        if (_cachedAccount != null && (DateTime.UtcNow - _accountCacheTime).TotalSeconds < 5)
            return _cachedAccount;

        await _accountLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (_cachedAccount != null && (DateTime.UtcNow - _accountCacheTime).TotalSeconds < 5)
                return _cachedAccount;

            var account = await _broker.GetAccountAsync();
            if (account != null)
            {
                _cachedAccount = account;
                _accountCacheTime = DateTime.UtcNow;
            }
            return account;
        }
        finally
        {
            _accountLock.Release();
        }
    }

    /// <summary>
    /// Handle a new trading signal — entries only.
    /// Time stop, stop loss, score-based exits are handled by PositionMonitor.
    /// </summary>
    public async Task OnSignalAsync(TradingSignal signal)
    {
        // ═══════════════════════════════════════════════════════════
        // ENTRY GATING
        // ═══════════════════════════════════════════════════════════
        if (!_broker.IsAuthenticated) return;

        // ═══════════════════════════════════════════════════════════
        // OPPOSING SIGNAL EXIT: If we have a position and get a strong
        // signal in the opposite direction (|score| >= 0.40), exit.
        // Threshold 0.40 = "strong signal" throughout codebase, prevents whipsaw.
        // This implements the event-driven exit referenced in PositionMonitor comments.
        // ═══════════════════════════════════════════════════════════
        if (_positions.TryGetValue(signal.Ticker, out var existingPos))
        {
            decimal opposingScore = signal.Indicators.GetValueOrDefault("CompositeScore");
            bool isOpposing = (existingPos.IsLong && signal.Type == SignalType.Sell && opposingScore <= -0.40m)
                           || (!existingPos.IsLong && signal.Type == SignalType.Buy && opposingScore >= 0.40m);

            if (isOpposing && !HasPendingExit(signal.Ticker))
            {
                _logger.LogWarning("OPPOSING SIGNAL: {Symbol} has {Dir} position but got strong {Opposing} signal score={Score:F3}",
                    signal.Ticker, existingPos.IsLong ? "LONG" : "SHORT",
                    signal.Type, opposingScore);
                decimal exitPrice = signal.Price > 0 ? signal.Price : existingPos.EntryPrice;
                await ExitPositionAsync(signal.Ticker, exitPrice,
                    $"OPPOSING SIGNAL score={opposingScore:F3} vs entry={existingPos.EntryScore:F3}", opposingScore);
            }
            return; // Whether we triggered exit or not, don't enter while we have a position
        }

        if (_pendingEntries.ContainsKey(signal.Ticker)) return;

        // Check broker account for existing position and circuit breaker
        var cachedAccount = await GetCachedAccountAsync();
        if (cachedAccount == null) return;

        // If broker already has this symbol, skip
        if (cachedAccount.Positions.Any(p => p.Quantity != 0 && p.Symbol == signal.Ticker))
            return;

        // Circuit breaker: daily loss limit from broker account
        if (cachedAccount.DayPnl <= _dailyLossLimit)
        {
            _logger.LogDebug("Skipping entry for {Ticker}: daily loss limit hit ({DayPnl:F2})",
                signal.Ticker, cachedAccount.DayPnl);
            return;
        }

        // Daily P&L hard stops: stop if lost $500 or won $500
        if (cachedAccount.DayPnl <= _dailyPnlStopLoss)
        {
            _logger.LogInformation("{Symbol}: daily P&L stop-loss hit ({Pnl:F2} <= {Limit:F2}), no new trades", signal.Ticker, cachedAccount.DayPnl, _dailyPnlStopLoss);
            return;
        }
        if (cachedAccount.DayPnl >= _dailyPnlStopProfit)
        {
            _logger.LogInformation("{Symbol}: daily P&L profit target hit ({Pnl:F2} >= {Limit:F2}), no new trades", signal.Ticker, cachedAccount.DayPnl, _dailyPnlStopProfit);
            return;
        }

        // Position limit (include pending entries in count)
        if (_positions.Count + _pendingEntries.Count >= _maxConcurrentPositions)
        {
            _logger.LogDebug("Skipping entry for {Ticker}: max concurrent positions ({Max})",
                signal.Ticker, _maxConcurrentPositions);
            return;
        }

        // Rate limit per symbol (5 min base cooldown)
        if (_lastTradeTime.TryGetValue(signal.Ticker, out var lastTime)
            && (DateTime.UtcNow - lastTime).TotalSeconds < _rateLimitSeconds)
            return;

        // Loss cooldown: 30 min after a losing trade on same symbol.
        // Prevents repeatedly entering the same losing setup (e.g., SMCI shorted 4x into a rally).
        if (_lastTradeOutcome.TryGetValue(signal.Ticker, out var lastOutcome)
            && lastOutcome.Pnl <= 0
            && (DateTime.UtcNow - lastOutcome.ExitTime).TotalSeconds < LossCooldownSeconds)
        {
            _logger.LogDebug("Skipping {Ticker}: loss cooldown ({Pnl:F2} at {Time}, {Remaining}s remaining)",
                signal.Ticker, lastOutcome.Pnl, lastOutcome.ExitTime,
                LossCooldownSeconds - (int)(DateTime.UtcNow - lastOutcome.ExitTime).TotalSeconds);
            return;
        }

        decimal score = signal.Indicators.GetValueOrDefault("CompositeScore");

        // Check if signal came from AI rule (has per-rule parameters)
        bool isRuleSignal = signal.Indicators.ContainsKey("RuleConfidence");
        int ruleHoldSeconds = isRuleSignal ? (int)signal.Indicators.GetValueOrDefault("RuleHoldSeconds", 60) : 0;
        decimal ruleStopLoss = isRuleSignal ? signal.Indicators.GetValueOrDefault("RuleStopLoss", 0.30m) : 0;
        decimal ruleConfidence = isRuleSignal ? signal.Indicators.GetValueOrDefault("RuleConfidence", 0.55m) : 0;
        string? ruleId = null;

        // Read learned thresholds from model config (fall back to defaults)
        var tickerConfig = _analyzer.CurrentModelConfig?.Tickers.GetValueOrDefault(signal.TickerId);

        // Stage 2: use learned MinScoreToBuy from model_config.json (not hardcoded)
        decimal minScoreEntry = isRuleSignal ? 0.55m : (tickerConfig?.MinScoreToBuy ?? 0.35m);
        int entryHoldSeconds = isRuleSignal ? ruleHoldSeconds : (tickerConfig?.OptimalHoldSeconds ?? 60);
        decimal entryStopLoss = isRuleSignal ? ruleStopLoss : (tickerConfig?.StopLossAmount ?? 0.30m);

        // For rule-based signals, get rule ID and check per-symbol constraints
        if (isRuleSignal)
        {
            var strategyConfig = _analyzer.RuleEvaluator.CurrentConfig;
            if (strategyConfig != null &&
                strategyConfig.Symbols.TryGetValue(signal.Ticker, out var symbolStrategy))
            {
                ruleId = signal.Reason; // Contains rule ID in the reason string
            }
        }

        // Respect direction enablement from model config
        if (tickerConfig != null && !isRuleSignal)
        {
            if (signal.Type == SignalType.Buy && !tickerConfig.EnableBuy) return;
            if (signal.Type == SignalType.Sell && !tickerConfig.EnableSell) return;
        }

        // ═══════════════════════════════════════════════════════════
        // MARKET REGIME FILTER: Skip entry when microstructure is unfavorable.
        // Wide spreads = low liquidity / high uncertainty = poor fill quality.
        // ═══════════════════════════════════════════════════════════
        decimal spreadPctile = signal.Indicators.GetValueOrDefault("SpreadPercentile", 0.5m);
        if (spreadPctile >= 0.90m)
        {
            _logger.LogDebug("Skipping entry for {Ticker}: spread at {Pctile:P0} — unfavorable regime",
                signal.Ticker, spreadPctile);
            return;
        }

        // ═══════════════════════════════════════════════════════════
        // CHECK MOMENTUM: get price 30s ago from L2BookCache
        // Block entry if no valid momentum reading (cold ticker protection)
        // ═══════════════════════════════════════════════════════════
        var recentSnapshots = _l2Cache.GetSnapshots(signal.TickerId, 60);
        if (recentSnapshots.Count < 2)
        {
            _logger.LogDebug("Skipping entry for {Ticker}: insufficient L2 cache ({Count} snapshots)",
                signal.Ticker, recentSnapshots.Count);
            return;
        }

        var latestSnapshot = recentSnapshots[^1];
        // Find snapshot closest to 30 seconds ago within a wide 15-60s window.
        // Previous 25-40s window was too brittle — a 1-second data gap would block all entries.
        var olderSnapshot = recentSnapshots
            .Where(s =>
            {
                var age = (latestSnapshot.Timestamp - s.Timestamp).TotalSeconds;
                return age >= 15 && age <= 60;
            })
            .OrderBy(s => Math.Abs((latestSnapshot.Timestamp - s.Timestamp).TotalSeconds - 30))
            .FirstOrDefault();

        if (olderSnapshot == null)
        {
            _logger.LogDebug("Skipping entry for {Ticker}: no L2 snapshot in 15-60s window for momentum check",
                signal.Ticker);
            return;
        }

        decimal momentum = latestSnapshot.MidPrice - olderSnapshot.MidPrice;
        // Price-relative momentum threshold: scales with stock price
        decimal momentumThreshold = latestSnapshot.MidPrice > 0
            ? latestSnapshot.MidPrice * MomentumThresholdPct
            : 0.01m;

        // ═══════════════════════════════════════════════════════════
        // ENTRY: Strong signal + aligned with meaningful momentum
        // ═══════════════════════════════════════════════════════════
        bool isBuyEntry = signal.Type == SignalType.Buy && score >= minScoreEntry && momentum >= momentumThreshold;
        bool isSellEntry = signal.Type == SignalType.Sell && score <= -minScoreEntry && momentum <= -momentumThreshold;

        if (isBuyEntry || isSellEntry)
        {
            string action = isBuyEntry ? "BUY" : "SELL";

            // ═══════════════════════════════════════════════════════════
            // ATR-BASED POSITION SIZING: Normalize risk across symbols.
            // Higher ATR% → smaller position. Target: each position risks
            // roughly the same dollar amount of adverse move per ATR unit.
            // ═══════════════════════════════════════════════════════════
            decimal maxDollars = _maxPositionDollars;
            var strategyConfig = _analyzer.RuleEvaluator.CurrentConfig;
            if (strategyConfig != null &&
                strategyConfig.Symbols.TryGetValue(signal.Ticker, out var symStrategy))
            {
                maxDollars = symStrategy.MaxPositionDollars > 0
                    ? symStrategy.MaxPositionDollars
                    : _maxPositionDollars;
            }
            if (signal.Price <= 0)
            {
                _logger.LogWarning("[PaperTrader] Rejecting entry for {Ticker}: invalid price {Price}", signal.Ticker, signal.Price);
                return;
            }

            // ATR-based scaling: reduce position for volatile stocks
            var barIndicators = _barCache.GetIndicators(signal.TickerId);
            if (barIndicators != null && barIndicators.Atr14Pct > 0)
            {
                // Target ATR% baseline: 0.15% (typical for large-cap intraday 1-min bars).
                // If a stock's ATR% is 2x the baseline, halve the position.
                const decimal baselineAtrPct = 0.0015m;
                decimal atrScaleFactor = baselineAtrPct / barIndicators.Atr14Pct;
                // Clamp between 0.25x and 2.0x to avoid extreme sizing
                atrScaleFactor = Math.Clamp(atrScaleFactor, 0.25m, 2.0m);
                maxDollars *= atrScaleFactor;

                _logger.LogDebug(
                    "[PaperTrader] {Ticker}: ATR14={Atr:F4} ({AtrPct:P3}) scaleFactor={Scale:F2} maxDollars=${MaxDollars:F0}",
                    signal.Ticker, barIndicators.Atr14, barIndicators.Atr14Pct, atrScaleFactor, maxDollars);
            }

            // Scale position size by signal strength: weak signals get smaller positions
            decimal strengthFactor = Math.Clamp(Math.Abs(score) / 0.40m, 0.50m, 1.0m);
            maxDollars *= strengthFactor;

            int qty = Math.Max(1, (int)(maxDollars / signal.Price));

            // Use passive limit at favorable side of L2 book (bid for buys, ask for sells).
            // This saves the full spread on every fill and acts as a natural signal quality
            // filter: if the signal flips in 3 seconds, the order won't fill — only signals
            // where the market comes to us get executed. Add 10% of spread as offset to
            // improve fill probability slightly vs sitting exactly at BBO.
            decimal limitPrice;
            if (latestSnapshot.BidPrices.Length > 0 && latestSnapshot.AskPrices.Length > 0)
            {
                // Tiered pricing: strong signals cross more of the spread for better fills
                bool isStrongSignal = Math.Abs(score) >= 0.40m;
                decimal spreadOffset = isStrongSignal ? latestSnapshot.Spread * 0.50m : latestSnapshot.Spread * 0.10m;
                limitPrice = action == "BUY"
                    ? latestSnapshot.BidPrices[0] + spreadOffset   // just above best bid
                    : latestSnapshot.AskPrices[0] - spreadOffset;  // just below best ask
            }
            else
            {
                // Fallback: no L2 book data, use midprice with tight buffer
                limitPrice = action == "BUY" ? signal.Price * 1.0002m : signal.Price * 0.9998m;
            }
            limitPrice = Math.Round(limitPrice, 2);

            var result = await _broker.PlaceOrderAsync(new BrokerOrderRequest
            {
                Symbol = signal.Ticker,
                Action = action,
                Type = OrderType.Limit,
                LimitPrice = limitPrice,
                Quantity = qty,
                ExtendedHours = true,
                TimeInForce = "DAY",
            });

            if (result.Success && result.OrderId != null)
            {
                _lastTradeTime[signal.Ticker] = DateTime.UtcNow;
                // Invalidate cached account so next signal sees fresh state
                _cachedAccount = null;

                var pending = new PendingOrder
                {
                    Symbol = signal.Ticker,
                    TickerId = signal.TickerId,
                    OrderId = result.OrderId,
                    Action = action,
                    Quantity = qty,
                    LimitPrice = limitPrice,
                    PlacedAt = DateTime.UtcNow,
                    Purpose = OrderPurpose.Entry,
                    EntryScore = score,
                    EntryImbalance = latestSnapshot.Imbalance,
                    EntrySpread = latestSnapshot.Spread,
                    EntrySpreadPercentile = 0.5m,
                    EntryTrendDirection = (int)(signal.Indicators.GetValueOrDefault("TrendDir", 0)),
                    EntryRuleId = ruleId,
                    RuleConfidence = ruleConfidence,
                    HoldSeconds = entryHoldSeconds,
                    StopLoss = entryStopLoss,
                };

                // Atomic add — if another thread already added, skip (prevents duplicate)
                if (!_pendingEntries.TryAdd(signal.Ticker, pending))
                {
                    _logger.LogWarning("ENTRY ORDER PLACED but duplicate detected, cancelling: {Symbol} OrderId={OrderId}",
                        signal.Ticker, result.OrderId);
                    await _broker.CancelOrderAsync(result.OrderId);
                    return;
                }

                _logger.LogWarning("ENTRY ORDER PLACED: {Action} {Qty} {Symbol} @ ~{Price} | score={Score:F3} | OrderId={OrderId}",
                    action, qty, signal.Ticker, signal.Price, score, result.OrderId);
            }
            else
            {
                _logger.LogError("ENTRY ORDER FAILED: {Action} {Qty} {Symbol} | {Error}",
                    action, qty, signal.Ticker, result.Error);
            }
        }
    }

    /// <summary>
    /// Exit a position (called by PositionMonitor or signal-driven exit).
    /// Places an exit order via the broker; actual removal happens in VerifyPendingOrdersAsync on fill.
    /// </summary>
    public async Task ExitPositionAsync(string symbol, decimal currentPrice, string reason, decimal score)
    {
        // Exit already in flight — skip
        if (_pendingExits.ContainsKey(symbol))
            return;

        if (!_positions.TryGetValue(symbol, out var pos))
            return;

        string action = pos.IsLong ? "SELL" : "BUY";
        int qty = Math.Abs(pos.Shares);

        // Exit limit: use L2 book to place at best bid/ask instead of crossing the spread.
        // Exits are more urgent than entries, so place at the BBO directly (no offset).
        // Still saves ~half the spread vs the old 0.1% cross approach.
        decimal limitPrice;
        var exitSnapshot = _l2Cache.GetLatest(pos.TickerId);
        if (exitSnapshot != null && exitSnapshot.BidPrices.Length > 0 && exitSnapshot.AskPrices.Length > 0)
        {
            limitPrice = action == "BUY"
                ? exitSnapshot.AskPrices[0]   // exit short: buy at best ask
                : exitSnapshot.BidPrices[0];  // exit long: sell at best bid
        }
        else
        {
            limitPrice = action == "BUY" ? currentPrice * 1.001m : currentPrice * 0.999m;
        }
        limitPrice = Math.Round(limitPrice, 2);

        var result = await _broker.PlaceOrderAsync(new BrokerOrderRequest
        {
            Symbol = symbol,
            Action = action,
            Type = OrderType.Limit,
            LimitPrice = limitPrice,
            Quantity = qty,
            ExtendedHours = true,
            TimeInForce = "DAY",
        });

        if (result.Success && result.OrderId != null)
        {
            var pendingExit = new PendingOrder
            {
                Symbol = symbol,
                TickerId = pos.TickerId,
                OrderId = result.OrderId,
                Action = action,
                Quantity = qty,
                LimitPrice = limitPrice,
                PlacedAt = DateTime.UtcNow,
                Purpose = OrderPurpose.Exit,
                ExitReason = reason,
            };

            // Atomic add — if another thread already placed an exit, cancel this one
            if (!_pendingExits.TryAdd(symbol, pendingExit))
            {
                _logger.LogDebug("EXIT ORDER already pending for {Symbol}, cancelling duplicate OrderId={OrderId}",
                    symbol, result.OrderId);
                await _broker.CancelOrderAsync(result.OrderId);
                return;
            }

            // Update position reference (may be stale if sync removed it, but that's OK)
            if (_positions.TryGetValue(symbol, out var currentPos))
                currentPos.PendingExitOrderId = result.OrderId;

            _logger.LogWarning("EXIT ORDER PLACED: {Action} {Qty} {Symbol} @ ~{Price} | {Reason} | score={Score:F3} | OrderId={OrderId}",
                action, qty, symbol, currentPrice, reason, score, result.OrderId);
        }
        else
        {
            _logger.LogError("EXIT ORDER FAILED: {Action} {Qty} {Symbol} | {Error}",
                action, qty, symbol, result.Error);
        }
    }

    /// <summary>
    /// Verify pending entry and exit orders by checking broker fill status.
    /// Called by PositionMonitor every 5s.
    /// </summary>
    public async Task VerifyPendingOrdersAsync()
    {
        // ═══════════════════════════════════════════════════════════
        // PENDING ENTRIES
        // ═══════════════════════════════════════════════════════════
        foreach (var (symbol, pending) in _pendingEntries)
        {
            if (pending.OrderId == null) continue;

            // Give orders time to fill before checking
            if ((DateTime.UtcNow - pending.PlacedAt).TotalSeconds < 2) continue;

            try
            {
                var order = await _broker.GetOrderAsync(pending.OrderId);
                if (order == null) continue;

                if (order.Status is "Filled" or "PartiallyFilled")
                {
                    int filledQty = order.FilledQuantity > 0 ? order.FilledQuantity : pending.Quantity;
                    decimal filledPrice = order.FilledPrice ?? pending.LimitPrice;

                    if (order.Status == "PartiallyFilled")
                    {
                        // Partial fill: create position for filled shares, cancel remainder
                        if (filledQty <= 0) continue; // No shares actually filled yet
                        await _broker.CancelOrderAsync(pending.OrderId);
                        _logger.LogWarning("ENTRY PARTIAL FILL: {Action} {Filled}/{Total} {Symbol} @ {Price}, cancelled remainder | OrderId={OrderId}",
                            pending.Action, filledQty, pending.Quantity, symbol, filledPrice, pending.OrderId);
                    }
                    else
                    {
                        _logger.LogWarning("ENTRY CONFIRMED: {Action} {Qty} {Symbol} @ {FilledPrice} | OrderId={OrderId}",
                            pending.Action, filledQty, symbol, filledPrice, pending.OrderId);
                    }

                    var entryBar = _barCache.GetIndicators(pending.TickerId);
                    var pos = new PositionState
                    {
                        Symbol = pending.Symbol,
                        TickerId = pending.TickerId,
                        Shares = pending.Action == "BUY" ? filledQty : -filledQty,
                        EntryPrice = filledPrice,
                        EntryTime = order.FilledTime ?? DateTime.UtcNow,
                        EntryScore = pending.EntryScore,
                        PeakFavorableScore = pending.EntryScore,
                        PeakFavorablePrice = filledPrice,
                        PeakPriceSetAt = DateTime.UtcNow,
                        EntryImbalance = pending.EntryImbalance,
                        EntrySpread = pending.EntrySpread,
                        EntrySpreadPercentile = pending.EntrySpreadPercentile,
                        EntryTrendDirection = pending.EntryTrendDirection,
                        NotionalValue = filledPrice * filledQty,
                        EntryRuleId = pending.EntryRuleId,
                        RuleConfidence = pending.RuleConfidence,
                        HoldSeconds = pending.HoldSeconds,
                        StopLoss = pending.StopLoss,
                        EntryAtr = entryBar?.Atr14 ?? 0,
                    };
                    _positions[symbol] = pos;
                    _pendingEntries.TryRemove(symbol, out _);
                    _cachedAccount = null; // Invalidate so next signal check sees fresh state
                }
                else if (order.Status is "Cancelled" or "Rejected" or "Expired")
                {
                    _pendingEntries.TryRemove(symbol, out _);
                    _logger.LogWarning("ENTRY ORDER {Status}: {Symbol} OrderId={OrderId}", order.Status, symbol, pending.OrderId);
                }
                else if ((DateTime.UtcNow - pending.PlacedAt).TotalSeconds > _orderTimeoutSeconds)
                {
                    await _broker.CancelOrderAsync(pending.OrderId);
                    _pendingEntries.TryRemove(symbol, out _);
                    _logger.LogWarning("ENTRY ORDER TIMEOUT: {Symbol} OrderId={OrderId} (>{Timeout}s)", symbol, pending.OrderId, _orderTimeoutSeconds);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error verifying pending entry for {Symbol} OrderId={OrderId}", symbol, pending.OrderId);
            }
        }

        // ═══════════════════════════════════════════════════════════
        // PENDING EXITS
        // ═══════════════════════════════════════════════════════════
        foreach (var (symbol, pending) in _pendingExits)
        {
            if (pending.OrderId == null) continue;

            if ((DateTime.UtcNow - pending.PlacedAt).TotalSeconds < 2) continue;

            try
            {
                var order = await _broker.GetOrderAsync(pending.OrderId);
                if (order == null) continue;

                if (order.Status is "Filled" or "PartiallyFilled")
                {
                    int filledQty = order.FilledQuantity > 0 ? order.FilledQuantity : pending.Quantity;
                    decimal filledPrice = order.FilledPrice ?? pending.LimitPrice;

                    if (_positions.TryGetValue(symbol, out var pos))
                    {
                        decimal pnl = (pos.IsLong ? filledPrice - pos.EntryPrice : pos.EntryPrice - filledPrice) * filledQty;

                        // Record outcome for loss cooldown (30 min after losing trade on same symbol)
                        _lastTradeOutcome[symbol] = (DateTime.UtcNow, pnl);

                        // Record completed round-trip to DB for dashboard (DISPLAY ONLY — not used for trading)
                        try
                        {
                            string entrySource = !string.IsNullOrEmpty(pos.EntryRuleId) && pos.EntryRuleId != "RECOVERED" && pos.EntryRuleId != "STARTUP"
                                ? "RULE" : "WEIGHTED";
                            using var tradeScope = _scopeFactory.CreateScope();
                            var tradeDb = tradeScope.ServiceProvider.GetRequiredService<TradingPilotDbContext>();
                            tradeDb.CompletedTrades.Add(new CompletedTrade
                            {
                                Ticker = symbol,
                                TickerId = pos.TickerId,
                                IsLong = pos.IsLong,
                                Quantity = filledQty,
                                EntryPrice = pos.EntryPrice,
                                ExitPrice = filledPrice,
                                EntryTime = pos.EntryTime,
                                ExitTime = order.FilledTime ?? DateTime.UtcNow,
                                Pnl = pnl,
                                EntrySource = entrySource,
                                EntryScore = pos.EntryScore,
                                ExitReason = pending.ExitReason,
                            });
                            await tradeDb.SaveChangesAsync();
                        }
                        catch (Exception dbEx)
                        {
                            _logger.LogWarning(dbEx, "Failed to persist completed trade to DB (non-fatal)");
                        }

                        // Track live rule performance for auto-disabling losing rules
                        if (!string.IsNullOrEmpty(pos.EntryRuleId) && pos.EntryRuleId != "RECOVERED" && pos.EntryRuleId != "STARTUP")
                            _analyzer.RuleEvaluator.RecordTradeOutcome(pos.EntryRuleId, pnl);

                        if (order.Status == "PartiallyFilled")
                        {
                            // Partial exit: adjust position shares, cancel remainder, monitor will re-exit
                            if (filledQty <= 0) continue;
                            await _broker.CancelOrderAsync(pending.OrderId);
                            int remaining = Math.Abs(pos.Shares) - filledQty;
                            pos.Shares = pos.IsLong ? remaining : -remaining;
                            pos.PendingExitOrderId = null;
                            _pendingExits.TryRemove(symbol, out _);

                            if (remaining <= 0)
                            {
                                _positions.TryRemove(symbol, out _);
                                _logger.LogWarning("EXIT PARTIAL→FULL: {Action} {Qty} {Symbol} @ {Price} | P&L=${Pnl:F2} | OrderId={OrderId}",
                                    pending.Action, filledQty, symbol, filledPrice, pnl, pending.OrderId);
                            }
                            else
                            {
                                _logger.LogWarning("EXIT PARTIAL FILL: {Action} {Filled}/{Total} {Symbol} @ {Price} | P&L=${Pnl:F2} remaining={Remaining} — monitor will re-exit | OrderId={OrderId}",
                                    pending.Action, filledQty, pending.Quantity, symbol, filledPrice, pnl, remaining, pending.OrderId);
                            }
                        }
                        else
                        {
                            _logger.LogWarning("EXIT CONFIRMED: {Action} {Qty} {Symbol} @ {FilledPrice} | P&L=${Pnl:F2} | OrderId={OrderId}",
                                pending.Action, filledQty, symbol, filledPrice, pnl, pending.OrderId);
                            _positions.TryRemove(symbol, out _);
                            _pendingExits.TryRemove(symbol, out _);
                        }
                    }
                    else
                    {
                        // Position already removed (e.g. by broker sync), just clean up pending
                        _pendingExits.TryRemove(symbol, out _);
                    }
                    _cachedAccount = null; // Invalidate cache
                }
                else if (order.Status is "Cancelled" or "Rejected" or "Expired")
                {
                    _pendingExits.TryRemove(symbol, out _);
                    if (_positions.TryGetValue(symbol, out var pos))
                        pos.PendingExitOrderId = null;
                    _logger.LogWarning("EXIT ORDER {Status}: {Symbol} OrderId={OrderId} — monitor will re-trigger",
                        order.Status, symbol, pending.OrderId);
                }
                else if ((DateTime.UtcNow - pending.PlacedAt).TotalSeconds > 30)
                {
                    // Exit order escalation: cancel passive order and resubmit aggressively
                    await _broker.CancelOrderAsync(pending.OrderId);

                    var exitEscalationSnapshot = _l2Cache.GetLatest(pending.TickerId);
                    bool isLongExit = pending.Action == "SELL"; // selling = exiting a long
                    decimal currentMid = exitEscalationSnapshot?.MidPrice ?? pending.LimitPrice;
                    decimal urgencyBuffer = currentMid * 0.0005m; // cross spread by 0.05%

                    decimal aggressivePrice;
                    if (exitEscalationSnapshot != null && exitEscalationSnapshot.BidPrices.Length > 0 && exitEscalationSnapshot.AskPrices.Length > 0)
                    {
                        aggressivePrice = isLongExit
                            ? exitEscalationSnapshot.BidPrices[0] - urgencyBuffer   // sell below bid
                            : exitEscalationSnapshot.AskPrices[0] + urgencyBuffer;  // buy above ask
                    }
                    else
                    {
                        aggressivePrice = isLongExit
                            ? currentMid * 0.999m
                            : currentMid * 1.001m;
                    }
                    aggressivePrice = Math.Round(aggressivePrice, 2);

                    var escalationResult = await _broker.PlaceOrderAsync(new BrokerOrderRequest
                    {
                        Symbol = symbol,
                        Action = pending.Action,
                        Type = OrderType.Limit,
                        LimitPrice = aggressivePrice,
                        Quantity = pending.Quantity,
                        ExtendedHours = true,
                        TimeInForce = "DAY",
                    });

                    if (escalationResult.Success && escalationResult.OrderId != null)
                    {
                        pending.OrderId = escalationResult.OrderId;
                        pending.LimitPrice = aggressivePrice;
                        pending.PlacedAt = DateTime.UtcNow; // reset timer
                        if (_positions.TryGetValue(symbol, out var pos))
                            pos.PendingExitOrderId = escalationResult.OrderId;

                        _logger.LogWarning("EXIT ORDER ESCALATED: {Action} {Qty} {Symbol} @ {Price} (aggressive) | OrderId={OrderId}",
                            pending.Action, pending.Quantity, symbol, aggressivePrice, escalationResult.OrderId);
                    }
                    else
                    {
                        // Escalation failed — remove pending so monitor can retry
                        _pendingExits.TryRemove(symbol, out _);
                        if (_positions.TryGetValue(symbol, out var pos))
                            pos.PendingExitOrderId = null;
                        _logger.LogError("EXIT ORDER ESCALATION FAILED: {Symbol} | {Error}", symbol, escalationResult.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error verifying pending exit for {Symbol} OrderId={OrderId}", symbol, pending.OrderId);
            }
        }
    }

    /// <summary>
    /// Sync local state with broker account. Called by PositionMonitor every 30s.
    /// Adopts untracked broker positions, removes stale local positions, adjusts share counts.
    /// </summary>
    public async Task SyncWithBrokerAsync()
    {
        try
        {
            var account = await _broker.GetAccountAsync();
            if (account == null) return;

            // Cache account
            _cachedAccount = account;
            _accountCacheTime = DateTime.UtcNow;

            var brokerSymbols = account.Positions
                .Where(p => p.Quantity != 0)
                .ToDictionary(p => p.Symbol);

            // Adopt positions at broker but not locally tracked
            foreach (var (symbol, brokerPos) in brokerSymbols)
            {
                // If we have a pending entry for this symbol and broker already has it, the entry filled
                PendingOrder? pendingEntry = null;
                if (_pendingEntries.TryRemove(symbol, out var removed))
                    pendingEntry = removed;

                if (!_positions.ContainsKey(symbol))
                {
                    long tickerId = pendingEntry?.TickerId ?? _broker.ResolveInternalId(symbol);
                    var tickerConfig = _analyzer.CurrentModelConfig?.Tickers.GetValueOrDefault(tickerId);

                    var syncBar = _barCache.GetIndicators(tickerId);
                    // Preserve pending entry metadata if available (avoids losing hold/stop/score settings)
                    if (pendingEntry != null)
                    {
                        _positions[symbol] = new PositionState
                        {
                            Symbol = symbol,
                            TickerId = tickerId,
                            Shares = brokerPos.Quantity,
                            EntryPrice = brokerPos.AvgPrice,
                            EntryTime = DateTime.UtcNow,
                            EntryScore = pendingEntry.EntryScore,
                            PeakFavorableScore = pendingEntry.EntryScore,
                            PeakFavorablePrice = brokerPos.AvgPrice,
                            PeakPriceSetAt = DateTime.UtcNow,
                            EntryImbalance = pendingEntry.EntryImbalance,
                            EntrySpread = pendingEntry.EntrySpread,
                            EntrySpreadPercentile = pendingEntry.EntrySpreadPercentile,
                            EntryTrendDirection = pendingEntry.EntryTrendDirection,
                            NotionalValue = brokerPos.MarketValue,
                            EntryRuleId = pendingEntry.EntryRuleId,
                            RuleConfidence = pendingEntry.RuleConfidence,
                            HoldSeconds = pendingEntry.HoldSeconds,
                            StopLoss = pendingEntry.StopLoss,
                            EntryAtr = syncBar?.Atr14 ?? 0,
                        };
                        _logger.LogWarning("ADOPTED broker position (from pending entry): {Symbol} {Qty} shares @ {AvgPrice} | hold={Hold}s stop={Stop} ruleId={RuleId}",
                            symbol, brokerPos.Quantity, brokerPos.AvgPrice,
                            pendingEntry.HoldSeconds, pendingEntry.StopLoss, pendingEntry.EntryRuleId);
                    }
                    else
                    {
                        // Compute initial score so recovered positions have meaningful exit thresholds
                        decimal initialScore = _analyzer.ComputeCurrentScore(tickerId);
                        _positions[symbol] = new PositionState
                        {
                            Symbol = symbol,
                            TickerId = tickerId,
                            Shares = brokerPos.Quantity,
                            EntryPrice = brokerPos.AvgPrice,
                            EntryTime = DateTime.UtcNow,
                            EntryScore = initialScore,
                            PeakFavorableScore = initialScore,
                            PeakFavorablePrice = brokerPos.AvgPrice,
                            PeakPriceSetAt = DateTime.UtcNow,
                            NotionalValue = brokerPos.MarketValue,
                            EntryRuleId = "RECOVERED",
                            HoldSeconds = tickerConfig?.OptimalHoldSeconds ?? 120,
                            StopLoss = tickerConfig?.StopLossAmount ?? 0.50m,
                            EntryAtr = syncBar?.Atr14 ?? 0,
                        };
                        _logger.LogWarning("ADOPTED broker position: {Symbol} {Qty} shares @ {AvgPrice} (hold={Hold}s stop={Stop} score={Score:F3})",
                            symbol, brokerPos.Quantity, brokerPos.AvgPrice,
                            tickerConfig?.OptimalHoldSeconds ?? 120, tickerConfig?.StopLossAmount ?? 0.50m, initialScore);
                    }
                }
            }

            // Remove positions not at broker (and not pending exit)
            foreach (var symbol in _positions.Keys.ToList())
            {
                if (!brokerSymbols.ContainsKey(symbol) && !_pendingExits.ContainsKey(symbol))
                {
                    _positions.TryRemove(symbol, out _);
                    _logger.LogInformation("Position {Symbol} no longer at broker, removed", symbol);
                }
            }

            // Update share counts
            foreach (var (symbol, brokerPos) in brokerSymbols)
            {
                if (_positions.TryGetValue(symbol, out var pos) && pos.Shares != brokerPos.Quantity)
                {
                    _logger.LogInformation("Position {Symbol} shares adjusted: {Old} -> {New}",
                        symbol, pos.Shares, brokerPos.Quantity);
                    pos.Shares = brokerPos.Quantity;
                }
            }

            _logger.LogDebug("Broker sync: NetLiq={NetLiq:C} DayPnL={DayPnl:C} Positions={Count}",
                account.NetLiquidation, account.DayPnl, account.Positions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync with broker");
        }
    }

    /// <summary>
    /// Initialize position state from broker on startup. Called once.
    /// </summary>
    public async Task InitializeFromBrokerAsync()
    {
        try
        {
            var account = await _broker.GetAccountAsync();
            if (account == null)
            {
                _logger.LogWarning("Startup: broker not available, no positions adopted");
                return;
            }

            _cachedAccount = account;
            _accountCacheTime = DateTime.UtcNow;

            int adopted = 0;
            foreach (var brokerPos in account.Positions.Where(p => p.Quantity != 0))
            {
                long tickerId = _broker.ResolveInternalId(brokerPos.Symbol);
                var tickerConfig = _analyzer.CurrentModelConfig?.Tickers.GetValueOrDefault(tickerId);
                // Compute current score so recovered positions have meaningful exit thresholds
                decimal initialScore = _analyzer.ComputeCurrentScore(tickerId);
                var startupBar = _barCache.GetIndicators(tickerId);
                _positions[brokerPos.Symbol] = new PositionState
                {
                    Symbol = brokerPos.Symbol,
                    TickerId = tickerId,
                    Shares = brokerPos.Quantity,
                    EntryPrice = brokerPos.AvgPrice,
                    EntryTime = DateTime.UtcNow,
                    EntryScore = initialScore,
                    PeakFavorableScore = initialScore,
                    PeakFavorablePrice = brokerPos.AvgPrice,
                    PeakPriceSetAt = DateTime.UtcNow,
                    NotionalValue = brokerPos.MarketValue,
                    EntryRuleId = "STARTUP",
                    HoldSeconds = tickerConfig?.OptimalHoldSeconds ?? 120,
                    StopLoss = tickerConfig?.StopLossAmount ?? 0.50m,
                    EntryAtr = startupBar?.Atr14 ?? 0,
                };
                adopted++;
            }

            _logger.LogWarning("Startup: adopted {Count} positions from broker", adopted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize positions from broker");
        }
    }
}
