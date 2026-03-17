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
    private readonly IServiceScopeFactory _scopeFactory;

    public DashboardAppService(
        SignalStore signalStore,
        TickDataCache tickCache,
        BarIndicatorCache barCache,
        L2BookCache l2Cache,
        StrategyRuleEvaluator ruleEvaluator,
        MqttMessageProcessor mqttProcessor,
        IServiceScopeFactory scopeFactory)
    {
        _signalStore = signalStore;
        _tickCache = tickCache;
        _barCache = barCache;
        _l2Cache = l2Cache;
        _ruleEvaluator = ruleEvaluator;
        _mqttProcessor = mqttProcessor;
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
                CurrentPosition = 0, // filled below from trades
                LastUpdate = tickData?.LastQuoteTime ?? DateTime.MinValue,
            });
        }

        // Recent trades (today)
        var today = DateTime.UtcNow.Date;
        var trades = await dbContext.PaperTrades
            .Where(t => t.Timestamp >= today)
            .OrderByDescending(t => t.Timestamp)
            .Take(50)
            .ToListAsync();

        dto.RecentTrades = trades.Select(t => new TradeDto
        {
            Ticker = t.SymbolId,
            Timestamp = t.Timestamp,
            Action = t.Action,
            Quantity = t.Quantity,
            Price = t.SignalPrice,
            Score = t.Score,
            Reason = t.Reason,
            Status = t.OrderStatus,
        }).ToList();

        // Recent signals from all tickers (from in-memory store)
        var allSignals = new List<TradingSignal>();
        foreach (var symbol in symbols)
        {
            allSignals.AddRange(_signalStore.GetRecent(symbol.WebullTickerId, 20));
        }
        dto.RecentSignals = allSignals
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

        // P&L summary — compute from paired entry/exit trades
        await ComputePnlSummaryAsync(dbContext, dto);

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

        // Per-symbol health: L2 age, quote age, tick snapshot age
        var now = DateTime.UtcNow;
        foreach (var symbol in symbols)
        {
            var l2Latest = _l2Cache.GetLatest(symbol.WebullTickerId);
            var tickData = _tickCache.GetData(symbol.WebullTickerId);
            var barData = _barCache.GetIndicators(symbol.WebullTickerId);

            double l2AgeSec = l2Latest != null ? (now - l2Latest.Timestamp).TotalSeconds : -1;
            double quoteAgeSec = tickData != null && tickData.LastQuoteTime != default
                ? (now - tickData.LastQuoteTime).TotalSeconds : -1;
            double tickSnapAgeSec = dto.StreamingHealth.TickerStalenessSeconds
                .GetValueOrDefault(symbol.WebullTickerId, -1);

            // Status: Live (<10s), Stale (10-60s), Delayed (60-300s), Offline (>300s or no data)
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
                TickSnapshotAgeSec = Math.Round(tickSnapAgeSec, 1),
                Status = status,
            });
        }

        return dto;
    }

    private async Task ComputePnlSummaryAsync(TradingPilotDbContext dbContext, DashboardDto dto)
    {
        var allTrades = await dbContext.PaperTrades
            .OrderBy(t => t.Timestamp)
            .ToListAsync();

        if (allTrades.Count == 0) return;

        // Pair trades: entry (BUY with no prior position) → exit (SELL)
        var pnls = new List<decimal>();
        var positions = new Dictionary<string, (string Action, decimal Price, int Qty)>();
        decimal commissionPerTrade = 2.99m;
        var today = DateTime.UtcNow.Date;
        var todayPnls = new List<decimal>();

        foreach (var trade in allTrades)
        {
            if (positions.TryGetValue(trade.SymbolId, out var pos))
            {
                // This is an exit trade
                decimal pnl;
                if (pos.Action == "BUY")
                    pnl = (trade.SignalPrice - pos.Price) * pos.Qty;
                else
                    pnl = (pos.Price - trade.SignalPrice) * pos.Qty;

                pnl -= commissionPerTrade * 2; // entry + exit commission
                pnls.Add(pnl);
                if (trade.Timestamp >= today) todayPnls.Add(pnl);
                positions.Remove(trade.SymbolId);
            }
            else
            {
                // This is an entry trade
                positions[trade.SymbolId] = (trade.Action, trade.SignalPrice, trade.Quantity);
            }
        }

        // Update current positions on symbols
        foreach (var (symbolId, pos) in positions)
        {
            var symbolDto = dto.Symbols.FirstOrDefault(s => s.Ticker == symbolId);
            if (symbolDto != null)
                symbolDto.CurrentPosition = pos.Action == "BUY" ? pos.Qty : -pos.Qty;
        }

        var wins = pnls.Where(p => p > 0).ToList();
        var losses = pnls.Where(p => p <= 0).ToList();

        dto.PnlSummary = new PnlSummaryDto
        {
            TotalTrades = pnls.Count,
            WinningTrades = wins.Count,
            LosingTrades = losses.Count,
            TotalPnl = pnls.Sum(),
            TotalCommissions = allTrades.Count * commissionPerTrade,
            NetPnl = pnls.Sum(),
            WinRate = pnls.Count > 0 ? (decimal)wins.Count / pnls.Count * 100 : 0,
            AvgWin = wins.Count > 0 ? wins.Average() : 0,
            AvgLoss = losses.Count > 0 ? losses.Average() : 0,
            BestTrade = pnls.Count > 0 ? pnls.Max() : 0,
            WorstTrade = pnls.Count > 0 ? pnls.Min() : 0,
            TodayTrades = todayPnls.Count,
            TodayPnl = todayPnls.Sum(),
        };
    }
}
