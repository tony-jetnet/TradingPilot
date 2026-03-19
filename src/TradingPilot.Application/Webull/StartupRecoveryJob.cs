using Hangfire;
using Microsoft.Extensions.Logging;
using TradingPilot.Symbols;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Linq;
using Volo.Abp.Uow;

namespace TradingPilot.Webull;

/// <summary>
/// Startup job: backfills news and schedules historical bar loads for watched symbols.
/// Bar loads are staggered via Hangfire to avoid Webull 403 rate limits.
/// Heavy backfill (TradingSignals from bars) runs nightly.
/// </summary>
[AutomaticRetry(Attempts = 3)]
public class StartupRecoveryJob
{
    private readonly IWebullApiClient _api;
    private readonly IBackgroundJobClient _jobClient;
    private readonly IRepository<Symbol, string> _symbolRepo;
    private readonly IRepository<SymbolNews, Guid> _newsRepo;
    private readonly IAsyncQueryableExecuter _asyncExecuter;
    private readonly IUnitOfWorkManager _uowManager;
    private readonly ILogger<StartupRecoveryJob> _logger;

    private static readonly string AuthFilePath = Path.Combine(
        @"D:\Third-Parties\WebullHook", "auth_header.json");

    public StartupRecoveryJob(
        IWebullApiClient api,
        IBackgroundJobClient jobClient,
        IRepository<Symbol, string> symbolRepo,
        IRepository<SymbolNews, Guid> newsRepo,
        IAsyncQueryableExecuter asyncExecuter,
        IUnitOfWorkManager uowManager,
        ILogger<StartupRecoveryJob> logger)
    {
        _api = api;
        _jobClient = jobClient;
        _symbolRepo = symbolRepo;
        _newsRepo = newsRepo;
        _asyncExecuter = asyncExecuter;
        _uowManager = uowManager;
        _logger = logger;
    }

    public async Task ExecuteAsync()
    {
        string? authHeader = ResolveAuthHeader();
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

        // Schedule historical bar loads, staggered by 30s to avoid 403 rate limits
        if (MarketHoursHelper.IsMarketOpen(DateTime.UtcNow))
        {
            string[] timeframes = ["m1"];
            for (int i = 0; i < watched.Count; i++)
            {
                var ticker = watched[i].Id;
                var tf = timeframes;
                _jobClient.Schedule<LoadHistoricalBarsJob>(
                    job => job.ExecuteAsync(ticker, tf),
                    TimeSpan.FromSeconds(30 + i * 30));
            }
            _logger.LogInformation("Scheduled bar backfill for {Count} symbols (staggered 30s apart)", watched.Count);
        }

        // Backfill news
        foreach (var symbol in watched)
        {
            try
            {
                var items = await _api.GetTickerNewsAsync(authHeader, symbol.WebullTickerId);
                await Task.Delay(1_000); // rate limit news API calls
                if (items.Count > 0)
                {
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
                    if (inserted > 0)
                        _logger.LogInformation("Startup news backfill for {Ticker}: {New} new articles", symbol.Id, inserted);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Startup news backfill failed for {Ticker}", symbol.Id);
            }
        }

        _logger.LogInformation("Startup recovery complete for {Count} watched symbols", watched.Count);
    }

    private static string? ResolveAuthHeader()
    {
        var header = WebullHookAppService.CapturedAuthHeader;
        if (!string.IsNullOrWhiteSpace(header))
            return header;
        try
        {
            if (File.Exists(AuthFilePath))
            {
                var content = File.ReadAllText(AuthFilePath).Trim();
                if (!string.IsNullOrWhiteSpace(content))
                    return content;
            }
        }
        catch { }
        return null;
    }
}
