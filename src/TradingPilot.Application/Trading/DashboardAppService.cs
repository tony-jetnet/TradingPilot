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

        dto.RecentTrades = todayFilled.Select(o => new TradeDto
        {
            Ticker = o.Symbol,
            Timestamp = o.FilledTime ?? DateTime.UtcNow,
            Action = o.Action,
            Quantity = o.Quantity,
            Price = o.FilledPrice ?? o.LimitPrice ?? 0,
            Score = 0,
            Reason = o.Status,
            Status = o.Status,
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
            }
        }

        // Pair filled orders: first per symbol = entry, next = exit
        var pnls = new List<decimal>();
        var openPositions = new Dictionary<string, (string Action, decimal Price, int Qty)>();

        var orderedFilled = todayFilled.OrderBy(o => o.FilledTime).ToList();
        foreach (var order in orderedFilled)
        {
            if (openPositions.TryGetValue(order.Symbol, out var entry))
            {
                decimal pnl = entry.Action == "BUY"
                    ? (order.FilledPrice!.Value - entry.Price) * entry.Qty
                    : (entry.Price - order.FilledPrice!.Value) * entry.Qty;
                pnls.Add(pnl);
                openPositions.Remove(order.Symbol);
            }
            else
            {
                openPositions[order.Symbol] = (order.Action, order.FilledPrice ?? 0, order.Quantity);
            }
        }

        var wins = pnls.Where(p => p > 0).ToList();
        var losses = pnls.Where(p => p <= 0).ToList();

        dto.PnlSummary = new PnlSummaryDto
        {
            TotalTrades = pnls.Count,
            WinningTrades = wins.Count,
            LosingTrades = losses.Count,
            TotalPnl = pnls.Sum(),
            TotalCommissions = 0,
            NetPnl = pnls.Sum(),
            WinRate = pnls.Count > 0 ? (decimal)wins.Count / pnls.Count * 100 : 0,
            AvgWin = wins.Count > 0 ? wins.Average() : 0,
            AvgLoss = losses.Count > 0 ? losses.Average() : 0,
            BestTrade = pnls.Count > 0 ? pnls.Max() : 0,
            WorstTrade = pnls.Count > 0 ? pnls.Min() : 0,
            TodayTrades = pnls.Count,
            TodayPnl = account?.DayPnl ?? pnls.Sum(),
        };
    }
}
