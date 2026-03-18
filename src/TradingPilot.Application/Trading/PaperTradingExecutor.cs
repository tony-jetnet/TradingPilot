using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingPilot.Symbols;
using TradingPilot.Trading;
using TradingPilot.Webull;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Linq;
using Volo.Abp.Uow;

namespace TradingPilot.Trading;

/// <summary>
/// Auto-trading engine that connects microstructure signals to paper trading order execution.
/// Manages position sizing, rate limiting, and trade persistence.
/// Exit logic is handled by PositionMonitor (continuous) — OnSignalAsync only handles
/// strong opposing signal exits (event-driven) and entries.
/// </summary>
public class PaperTradingExecutor
{
    // Configuration
    private const int MinSecondsBetweenTrades = 90;
    private const decimal DefaultMaxPositionDollars = 25000m;
    private const decimal MomentumThreshold = 0.02m;
    private const decimal CommissionPerTrade = 2.99m;
    private const int MaxConcurrentPositions = 3;
    private const decimal DailyLossLimit = -2000m; // Stop entries after losing $2K in a day

    // Fee tracking
    private int _totalTrades;
    private decimal _totalCommissions;

    // Daily P&L tracking (reset at market open)
    private decimal _dailyRealizedPnl;
    private DateTime _dailyResetDate;
    private const long DefaultAccountId = 58226259;
    private static readonly TimeSpan AuthRefreshInterval = TimeSpan.FromMinutes(5);
    private const string AuthHeaderPath = @"D:\Third-Parties\WebullHook\auth_header.json";

    private readonly WebullPaperTradingClient _client;
    private readonly SignalStore _signalStore;
    private readonly L2BookCache _l2Cache;
    private readonly MarketMicrostructureAnalyzer _analyzer;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PaperTradingExecutor> _logger;

    // State
    private string? _authHeaderJson;
    private DateTime _authLoadedAt;
    private long _accountId = DefaultAccountId;
    private readonly ConcurrentDictionary<long, DateTime> _lastTradeTime = new();
    private readonly ConcurrentDictionary<long, PositionState> _positions = new();

    public PaperTradingExecutor(
        WebullPaperTradingClient client,
        SignalStore signalStore,
        L2BookCache l2Cache,
        MarketMicrostructureAnalyzer analyzer,
        IServiceScopeFactory scopeFactory,
        ILogger<PaperTradingExecutor> logger)
    {
        _client = client;
        _signalStore = signalStore;
        _l2Cache = l2Cache;
        _analyzer = analyzer;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Expose open positions for PositionMonitor to evaluate.
    /// </summary>
    public IReadOnlyDictionary<long, PositionState> GetOpenPositions()
        => _positions;

    /// <summary>
    /// Exit a position (called by PositionMonitor or OnSignalAsync).
    /// </summary>
    public async Task ExitPositionAsync(long tickerId, decimal currentPrice, string reason, decimal score)
    {
        // Atomic remove prevents double-exit from concurrent PositionMonitor + OnSignalAsync
        if (!_positions.TryRemove(tickerId, out var pos))
            return;

        RefreshAuthIfNeeded();
        if (_authHeaderJson == null) return;

        string action = pos.IsLong ? "SELL" : "BUY";
        int qty = Math.Abs(pos.Shares);
        decimal pnl = (pos.IsLong ? currentPrice - pos.EntryPrice : pos.EntryPrice - currentPrice) * qty;
        decimal netPnl = pnl - CommissionPerTrade * 2; // Round-trip commission

        // Track daily realized P&L
        ResetDailyPnlIfNewDay();
        _dailyRealizedPnl += netPnl;

        await PlaceOrderAsync(tickerId, pos.Ticker, action, qty,
            currentPrice, $"EXIT {reason} P&L=${netPnl:F2}net DayPnL=${_dailyRealizedPnl:F2}", score, pos.SymbolId, null);

        if (_dailyRealizedPnl <= DailyLossLimit)
        {
            _logger.LogWarning("CIRCUIT BREAKER: Daily P&L={DailyPnl:F2} hit limit {Limit}. No new entries until tomorrow.",
                _dailyRealizedPnl, DailyLossLimit);
        }
    }

    /// <summary>
    /// Handle a new trading signal — only strong opposing signal exits + entries.
    /// Time stop, stop loss, score-based exits are handled by PositionMonitor.
    /// </summary>
    public async Task OnSignalAsync(TradingSignal signal)
    {
        RefreshAuthIfNeeded();
        if (_authHeaderJson == null) return;

        decimal score = signal.Indicators.GetValueOrDefault("CompositeScore");
        string? symbolId = await ResolveSymbolIdAsync(signal.TickerId);

        // Already in a position for this ticker — skip (PositionMonitor handles all exits)
        if (_positions.ContainsKey(signal.TickerId))
            return;

        // ═══════════════════════════════════════════════════════════
        // ENTRY: Circuit breaker, position limit, rate limit
        // ═══════════════════════════════════════════════════════════
        ResetDailyPnlIfNewDay();
        if (_dailyRealizedPnl <= DailyLossLimit)
        {
            _logger.LogDebug("Skipping entry for {Ticker}: daily loss limit hit ({DailyPnl:F2})",
                signal.Ticker, _dailyRealizedPnl);
            return;
        }

        if (_positions.Count >= MaxConcurrentPositions)
        {
            _logger.LogDebug("Skipping entry for {Ticker}: max concurrent positions ({Max})",
                signal.Ticker, MaxConcurrentPositions);
            return;
        }

        if (_lastTradeTime.TryGetValue(signal.TickerId, out var lastTime)
            && (DateTime.UtcNow - lastTime).TotalSeconds < MinSecondsBetweenTrades)
            return;

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

        // ═══════════════════════════════════════════════════════════
        // ENTRY: Strong signal + aligned with meaningful momentum
        // ═══════════════════════════════════════════════════════════
        bool isBuyEntry = signal.Type == SignalType.Buy && score >= minScoreEntry && momentum >= MomentumThreshold;
        bool isSellEntry = signal.Type == SignalType.Sell && score <= -minScoreEntry && momentum <= -MomentumThreshold;

        if (isBuyEntry || isSellEntry)
        {
            string action = isBuyEntry ? "BUY" : "SELL";

            // Dollar-based position sizing
            decimal maxDollars = DefaultMaxPositionDollars;
            var strategyConfig = _analyzer.RuleEvaluator.CurrentConfig;
            if (strategyConfig != null &&
                strategyConfig.Symbols.TryGetValue(signal.Ticker, out var symStrategy))
            {
                maxDollars = symStrategy.MaxPositionDollars > 0
                    ? symStrategy.MaxPositionDollars
                    : DefaultMaxPositionDollars;
            }
            int qty = Math.Max(1, (int)(maxDollars / signal.Price));

            await PlaceOrderAsync(signal.TickerId, signal.Ticker, action, qty,
                signal.Price, $"{action} score={score:F3} mom={momentum:F3}", score, symbolId, null);

            // Create consolidated position state
            _positions[signal.TickerId] = new PositionState
            {
                TickerId = signal.TickerId,
                Ticker = signal.Ticker,
                SymbolId = symbolId,
                Shares = isBuyEntry ? qty : -qty,
                EntryPrice = signal.Price,
                EntryTime = DateTime.UtcNow,
                EntryScore = score,
                PeakFavorableScore = score,
                PeakFavorablePrice = signal.Price,
                EntryImbalance = latestSnapshot.Imbalance,
                EntrySpread = latestSnapshot.Spread,
                EntrySpreadPercentile = 0.5m,
                EntryTrendDirection = (int)(signal.Indicators.GetValueOrDefault("TrendDir", 0)),
                NotionalValue = signal.Price * qty,
                EntryRuleId = ruleId,
                RuleConfidence = ruleConfidence,
                HoldSeconds = entryHoldSeconds,
                StopLoss = entryStopLoss,

            };
        }
    }

    /// <summary>
    /// Sync positions from broker API. Called by PositionMonitor every ~30s.
    /// </summary>
    public async Task SyncPositionsFromBrokerAsync()
    {
        RefreshAuthIfNeeded();
        if (_authHeaderJson == null) return;

        try
        {
            var account = await _client.GetAccountAsync(_authHeaderJson, _accountId);
            if (account == null) return;

            // Build map of broker positions
            var brokerPositions = new Dictionary<long, int>();
            foreach (var pos in account.Positions)
            {
                if (pos.TickerId > 0)
                    brokerPositions[pos.TickerId] = pos.Quantity;
            }

            // Remove local positions not in broker
            foreach (var key in _positions.Keys)
            {
                if (!brokerPositions.ContainsKey(key))
                {
                    _positions.TryRemove(key, out _);
                    _logger.LogInformation("Position {TickerId} closed at broker, removed from local state", key);
                }
            }

            // Update share counts from broker (catches partial fills, external trades)
            foreach (var (tickerId, qty) in brokerPositions)
            {
                if (_positions.TryGetValue(tickerId, out var pos))
                {
                    if (pos.Shares != qty)
                    {
                        _logger.LogInformation("Position {TickerId} shares adjusted: {Old} -> {New}",
                            tickerId, pos.Shares, qty);
                        pos.Shares = qty;
                    }
                }
                // Note: positions opened externally are not tracked (no entry context)
            }

            _logger.LogInformation(
                "Broker sync: NetLiq={NetLiq:C} Cash={Cash:C} Positions={PosCount} OpenOrders={OrderCount}",
                account.NetLiquidation, account.UsableCash,
                account.Positions.Count, account.OpenOrders.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync paper trading positions");
        }
    }

    private async Task PlaceOrderAsync(long tickerId, string ticker, string action, int qty,
        decimal price, string reason, decimal score, string? symbolId, Guid? signalId)
    {
        // Use limit order at current price (market orders don't work in extended hours)
        // Add small buffer: pay slightly more for buys, accept slightly less for sells
        decimal limitPrice = action == "BUY" ? price * 1.001m : price * 0.999m;
        limitPrice = Math.Round(limitPrice, 2);

        var order = new PaperOrderRequest
        {
            Action = action,
            OrderType = "LMT",
            LimitPrice = limitPrice,
            Quantity = qty,
            TickerId = tickerId,
            OutsideRegularTradingHour = true,
            TimeInForce = "DAY"
        };

        var result = await _client.PlaceOrderAsync(_authHeaderJson!, _accountId, order);

        if (result?.Success == true)
        {
            _lastTradeTime[tickerId] = DateTime.UtcNow;

            _totalTrades++;
            _totalCommissions += CommissionPerTrade;

            _logger.LogWarning(
                "PAPER TRADE: {Action} {Qty} {Ticker} @ ~{Price} | {Reason} | OrderId={OrderId} | Fee=${Fee} | TotalFees=${TotalFees} ({TradeCount} trades)",
                action, qty, ticker, price, reason, result.OrderId, CommissionPerTrade, _totalCommissions, _totalTrades);
        }
        else
        {
            _logger.LogError("PAPER TRADE FAILED: {Action} {Qty} {Ticker} | {Error}",
                action, qty, ticker, result?.ErrorMessage);
        }

        // Persist trade attempt to database
        await PersistTradeAsync(tickerId, symbolId, action, qty, price, score, reason,
            result?.OrderId, result?.Success == true ? "Placed" : $"Failed: {result?.ErrorMessage}", signalId);
    }

    private void ResetDailyPnlIfNewDay()
    {
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var etNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, eastern);
        var today = etNow.Date;
        if (_dailyResetDate != today)
        {
            _dailyRealizedPnl = 0;
            _dailyResetDate = today;
            _logger.LogInformation("Daily P&L reset for {Date}", today);
        }
    }

    internal void RefreshAuthIfNeeded()
    {
        if (_authHeaderJson != null && (DateTime.UtcNow - _authLoadedAt) < AuthRefreshInterval)
            return;

        _authHeaderJson = LoadAuthHeader();
        _authLoadedAt = DateTime.UtcNow;
    }

    internal string? AuthHeaderJson => _authHeaderJson;
    internal long AccountId => _accountId;

    private string? LoadAuthHeader()
    {
        try
        {
            if (File.Exists(AuthHeaderPath))
            {
                var content = File.ReadAllText(AuthHeaderPath).Trim();
                if (!string.IsNullOrEmpty(content))
                {
                    _logger.LogDebug("Loaded paper trading auth header from {Path}", AuthHeaderPath);
                    return content;
                }
            }

            _logger.LogWarning("Auth header file not found at {Path}", AuthHeaderPath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load auth header from {Path}", AuthHeaderPath);
            return null;
        }
    }

    private async Task PersistTradeAsync(long tickerId, string? symbolId, string action, int qty,
        decimal signalPrice, decimal score, string reason, long? webullOrderId, string? orderStatus, Guid? signalId)
    {
        try
        {
            var trade = new PaperTrade
            {
                SymbolId = symbolId ?? "",
                TickerId = tickerId,
                Timestamp = DateTime.UtcNow,
                Action = action,
                Quantity = qty,
                SignalPrice = signalPrice,
                Score = score,
                Reason = reason,
                WebullOrderId = webullOrderId,
                OrderStatus = orderStatus,
                SignalId = signalId,
            };

            using var scope = _scopeFactory.CreateScope();
            var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            var repo = scope.ServiceProvider.GetRequiredService<IRepository<PaperTrade, Guid>>();
            using var uow = uowManager.Begin();
            await repo.InsertAsync(trade, autoSave: false);
            await uow.CompleteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist paper trade for tickerId={TickerId}", tickerId);
        }
    }

    private async Task<string?> ResolveSymbolIdAsync(long tickerId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            var symbolRepo = scope.ServiceProvider.GetRequiredService<IRepository<Symbol, string>>();
            var asyncExecuter = scope.ServiceProvider.GetRequiredService<IAsyncQueryableExecuter>();

            using var uow = uowManager.Begin();
            var symbol = await asyncExecuter.FirstOrDefaultAsync(
                (await symbolRepo.GetQueryableAsync()).Where(s => s.WebullTickerId == tickerId));
            await uow.CompleteAsync();

            return symbol?.Id;
        }
        catch
        {
            return null;
        }
    }
}
