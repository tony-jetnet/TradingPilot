using Hangfire;
using Microsoft.Extensions.Logging;
using TradingPilot.Symbols;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Linq;
using Volo.Abp.Uow;

namespace TradingPilot.Webull;

[DisableConcurrentExecution(300)]
[AutomaticRetry(Attempts = 1)]
public class RefreshNewsJob
{
    private readonly IWebullApiClient _api;
    private readonly IRepository<Symbol, Guid> _symbolRepo;
    private readonly IRepository<SymbolNews, Guid> _newsRepo;
    private readonly IAsyncQueryableExecuter _asyncExecuter;
    private readonly IUnitOfWorkManager _uowManager;
    private readonly ILogger<RefreshNewsJob> _logger;

    public RefreshNewsJob(
        IWebullApiClient api,
        IRepository<Symbol, Guid> symbolRepo,
        IRepository<SymbolNews, Guid> newsRepo,
        IAsyncQueryableExecuter asyncExecuter,
        IUnitOfWorkManager uowManager,
        ILogger<RefreshNewsJob> logger)
    {
        _api = api;
        _symbolRepo = symbolRepo;
        _newsRepo = newsRepo;
        _asyncExecuter = asyncExecuter;
        _uowManager = uowManager;
        _logger = logger;
    }

    private static readonly string AuthFilePath = Path.Combine(
        @"D:\Third-Parties\WebullHook", "auth_header.json");

    public async Task ExecuteAsync()
    {
        string? authHeader = ResolveAuthHeader();
        if (authHeader == null)
        {
            _logger.LogWarning("No auth header available (memory or file), skipping news refresh");
            return;
        }

        List<Symbol> watched;
        using (var uow = _uowManager.Begin())
        {
            watched = await _asyncExecuter.ToListAsync(
                (await _symbolRepo.GetQueryableAsync()).Where(s => s.IsWatched));
            await uow.CompleteAsync();
        }

        foreach (var symbol in watched)
        {
            try
            {
                await RefreshForSymbolAsync(authHeader, symbol);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "News refresh failed for {Ticker}", symbol.Ticker);
            }
        }
    }

    private async Task RefreshForSymbolAsync(string authHeader, Symbol symbol)
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
        _logger.LogInformation("News refresh for {Ticker}: {New} new articles (of {Total} fetched)",
            symbol.Ticker, inserted, items.Count);
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
