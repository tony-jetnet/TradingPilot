using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
    private readonly int _rateLimitSeconds;
    private readonly int _orderTimeoutMinutes;

    // Momentum as % of mid price — adaptive to any price level
    private const decimal MomentumThresholdPct = 0.0001m; // 0.01% of mid price

    private readonly IBrokerClient _broker;
    private readonly SignalStore _signalStore;
    private readonly L2BookCache _l2Cache;
    private readonly MarketMicrostructureAnalyzer _analyzer;
    private readonly ILogger<PaperTradingExecutor> _logger;

    // State: keyed by Symbol (ticker name)
    private readonly ConcurrentDictionary<string, PendingOrder> _pendingEntries = new();
    private readonly ConcurrentDictionary<string, PositionState> _positions = new();
    private readonly ConcurrentDictionary<string, PendingOrder> _pendingExits = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastTradeTime = new();
    // Daily trade counter per symbol (reset on date change)
    private readonly ConcurrentDictionary<string, int> _dailyTradeCount = new();
    private DateOnly _dailyTradeDate = DateOnly.FromDateTime(DateTime.UtcNow);

    // Cached broker account (5s TTL) — guarded by SemaphoreSlim for thread safety
    private BrokerAccount? _cachedAccount;
    private DateTime _accountCacheTime;
    private readonly SemaphoreSlim _accountLock = new(1, 1);

    public PaperTradingExecutor(
        IBrokerClient broker,
        SignalStore signalStore,
        L2BookCache l2Cache,
        MarketMicrostructureAnalyzer analyzer,
        IConfiguration configuration,
        ILogger<PaperTradingExecutor> logger)
    {
        _broker = broker;
        _signalStore = signalStore;
        _l2Cache = l2Cache;
        _analyzer = analyzer;
        _logger = logger;

        // Read config from the active broker's section (Broker or BrokerQuestrade)
        var brokerType = configuration.GetValue<string>("Broker:Type") ?? "WebullPaper";
        var section = brokerType == "Questrade" ? "BrokerQuestrade" : "Broker";

        _maxPositionDollars = configuration.GetValue<decimal>($"{section}:MaxPositionDollars", 25000m);
        _maxConcurrentPositions = configuration.GetValue<int>($"{section}:MaxConcurrentPositions", 3);
        _dailyLossLimit = configuration.GetValue<decimal>($"{section}:DailyLossLimit", -2000m);
        _rateLimitSeconds = configuration.GetValue<int>($"{section}:RateLimitSeconds", 90);
        _orderTimeoutMinutes = configuration.GetValue<int>($"{section}:OrderTimeoutMinutes", 5);

        _logger.LogInformation("Trading executor using broker={Broker} section={Section}: maxPosition=${MaxPos} maxPositions={MaxCount} dailyLimit=${DailyLimit}",
            brokerType, section, _maxPositionDollars, _maxConcurrentPositions, _dailyLossLimit);
    }

    /// <summary>
    /// Expose open positions for PositionMonitor to evaluate.
    /// </summary>
    public IReadOnlyDictionary<string, PositionState> GetOpenPositions()
        => _positions;

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

        if (_positions.ContainsKey(signal.Ticker)) return;

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

        // Position limit (include pending entries in count)
        if (_positions.Count + _pendingEntries.Count >= _maxConcurrentPositions)
        {
            _logger.LogDebug("Skipping entry for {Ticker}: max concurrent positions ({Max})",
                signal.Ticker, _maxConcurrentPositions);
            return;
        }

        // Rate limit per symbol
        if (_lastTradeTime.TryGetValue(signal.Ticker, out var lastTime)
            && (DateTime.UtcNow - lastTime).TotalSeconds < _rateLimitSeconds)
            return;

        // Daily trade count limit per symbol (from strategy config)
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (today != _dailyTradeDate)
        {
            _dailyTradeCount.Clear();
            _dailyTradeDate = today;
        }
        {
            var stratCfg = _analyzer.RuleEvaluator.CurrentConfig;
            if (stratCfg != null && stratCfg.Symbols.TryGetValue(signal.Ticker, out var symCfg))
            {
                int count = _dailyTradeCount.GetValueOrDefault(signal.Ticker, 0);
                if (count >= symCfg.MaxDailyTrades)
                {
                    _logger.LogDebug("Skipping entry for {Ticker}: daily trade limit ({Max}) reached",
                        signal.Ticker, symCfg.MaxDailyTrades);
                    return;
                }
            }
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
        var olderSnapshot = recentSnapshots.FirstOrDefault(s =>
            (latestSnapshot.Timestamp - s.Timestamp).TotalSeconds >= 25 &&
            (latestSnapshot.Timestamp - s.Timestamp).TotalSeconds <= 40);

        if (olderSnapshot == null)
        {
            _logger.LogDebug("Skipping entry for {Ticker}: no L2 snapshot in 25-40s window for momentum check",
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

            // Dollar-based position sizing
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
            int qty = Math.Max(1, (int)(maxDollars / signal.Price));

            // Use limit order at current price (market orders don't work in extended hours)
            // Add small buffer: pay slightly more for buys, accept slightly less for sells
            decimal limitPrice = action == "BUY" ? signal.Price * 1.001m : signal.Price * 0.999m;
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
                _dailyTradeCount.AddOrUpdate(signal.Ticker, 1, (_, count) => count + 1);
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

        decimal limitPrice = action == "BUY" ? currentPrice * 1.001m : currentPrice * 0.999m;
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
                        EntryImbalance = pending.EntryImbalance,
                        EntrySpread = pending.EntrySpread,
                        EntrySpreadPercentile = pending.EntrySpreadPercentile,
                        EntryTrendDirection = pending.EntryTrendDirection,
                        NotionalValue = filledPrice * filledQty,
                        EntryRuleId = pending.EntryRuleId,
                        RuleConfidence = pending.RuleConfidence,
                        HoldSeconds = pending.HoldSeconds,
                        StopLoss = pending.StopLoss,
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
                else if ((DateTime.UtcNow - pending.PlacedAt).TotalMinutes > _orderTimeoutMinutes)
                {
                    await _broker.CancelOrderAsync(pending.OrderId);
                    _pendingEntries.TryRemove(symbol, out _);
                    _logger.LogWarning("ENTRY ORDER TIMEOUT: {Symbol} OrderId={OrderId} (>{Timeout}min)", symbol, pending.OrderId, _orderTimeoutMinutes);
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
                else if ((DateTime.UtcNow - pending.PlacedAt).TotalMinutes > _orderTimeoutMinutes)
                {
                    await _broker.CancelOrderAsync(pending.OrderId);
                    _pendingExits.TryRemove(symbol, out _);
                    if (_positions.TryGetValue(symbol, out var pos))
                        pos.PendingExitOrderId = null;
                    _logger.LogWarning("EXIT ORDER TIMEOUT: {Symbol} OrderId={OrderId} (>{Timeout}min)", symbol, pending.OrderId, _orderTimeoutMinutes);
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
                            EntryImbalance = pendingEntry.EntryImbalance,
                            EntrySpread = pendingEntry.EntrySpread,
                            EntrySpreadPercentile = pendingEntry.EntrySpreadPercentile,
                            EntryTrendDirection = pendingEntry.EntryTrendDirection,
                            NotionalValue = brokerPos.MarketValue,
                            EntryRuleId = pendingEntry.EntryRuleId,
                            RuleConfidence = pendingEntry.RuleConfidence,
                            HoldSeconds = pendingEntry.HoldSeconds,
                            StopLoss = pendingEntry.StopLoss,
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
                            NotionalValue = brokerPos.MarketValue,
                            EntryRuleId = "RECOVERED",
                            HoldSeconds = tickerConfig?.OptimalHoldSeconds ?? 120,
                            StopLoss = tickerConfig?.StopLossAmount ?? 0.50m,
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
                    NotionalValue = brokerPos.MarketValue,
                    EntryRuleId = "STARTUP",
                    HoldSeconds = tickerConfig?.OptimalHoldSeconds ?? 120,
                    StopLoss = tickerConfig?.StopLossAmount ?? 0.50m,
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
