using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TradingPilot.EntityFrameworkCore;
using TradingPilot.Symbols;
using TradingPilot.Webull;
using Volo.Abp.Application.Services;

namespace TradingPilot.Trading;

public class DashboardAppService : ApplicationService, IDashboardAppService
{
    private readonly SignalStore _signalStore;
    private readonly TickDataCache _tickCache;
    private readonly BarIndicatorCache _barCache;
    private readonly L2BookCache _l2Cache;
    private readonly StrategyRuleEvaluator _ruleEvaluator;
    private readonly MqttMessageProcessor _mqttProcessor;
    private readonly IBrokerClient _broker;
    private readonly IServiceScopeFactory _scopeFactory;

    public DashboardAppService(
        SignalStore signalStore,
        TickDataCache tickCache,
        BarIndicatorCache barCache,
        L2BookCache l2Cache,
        StrategyRuleEvaluator ruleEvaluator,
        MqttMessageProcessor mqttProcessor,
        IBrokerClient broker,
        IServiceScopeFactory scopeFactory)
    {
        _signalStore = signalStore;
        _tickCache = tickCache;
        _barCache = barCache;
        _l2Cache = l2Cache;
        _ruleEvaluator = ruleEvaluator;
        _mqttProcessor = mqttProcessor;
        _broker = broker;
        _scopeFactory = scopeFactory;
    }

    public async Task<DashboardDto> GetAsync()
    {
        var dto = new DashboardDto();

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TradingPilotDbContext>();

        // Get all watched symbols
        var symbols = await dbContext.Symbols
            .Where(s => s.IsWatched)
            .OrderBy(s => s.Id)
            .ToListAsync();

        // Build live symbol data from caches
        foreach (var symbol in symbols)
        {
            var tickData = _tickCache.GetData(symbol.WebullTickerId);
            var barData = _barCache.GetIndicators(symbol.WebullTickerId);
            var latestSignal = _signalStore.GetLatest(symbol.WebullTickerId);
            var recentSignals = _signalStore.GetRecent(symbol.WebullTickerId, 200);

            decimal change = tickData != null && tickData.Open > 0
                ? tickData.LastPrice - tickData.Open : 0;
            decimal changePct = tickData != null && tickData.Open > 0
                ? change / tickData.Open * 100 : 0;

            dto.Symbols.Add(new SymbolLiveDto
            {
                Ticker = symbol.Id,
                TickerId = symbol.WebullTickerId,
                Price = tickData?.LastPrice ?? 0,
                Open = tickData?.Open ?? 0,
                High = tickData?.High ?? 0,
                Low = tickData?.Low ?? 0,
                Volume = tickData?.Volume ?? 0,
                Change = change,
                ChangePercent = changePct,
                Ema9 = barData?.Ema9 ?? 0,
                Ema20 = barData?.Ema20 ?? 0,
                Rsi14 = barData?.Rsi14 ?? 0,
                Vwap = barData?.Vwap ?? 0,
                TickMomentum = tickData?.TickMomentum ?? 0,
                BookDepthRatio = tickData?.BookDepthRatio ?? 0,
                BidWallSize = tickData?.BidWallSize ?? 0,
                AskWallSize = tickData?.AskWallSize ?? 0,
                ImbalanceVelocity = tickData?.ImbalanceVelocity ?? 0,
                SignalCount = recentSignals.Count,
                LastSignalType = latestSignal?.Type.ToString(),
                LastSignalScore = latestSignal?.Indicators.GetValueOrDefault("CompositeScore") ?? 0,
                CurrentPosition = 0, // filled below from broker
                LastUpdate = tickData?.LastQuoteTime ?? DateTime.MinValue,
            });
        }

        // Recent trades from broker (filled orders today)
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var todayEt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, eastern).Date;

        var allOrders = await _broker.GetOrdersAsync(200);
        var todayFilled = allOrders
            .Where(o => o.Status == "Filled" && o.FilledTime.HasValue)
            .Where(o => TimeZoneInfo.ConvertTimeFromUtc(o.FilledTime!.Value, eastern).Date == todayEt)
            .OrderByDescending(o => o.FilledTime)
            .Take(50)
            .ToList();

        // Match trades to signals to get source (RULE/SWIN/WEIGHTED)
        var allSignals = _signalStore.GetActiveTickerIds()
            .SelectMany(tid => _signalStore.GetRecent(tid, 200))
            .ToList();
        dto.RecentTrades = todayFilled.Select(o =>
        {
            // Find the closest signal within 10s of the fill for this ticker
            var matchedSignal = allSignals
                .Where(s => s.Ticker == o.Symbol && o.FilledTime.HasValue)
                .OrderBy(s => Math.Abs((s.Timestamp - o.FilledTime!.Value).TotalSeconds))
                .FirstOrDefault(s => Math.Abs((s.Timestamp - o.FilledTime!.Value).TotalSeconds) < 10);

            string source = ExtractSource(matchedSignal?.Reason ?? "");

            return new TradeDto
            {
                Ticker = o.Symbol,
                Timestamp = o.FilledTime ?? DateTime.UtcNow,
                Action = o.Action,
                Quantity = o.FilledQuantity > 0 ? o.FilledQuantity : o.Quantity,
                Price = o.FilledPrice ?? o.LimitPrice ?? 0,
                Score = matchedSignal?.Indicators.GetValueOrDefault("CompositeScore") ?? 0,
                Reason = matchedSignal?.Reason ?? o.Status ?? "",
                Source = source,
                Status = o.Status,
            };
        }).ToList();

        // Recent signals from all tickers (from in-memory store)
        var feedSignals = new List<TradingSignal>();
        foreach (var symbol in symbols)
        {
            feedSignals.AddRange(_signalStore.GetRecent(symbol.WebullTickerId, 20));
        }
        dto.RecentSignals = feedSignals
            .OrderByDescending(s => s.Timestamp)
            .Take(30)
            .Select(s => new SignalDto
            {
                Ticker = s.Ticker,
                Timestamp = s.Timestamp,
                Type = s.Type.ToString(),
                Strength = s.Strength.ToString(),
                Price = s.Price,
                Score = s.Indicators.GetValueOrDefault("CompositeScore"),
                Reason = s.Reason,
            }).ToList();

        // P&L summary from broker
        await ComputePnlSummaryAsync(dto, todayFilled);

        // Strategy status
        var config = _ruleEvaluator.CurrentConfig;
        dto.StrategyStatus = new StrategyStatusDto
        {
            IsLoaded = config != null,
            GeneratedAt = config?.GeneratedAt,
            SymbolCount = config?.Symbols.Count ?? 0,
            TotalRules = config?.Symbols.Values.Sum(s => s.Rules.Count) ?? 0,
            SymbolRules = config?.Symbols.Select(kv => new SymbolRuleSummaryDto
            {
                Ticker = kv.Key,
                RuleCount = kv.Value.Rules.Count,
                OverallWinRate = kv.Value.OverallWinRate,
                DisabledHours = kv.Value.DisabledHours,
            }).ToList() ?? new(),
        };

        // Hook status — check streaming health + static state
        if (dto.StreamingHealth.TotalMqttMessages > 0)
            dto.HookStatus = "Streaming";
        else if (WebullHookAppService.IsPipeConnected)
            dto.HookStatus = "Pipe Connected";
        else if (WebullHookAppService.IsInjected)
            dto.HookStatus = "Injected";
        else if (WebullHookAppService.CapturedAuthHeader != null)
            dto.HookStatus = "Auth Only";
        else
            dto.HookStatus = "Disconnected";

        // Streaming health
        dto.StreamingHealth = _mqttProcessor.GetHealthMetrics();

        // Per-symbol health: L2 age, quote age
        var now = DateTime.UtcNow;
        foreach (var symbol in symbols)
        {
            var l2Latest = _l2Cache.GetLatest(symbol.WebullTickerId);
            var tickData = _tickCache.GetData(symbol.WebullTickerId);

            double l2AgeSec = l2Latest != null ? (now - l2Latest.Timestamp).TotalSeconds : -1;
            double quoteAgeSec = tickData != null && tickData.LastQuoteTime != default
                ? (now - tickData.LastQuoteTime).TotalSeconds : -1;
            double tickSnapAgeSec = dto.StreamingHealth.TickerStalenessSeconds
                .GetValueOrDefault(symbol.WebullTickerId, -1);

            string status;
            if (l2AgeSec < 0 && quoteAgeSec < 0)
                status = "Offline";
            else
            {
                double freshest = new[] { l2AgeSec, quoteAgeSec }.Where(x => x >= 0).DefaultIfEmpty(999).Min();
                status = freshest switch
                {
                    < 10 => "Live",
                    < 60 => "Stale",
                    < 300 => "Delayed",
                    _ => "Offline"
                };
            }

            dto.SymbolHealth.Add(new SymbolHealthDto
            {
                Ticker = symbol.Id,
                L2AgeSec = Math.Round(l2AgeSec, 1),
                QuoteAgeSec = Math.Round(quoteAgeSec, 1),
                TickSnapshotAgeSec = 0,
                Status = status,
            });
        }

        return dto;
    }

    private async Task ComputePnlSummaryAsync(DashboardDto dto, List<BrokerOrder> todayFilled)
    {
        // Get account info for current positions
        var account = await _broker.GetAccountAsync();
        if (account != null)
        {
            foreach (var pos in account.Positions)
            {
                var symbolDto = dto.Symbols.FirstOrDefault(s => s.Ticker == pos.Symbol);
                if (symbolDto != null)
                    symbolDto.CurrentPosition = pos.Quantity;

                // Use Webull's exact numbers — broker is source of truth
                int absQty = Math.Abs(pos.Quantity);
                decimal costBasis = pos.AvgPrice * absQty;

                dto.OpenPositions.Add(new PositionDto
                {
                    Ticker = pos.Symbol,
                    Side = pos.Quantity > 0 ? "Long" : "Short",
                    Quantity = absQty,
                    AvgPrice = pos.AvgPrice,
                    CurrentPrice = pos.LastPrice,
                    MarketValue = Math.Abs(pos.MarketValue),
                    UnrealizedPnl = pos.UnrealizedPnl,
                    UnrealizedPnlPercent = costBasis > 0 ? pos.UnrealizedPnl / costBasis * 100 : 0,
                });
            }
        }

        // Pair filled orders: first per symbol = entry, next = exit
        // Track source from the entry trade in dto.RecentTrades
        var pnls = new List<(decimal Pnl, string Source)>();
        var openPositions = new Dictionary<string, (string Action, decimal Price, int Qty, string Source)>();

        var orderedFilled = todayFilled.OrderBy(o => o.FilledTime).ToList();
        var tradesByOrderId = dto.RecentTrades.ToDictionary(t => $"{t.Ticker}_{t.Timestamp.Ticks}", t => t.Source);

        foreach (var order in orderedFilled)
        {
            if (openPositions.TryGetValue(order.Symbol, out var entry))
            {
                decimal pnl = entry.Action == "BUY"
                    ? (order.FilledPrice!.Value - entry.Price) * entry.Qty
                    : (entry.Price - order.FilledPrice!.Value) * entry.Qty;
                pnls.Add((pnl, entry.Source));
                openPositions.Remove(order.Symbol);
            }
            else
            {
                // Find source from matched trade DTO
                var tradeDto = dto.RecentTrades
                    .FirstOrDefault(t => t.Ticker == order.Symbol && t.Timestamp == order.FilledTime);
                string source = tradeDto?.Source ?? "";
                openPositions[order.Symbol] = (order.Action, order.FilledPrice ?? 0, order.Quantity, source);
            }
        }

        var wins = pnls.Where(p => p.Pnl > 0).ToList();
        var losses = pnls.Where(p => p.Pnl <= 0).ToList();

        decimal avgWin = wins.Count > 0 ? wins.Average(p => p.Pnl) : 0;
        decimal avgLoss = losses.Count > 0 ? losses.Average(p => p.Pnl) : 0;
        decimal winRate = pnls.Count > 0 ? (decimal)wins.Count / pnls.Count : 0;

        // ═══════════════════════════════════════════════════════════
        // RISK-ADJUSTED PERFORMANCE METRICS
        // ═══════════════════════════════════════════════════════════

        // Expectancy: average $ you expect to make per trade
        // Positive = edge exists. Negative = you're donating to the market.
        decimal expectancy = (winRate * avgWin) + ((1 - winRate) * avgLoss);

        // Profit Factor: gross profits / gross losses. >1.5 to survive real costs.
        decimal grossProfit = wins.Sum(p => p.Pnl);
        decimal grossLoss = Math.Abs(losses.Sum(p => p.Pnl));
        decimal profitFactor = grossLoss > 0 ? grossProfit / grossLoss : grossProfit > 0 ? 99.9m : 0;

        // Sharpe Ratio (annualized): mean(returns) / stddev(returns) * sqrt(252)
        // Using per-trade P&L as "returns" since trades are intraday.
        decimal sharpeRatio = 0;
        decimal sortinoRatio = 0;
        if (pnls.Count >= 2)
        {
            decimal mean = pnls.Average(p => p.Pnl);
            double variance = pnls.Sum(p => (double)((p.Pnl - mean) * (p.Pnl - mean))) / (pnls.Count - 1);
            double stdDev = Math.Sqrt((double)variance);
            if (stdDev > 0)
                sharpeRatio = (decimal)((double)mean / stdDev * Math.Sqrt(252));

            // Sortino: only penalize downside deviation (negative returns)
            var downsideReturns = pnls.Where(p => p.Pnl < 0).ToList();
            if (downsideReturns.Count > 0)
            {
                double downsideVariance = downsideReturns.Sum(p => (double)(p.Pnl * p.Pnl)) / downsideReturns.Count;
                double downsideDev = Math.Sqrt(downsideVariance);
                if (downsideDev > 0)
                    sortinoRatio = (decimal)((double)mean / downsideDev * Math.Sqrt(252));
            }
            else if (mean > 0)
            {
                sortinoRatio = 99.9m; // All trades profitable, no downside
            }
        }

        // Max consecutive losses
        int maxConsecLosses = 0;
        int currentStreak = 0;
        foreach (var p in pnls)
        {
            if (p.Pnl <= 0) { currentStreak++; maxConsecLosses = Math.Max(maxConsecLosses, currentStreak); }
            else currentStreak = 0;
        }

        // Max drawdown: largest peak-to-trough drop in cumulative P&L
        decimal maxDrawdown = 0;
        decimal peak = 0;
        decimal cumPnl = 0;
        foreach (var p in pnls)
        {
            cumPnl += p.Pnl;
            if (cumPnl > peak) peak = cumPnl;
            decimal drawdown = cumPnl - peak;
            if (drawdown < maxDrawdown) maxDrawdown = drawdown;
        }

        dto.PnlSummary = new PnlSummaryDto
        {
            TotalTrades = pnls.Count,
            WinningTrades = wins.Count,
            LosingTrades = losses.Count,
            TotalPnl = pnls.Sum(p => p.Pnl),
            TotalCommissions = 0,
            NetPnl = pnls.Sum(p => p.Pnl),
            WinRate = winRate * 100,
            AvgWin = avgWin,
            AvgLoss = avgLoss,
            BestTrade = wins.Count > 0 ? wins.Max(p => p.Pnl) : 0,
            WorstTrade = losses.Count > 0 ? losses.Min(p => p.Pnl) : 0,
            TodayTrades = pnls.Count,
            TodayPnl = account?.DayPnl ?? pnls.Sum(p => p.Pnl),
            Expectancy = Math.Round(expectancy, 2),
            ProfitFactor = Math.Round(profitFactor, 2),
            SharpeRatio = Math.Round(sharpeRatio, 2),
            SortinoRatio = Math.Round(sortinoRatio, 2),
            MaxConsecutiveLosses = maxConsecLosses,
            MaxDrawdown = Math.Round(maxDrawdown, 2),
            SourceBreakdown = pnls
                .Where(p => !string.IsNullOrEmpty(p.Source))
                .GroupBy(p => p.Source)
                .Select(g => new SourcePnlDto
                {
                    Source = g.Key,
                    Trades = g.Count(),
                    Wins = g.Count(p => p.Pnl > 0),
                    Losses = g.Count(p => p.Pnl <= 0),
                    WinRate = g.Count() > 0 ? (decimal)g.Count(p => p.Pnl > 0) / g.Count() * 100 : 0,
                    TotalPnl = g.Sum(p => p.Pnl),
                    AvgPnl = g.Average(p => p.Pnl),
                })
                .OrderByDescending(s => s.Trades)
                .ToList(),
        };
    }

    private static string ExtractSource(string reason)
    {
        if (reason.StartsWith("[RULE")) return "RULE";
        if (reason.StartsWith("[SWIN")) return "SWIN";
        if (reason.StartsWith("[WEIGHTED")) return "WEIGHTED";
        return "";
    }
}
