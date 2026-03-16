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
/// </summary>
public class PaperTradingExecutor
{
    // Configuration — backtested on 1,400+ signals, 2026-03-16
    // ALIGNED+STRONG strategy: 57% win rate, $7.95/trade at 500 shares
    private const int SharesPerTrade = 500;         // Need 200+ to overcome $2.99 fee
    private const int MaxPositionShares = 500;      // Single position per ticker
    private const int MinSecondsBetweenTrades = 90; // 90s cooldown
    private const decimal MinScoreToEnter = 0.35m;  // Only strong signals
    private const decimal MinScoreToExit = 0.20m;   // Exit on moderate opposing signal
    private const decimal MomentumThreshold = 0.00m; // Signal must align with price direction
    private const decimal CommissionPerTrade = 2.99m; // Webull CA: $2.99 per trade

    // Fee tracking
    private int _totalTrades;
    private decimal _totalCommissions;
    private const long DefaultAccountId = 58226259;
    private static readonly TimeSpan AuthRefreshInterval = TimeSpan.FromMinutes(5);
    private const string AuthHeaderPath = @"D:\Third-Parties\WebullHook\auth_header.json";

    private readonly WebullPaperTradingClient _client;
    private readonly SignalStore _signalStore;
    private readonly L2BookCache _l2Cache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PaperTradingExecutor> _logger;

    // State
    private string? _authHeaderJson;
    private DateTime _authLoadedAt;
    private long _accountId = DefaultAccountId;
    private readonly ConcurrentDictionary<long, DateTime> _lastTradeTime = new();
    private readonly ConcurrentDictionary<long, int> _currentPosition = new();

    public PaperTradingExecutor(
        WebullPaperTradingClient client,
        SignalStore signalStore,
        L2BookCache l2Cache,
        IServiceScopeFactory scopeFactory,
        ILogger<PaperTradingExecutor> logger)
    {
        _client = client;
        _signalStore = signalStore;
        _l2Cache = l2Cache;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // Track entry price and time for exit logic
    private readonly ConcurrentDictionary<long, decimal> _entryPrice = new();
    private readonly ConcurrentDictionary<long, DateTime> _entryTime = new();
    private readonly ConcurrentDictionary<long, int> _consecutiveSellSignals = new();

    /// <summary>
    /// Backtested strategy (1,400+ signals, 2026-03-16):
    /// - Entry: score >= 0.35 AND aligned with 30s price momentum
    /// - Hold: ~1 minute (optimal from backtest)
    /// - Exit: 1 min time stop, opposing signal >= 0.20, or $0.30 stop loss
    /// - 500 shares per trade (need 200+ to cover $2.99 fee)
    /// - Expected: 57% win rate, ~$5/trade net after fees
    /// </summary>
    public async Task OnSignalAsync(TradingSignal signal)
    {
        RefreshAuthIfNeeded();
        if (_authHeaderJson == null) return;

        // Rate limit
        if (_lastTradeTime.TryGetValue(signal.TickerId, out var lastTime)
            && (DateTime.UtcNow - lastTime).TotalSeconds < MinSecondsBetweenTrades)
            return;

        decimal score = signal.Indicators.GetValueOrDefault("CompositeScore");
        int currentShares = _currentPosition.GetValueOrDefault(signal.TickerId, 0);
        Guid? symbolId = await ResolveSymbolIdAsync(signal.TickerId);

        // ═══════════════════════════════════════════════════════════
        // CHECK MOMENTUM: get price 30s ago from L2BookCache
        // ═══════════════════════════════════════════════════════════
        decimal momentum = 0;
        var recentSnapshots = _l2Cache.GetSnapshots(signal.TickerId, 60);
        if (recentSnapshots.Count >= 2)
        {
            // Find snapshot ~30s ago
            var now = recentSnapshots[^1];
            var older = recentSnapshots.FirstOrDefault(s =>
                (now.Timestamp - s.Timestamp).TotalSeconds >= 25 &&
                (now.Timestamp - s.Timestamp).TotalSeconds <= 40);
            if (older != null)
                momentum = now.MidPrice - older.MidPrice;
        }

        // ═══════════════════════════════════════════════════════════
        // ENTRY: Strong signal + aligned with momentum
        // ═══════════════════════════════════════════════════════════
        bool isBuyEntry = signal.Type == SignalType.Buy && score >= MinScoreToEnter && momentum >= MomentumThreshold;
        bool isSellEntry = signal.Type == SignalType.Sell && score <= -MinScoreToEnter && momentum <= -MomentumThreshold;

        if ((isBuyEntry || isSellEntry) && currentShares == 0)
        {
            string action = isBuyEntry ? "BUY" : "SELL";
            int qty = SharesPerTrade;

            await PlaceOrderAsync(signal.TickerId, signal.Ticker, action, qty,
                signal.Price, $"{action} score={score:F3} mom={momentum:F3}", score, symbolId, null);

            _entryPrice[signal.TickerId] = signal.Price;
            _entryTime[signal.TickerId] = DateTime.UtcNow;
            _consecutiveSellSignals[signal.TickerId] = 0;
        }
        // ═══════════════════════════════════════════════════════════
        // EXIT: Time stop (1 min), opposing signal, or stop loss
        // ═══════════════════════════════════════════════════════════
        else if (currentShares != 0)
        {
            bool isLong = currentShares > 0;
            bool shouldExit = false;
            string reason = "";

            // 1. Time stop: 1 minute (optimal hold from backtest)
            if (_entryTime.TryGetValue(signal.TickerId, out var et) && (DateTime.UtcNow - et).TotalSeconds >= 60)
            {
                shouldExit = true;
                decimal elapsed = (decimal)(DateTime.UtcNow - et).TotalSeconds;
                reason = $"TIME {elapsed:F0}s";
            }

            // 2. Stop loss: $0.30 adverse
            if (!shouldExit && _entryPrice.TryGetValue(signal.TickerId, out var ep))
            {
                decimal adverse = isLong ? ep - signal.Price : signal.Price - ep;
                if (adverse > 0.30m)
                {
                    shouldExit = true;
                    reason = $"STOP LOSS {adverse:F2}";
                }
            }

            // 3. Strong opposing signal
            bool opposing = (isLong && signal.Type == SignalType.Sell) || (!isLong && signal.Type == SignalType.Buy);
            if (!shouldExit && opposing && Math.Abs(score) >= MinScoreToExit)
            {
                shouldExit = true;
                reason = $"OPPOSING score={score:F3}";
            }

            if (shouldExit)
            {
                string action = isLong ? "SELL" : "BUY";
                int qty = Math.Abs(currentShares);
                decimal pnl = _entryPrice.TryGetValue(signal.TickerId, out var ep3)
                    ? (isLong ? signal.Price - ep3 : ep3 - signal.Price) * qty : 0;
                await PlaceOrderAsync(signal.TickerId, signal.Ticker, action, qty,
                    signal.Price, $"EXIT {reason} P&L=${pnl - CommissionPerTrade:F2}net", score, symbolId, null);
                _entryPrice.TryRemove(signal.TickerId, out _);
                _entryTime.TryRemove(signal.TickerId, out _);
                _consecutiveSellSignals[signal.TickerId] = 0;
            }
        }
    }

    private async Task PlaceOrderAsync(long tickerId, string ticker, string action, int qty,
        decimal price, string reason, decimal score, Guid? symbolId, Guid? signalId)
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
            int delta = action == "BUY" ? qty : -qty;
            _currentPosition.AddOrUpdate(tickerId, delta, (_, current) => current + delta);

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

    /// <summary>
    /// Periodically sync positions from the API to keep local state accurate.
    /// Should be called every ~60s.
    /// </summary>
    public async Task SyncPositionsAsync()
    {
        RefreshAuthIfNeeded();
        if (_authHeaderJson == null) return;

        try
        {
            var account = await _client.GetAccountAsync(_authHeaderJson, _accountId);
            if (account == null) return;

            // Update local position state from actual positions
            var newPositions = new Dictionary<long, int>();
            foreach (var pos in account.Positions)
            {
                if (pos.TickerId > 0)
                    newPositions[pos.TickerId] = pos.Quantity;
            }

            // Clear positions not in API response
            foreach (var key in _currentPosition.Keys)
            {
                if (!newPositions.ContainsKey(key))
                    _currentPosition.TryRemove(key, out _);
            }

            // Update from API
            foreach (var (tickerId, qty) in newPositions)
            {
                _currentPosition[tickerId] = qty;
            }

            _logger.LogInformation(
                "Paper account sync: NetLiq={NetLiq:C} Cash={Cash:C} Positions={PosCount} OpenOrders={OrderCount}",
                account.NetLiquidation, account.UsableCash,
                account.Positions.Count, account.OpenOrders.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync paper trading positions");
        }
    }

    private void RefreshAuthIfNeeded()
    {
        if (_authHeaderJson != null && (DateTime.UtcNow - _authLoadedAt) < AuthRefreshInterval)
            return;

        _authHeaderJson = LoadAuthHeader();
        _authLoadedAt = DateTime.UtcNow;
    }

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

    private async Task PersistTradeAsync(long tickerId, Guid? symbolId, string action, int qty,
        decimal signalPrice, decimal score, string reason, long? webullOrderId, string? orderStatus, Guid? signalId)
    {
        try
        {
            var trade = new PaperTrade
            {
                SymbolId = symbolId ?? Guid.Empty,
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

    private async Task<Guid?> ResolveSymbolIdAsync(long tickerId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            var symbolRepo = scope.ServiceProvider.GetRequiredService<IRepository<Symbol, Guid>>();
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
