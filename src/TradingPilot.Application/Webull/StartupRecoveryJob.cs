using Hangfire;
using Microsoft.Extensions.Logging;
using TradingPilot.Symbols;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Linq;
using Volo.Abp.Uow;

namespace TradingPilot.Webull;

[AutomaticRetry(Attempts = 3)]
public class StartupRecoveryJob
{
    private readonly IWebullApiClient _api;
    private readonly IRepository<Symbol, Guid> _symbolRepo;
    private readonly IRepository<SymbolBookSnapshot, Guid> _snapshotRepo;
    private readonly IRepository<SymbolNews, Guid> _newsRepo;
    private readonly IAsyncQueryableExecuter _asyncExecuter;
    private readonly IUnitOfWorkManager _uowManager;
    private readonly L2BookCache _cache;
    private readonly ILogger<StartupRecoveryJob> _logger;

    public StartupRecoveryJob(
        IWebullApiClient api,
        IRepository<Symbol, Guid> symbolRepo,
        IRepository<SymbolBookSnapshot, Guid> snapshotRepo,
        IRepository<SymbolNews, Guid> newsRepo,
        IAsyncQueryableExecuter asyncExecuter,
        IUnitOfWorkManager uowManager,
        L2BookCache cache,
        ILogger<StartupRecoveryJob> logger)
    {
        _api = api;
        _symbolRepo = symbolRepo;
        _snapshotRepo = snapshotRepo;
        _newsRepo = newsRepo;
        _asyncExecuter = asyncExecuter;
        _uowManager = uowManager;
        _cache = cache;
        _logger = logger;
    }

    public async Task ExecuteAsync()
    {
        string? authHeader = WebullHookAppService.CapturedAuthHeader;
        if (authHeader == null)
            throw new InvalidOperationException("Auth header not captured yet. Will retry.");

        List<Symbol> watched;
        using (var uow = _uowManager.Begin())
        {
            watched = await _asyncExecuter.ToListAsync(
                (await _symbolRepo.GetQueryableAsync()).Where(s => s.IsWatched));
            await uow.CompleteAsync();
        }

        if (watched.Count == 0)
        {
            _logger.LogInformation("No watched symbols for startup recovery");
            return;
        }

        foreach (var symbol in watched)
        {
            // Detect L2 gap
            await DetectL2GapAsync(symbol);

            // Take immediate L2 snapshot
            try
            {
                var depth = await _api.GetDepthAsync(authHeader, symbol.WebullTickerId);
                if (depth != null && (depth.Bids.Count > 0 || depth.Asks.Count > 0))
                {
                    var snapshot = BuildSnapshot(symbol, depth);
                    _cache.AddSnapshot(symbol.WebullTickerId, snapshot);
                    using var uow = _uowManager.Begin();
                    await _snapshotRepo.InsertAsync(snapshot, autoSave: false);
                    await uow.CompleteAsync();
                    _logger.LogInformation("Startup L2 snapshot taken for {Ticker}", symbol.Ticker);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Startup L2 snapshot failed for {Ticker}", symbol.Ticker);
            }

            // Backfill news
            try
            {
                await BackfillNewsAsync(authHeader, symbol);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Startup news backfill failed for {Ticker}", symbol.Ticker);
            }
        }

        _logger.LogInformation("Startup recovery complete for {Count} watched symbols", watched.Count);
    }

    private async Task DetectL2GapAsync(Symbol symbol)
    {
        using var uow = _uowManager.Begin();
        var symbolId = symbol.Id;
        var lastSnapshot = await _asyncExecuter.FirstOrDefaultAsync(
            (await _snapshotRepo.GetQueryableAsync())
                .Where(s => s.SymbolId == symbolId)
                .OrderByDescending(s => s.Timestamp)
                .Take(1));
        await uow.CompleteAsync();

        if (lastSnapshot == null)
        {
            _logger.LogWarning("L2 DATA GAP for {Ticker}: no prior snapshots found", symbol.Ticker);
            return;
        }

        var gap = DateTime.UtcNow - lastSnapshot.Timestamp;
        if (gap.TotalMinutes > 1)
        {
            _logger.LogWarning("L2 DATA GAP for {Ticker}: {From:HH:mm} to {To:HH:mm} ({Gap:F0} minutes) — data permanently lost",
                symbol.Ticker, lastSnapshot.Timestamp, DateTime.UtcNow, gap.TotalMinutes);
        }
    }

    private async Task BackfillNewsAsync(string authHeader, Symbol symbol)
    {
        var items = await _api.GetTickerNewsAsync(authHeader, symbol.WebullTickerId);
        if (items.Count == 0) return;

        int inserted = 0;
        using var uow = _uowManager.Begin();
        foreach (var item in items)
        {
            var symbolId = symbol.Id;
            var newsId = item.NewsId;
            bool exists = await _asyncExecuter.AnyAsync(
                (await _newsRepo.GetQueryableAsync()).Where(n =>
                    n.SymbolId == symbolId && n.WebullNewsId == newsId));

            if (exists) continue;

            await _newsRepo.InsertAsync(new SymbolNews
            {
                SymbolId = symbol.Id,
                WebullNewsId = item.NewsId,
                Title = item.Title,
                Summary = item.Summary,
                SourceName = item.SourceName,
                Url = item.Url,
                PublishedAt = item.PublishedAt,
                CollectedAt = DateTime.UtcNow,
            }, autoSave: false);
            inserted++;
        }
        await uow.CompleteAsync();

        _logger.LogInformation("Startup news backfill for {Ticker}: {New} new articles", symbol.Ticker, inserted);
    }

    private static SymbolBookSnapshot BuildSnapshot(Symbol symbol, WebullDepthData depth)
    {
        var bidPrices = depth.Bids.Select(l => l.Price).ToArray();
        var bidSizes = depth.Bids.Select(l => l.Volume).ToArray();
        var askPrices = depth.Asks.Select(l => l.Price).ToArray();
        var askSizes = depth.Asks.Select(l => l.Volume).ToArray();

        decimal bestBid = bidPrices.Length > 0 ? bidPrices[0] : 0;
        decimal bestAsk = askPrices.Length > 0 ? askPrices[0] : 0;
        decimal totalBidSize = bidSizes.Sum();
        decimal totalAskSize = askSizes.Sum();

        return new SymbolBookSnapshot
        {
            SymbolId = symbol.Id,
            Timestamp = DateTime.UtcNow,
            BidPrices = bidPrices,
            BidSizes = bidSizes,
            AskPrices = askPrices,
            AskSizes = askSizes,
            Spread = bestAsk - bestBid,
            MidPrice = (bestBid + bestAsk) / 2,
            Imbalance = totalBidSize + totalAskSize > 0
                ? (totalBidSize - totalAskSize) / (totalBidSize + totalAskSize) : 0,
            Depth = Math.Max(bidPrices.Length, askPrices.Length),
        };
    }
}
