using Hangfire;
using Microsoft.Extensions.Logging;
using TradingPilot.Symbols;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Linq;
using Volo.Abp.Uow;

namespace TradingPilot.Webull;

[DisableConcurrentExecution(120)]
[AutomaticRetry(Attempts = 0)]
public class PollL2DepthJob
{
    private readonly IWebullApiClient _api;
    private readonly IRepository<Symbol, Guid> _symbolRepo;
    private readonly IRepository<SymbolBookSnapshot, Guid> _snapshotRepo;
    private readonly IAsyncQueryableExecuter _asyncExecuter;
    private readonly IUnitOfWorkManager _uowManager;
    private readonly L2BookCache _cache;
    private readonly ILogger<PollL2DepthJob> _logger;

    public PollL2DepthJob(
        IWebullApiClient api,
        IRepository<Symbol, Guid> symbolRepo,
        IRepository<SymbolBookSnapshot, Guid> snapshotRepo,
        IAsyncQueryableExecuter asyncExecuter,
        IUnitOfWorkManager uowManager,
        L2BookCache cache,
        ILogger<PollL2DepthJob> logger)
    {
        _api = api;
        _symbolRepo = symbolRepo;
        _snapshotRepo = snapshotRepo;
        _asyncExecuter = asyncExecuter;
        _uowManager = uowManager;
        _cache = cache;
        _logger = logger;
    }

    public async Task ExecuteAsync()
    {
        if (!MarketHoursHelper.IsMarketOpen(DateTime.UtcNow))
        {
            _logger.LogDebug("Market closed, skipping L2 poll");
            return;
        }

        string? authHeader = WebullHookAppService.CapturedAuthHeader;
        if (authHeader == null)
        {
            _logger.LogWarning("Auth header not captured yet, skipping L2 poll");
            return;
        }

        List<Symbol> watched;
        using (var uow = _uowManager.Begin())
        {
            watched = await _asyncExecuter.ToListAsync(
                (await _symbolRepo.GetQueryableAsync()).Where(s => s.IsWatched));
            await uow.CompleteAsync();
        }

        if (watched.Count == 0)
        {
            _logger.LogDebug("No watched symbols, skipping L2 poll");
            return;
        }

        for (int i = 0; i < 12; i++)
        {
            foreach (var symbol in watched)
            {
                try
                {
                    await PollOneAsync(authHeader, symbol);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "L2 poll failed for {Ticker}", symbol.Ticker);
                }
            }

            if (i < 11)
                await Task.Delay(5000);
        }
    }

    private async Task PollOneAsync(string authHeader, Symbol symbol)
    {
        var depth = await _api.GetDepthAsync(authHeader, symbol.WebullTickerId);
        if (depth == null || (depth.Bids.Count == 0 && depth.Asks.Count == 0))
        {
            _logger.LogDebug("Empty depth for {Ticker}", symbol.Ticker);
            return;
        }

        var bidPrices = depth.Bids.Select(l => l.Price).ToArray();
        var bidSizes = depth.Bids.Select(l => l.Volume).ToArray();
        var askPrices = depth.Asks.Select(l => l.Price).ToArray();
        var askSizes = depth.Asks.Select(l => l.Volume).ToArray();

        decimal bestBid = bidPrices.Length > 0 ? bidPrices[0] : 0;
        decimal bestAsk = askPrices.Length > 0 ? askPrices[0] : 0;
        decimal spread = bestAsk - bestBid;
        decimal midPrice = (bestBid + bestAsk) / 2;
        decimal totalBidSize = bidSizes.Sum();
        decimal totalAskSize = askSizes.Sum();
        decimal imbalance = totalBidSize + totalAskSize > 0
            ? (totalBidSize - totalAskSize) / (totalBidSize + totalAskSize)
            : 0;

        var snapshot = new SymbolBookSnapshot
        {
            SymbolId = symbol.Id,
            Timestamp = DateTime.UtcNow,
            BidPrices = bidPrices,
            BidSizes = bidSizes,
            AskPrices = askPrices,
            AskSizes = askSizes,
            Spread = spread,
            MidPrice = midPrice,
            Imbalance = imbalance,
            Depth = Math.Max(bidPrices.Length, askPrices.Length),
        };

        _cache.AddSnapshot(symbol.WebullTickerId, snapshot);

        using var uow = _uowManager.Begin();
        await _snapshotRepo.InsertAsync(snapshot, autoSave: false);
        await uow.CompleteAsync();

        _logger.LogInformation("L2 depth: {Bids} bids, {Asks} asks for {Ticker} (spread={Spread:F4})",
            depth.Bids.Count, depth.Asks.Count, symbol.Ticker, spread);
    }
}
