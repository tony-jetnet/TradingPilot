using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingPilot.EntityFrameworkCore;
using TradingPilot.Symbols;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Linq;
using Volo.Abp.Uow;

namespace TradingPilot.Webull;

[AutomaticRetry(Attempts = 5)]
public class LoadHistoricalBarsJob
{
    private readonly IWebullApiClient _api;
    private readonly IRepository<Symbol, string> _symbolRepo;
    private readonly IRepository<SymbolBar, Guid> _barRepo;
    private readonly IAsyncQueryableExecuter _asyncExecuter;
    private readonly IUnitOfWorkManager _uowManager;
    private readonly ILogger<LoadHistoricalBarsJob> _logger;

    private static readonly string AuthFilePath = Path.Combine(
        @"D:\Third-Parties\WebullHook", "auth_header.json");

    // No date range filter — insert all bars the API returns, dedup handles duplicates

    public LoadHistoricalBarsJob(
        IWebullApiClient api,
        IRepository<Symbol, string> symbolRepo,
        IRepository<SymbolBar, Guid> barRepo,
        IAsyncQueryableExecuter asyncExecuter,
        IUnitOfWorkManager uowManager,
        ILogger<LoadHistoricalBarsJob> logger)
    {
        _api = api;
        _symbolRepo = symbolRepo;
        _barRepo = barRepo;
        _asyncExecuter = asyncExecuter;
        _uowManager = uowManager;
        _logger = logger;
    }

    /// <summary>
    /// One-shot startup: loads bars for a specific ticker + timeframes.
    /// </summary>
    public async Task ExecuteAsync(string ticker, string[] timeframes)
    {
        string? authHeader = ResolveAuthHeader();
        if (authHeader == null)
            throw new InvalidOperationException("Auth header not captured yet. Will retry.");

        _logger.LogInformation("LoadHistoricalBars: searching for ticker {Ticker}...", ticker);
        var tickerInfo = await _api.SearchTickerAsync(authHeader, ticker);
        if (tickerInfo == null)
            throw new InvalidOperationException($"Ticker '{ticker}' not found via Webull search API.");

        _logger.LogInformation("Found {Ticker}: tickerId={TickerId}, name={Name}, exchange={Exchange}",
            tickerInfo.Symbol, tickerInfo.TickerId, tickerInfo.Name, tickerInfo.ExchangeCode);

        // Seed symbol if not exists
        Symbol? symbol;
        using (var uow = _uowManager.Begin())
        {
            symbol = await _asyncExecuter.FirstOrDefaultAsync(
                (await _symbolRepo.GetQueryableAsync()).Where(s => s.WebullTickerId == tickerInfo.TickerId));

            if (symbol == null)
            {
                symbol = await _symbolRepo.InsertAsync(new Symbol(tickerInfo.Symbol)
                {
                    Name = tickerInfo.Name,
                    WebullTickerId = tickerInfo.TickerId,
                    WebullExchangeId = tickerInfo.ExchangeId,
                    Exchange = tickerInfo.ExchangeCode,
                    SecurityType = MapSecurityType(tickerInfo.Type),
                    Status = SymbolStatus.Active,
                    IsWatched = true,
                });
                _logger.LogInformation("Created new Symbol: {Ticker}", symbol.Id);
            }
            else if (!symbol.IsWatched)
            {
                symbol.IsWatched = true;
                await _symbolRepo.UpdateAsync(symbol);
                _logger.LogInformation("Set IsWatched=true for existing Symbol: {Ticker}", symbol.Id);
            }
            await uow.CompleteAsync();
        }

        await LoadBarsForSymbolAsync(authHeader, symbol, timeframes);
    }

    /// <summary>
    /// Recurring hourly refresh: loads bars for ALL watched symbols.
    /// Called by Hangfire during market hours to keep bars fresh.
    /// </summary>
    public async Task RefreshAllAsync()
    {
        if (!MarketHoursHelper.IsMarketOpen(DateTime.UtcNow))
        {
            _logger.LogDebug("Market closed, skipping bar refresh");
            return;
        }

        string? authHeader = ResolveAuthHeader();
        if (authHeader == null)
        {
            _logger.LogWarning("No auth header available for bar refresh");
            return;
        }

        List<Symbol> watched;
        using (var uow = _uowManager.Begin())
        {
            watched = await _asyncExecuter.ToListAsync(
                (await _symbolRepo.GetQueryableAsync()).Where(s => s.IsWatched));
            await uow.CompleteAsync();
        }

        if (watched.Count == 0) return;

        // Refresh short timeframes (1m, 5m) to keep data current
        string[] timeframes = ["m1", "m5", "m15"];

        foreach (var symbol in watched)
        {
            try
            {
                await LoadBarsForSymbolAsync(authHeader, symbol, timeframes);
                await Task.Delay(500); // rate limit between symbols
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bar refresh failed for {Ticker}", symbol.Id);
            }
        }

        _logger.LogInformation("Hourly bar refresh complete for {Count} symbols", watched.Count);
    }

    private async Task LoadBarsForSymbolAsync(string authHeader, Symbol symbol, string[] timeframes)
    {
        foreach (string tf in timeframes)
        {
            // Map our timeframe codes to Webull API type + domain enum
            var (apiType, timeframe) = tf switch
            {
                "d" => ("d1", BarTimeframe.Daily),
                "m1" => ("m1", BarTimeframe.Minute1),
                "m5" => ("m5", BarTimeframe.Minute5),
                "m15" => ("m15", BarTimeframe.Minute15),
                "m30" => ("m30", BarTimeframe.Minute30),
                "h1" => ("m60", BarTimeframe.Hour1),
                _ => throw new ArgumentException($"Unknown timeframe: {tf}")
            };

            // Keep counts minimal — BarIndicatorService only needs last 30 m1 bars
            int count = tf switch
            {
                "d" => 30,    // ~30 trading days = 6 weeks
                "h1" => 50,   // ~7 trading days
                "m30" => 50,  // ~4 trading days
                "m15" => 100, // ~4 trading days
                "m5" => 200,  // ~2.5 trading days
                "m1" => 50,   // ~50 minutes (covers 30-bar indicator window)
                _ => 100,
            };
            _logger.LogInformation("Fetching {Timeframe} bars for {Ticker} (count={Count})...", tf, symbol.Id, count);

            var bars = await _api.GetBarsAsync(authHeader, symbol.WebullTickerId, apiType, count);
            await Task.Delay(500); // rate limit

            _logger.LogInformation("Got {Total} bars for {Timeframe} {Ticker}", bars.Count, tf, symbol.Id);

            // Bulk upsert using ON CONFLICT DO NOTHING (avoids per-bar SELECT EXISTS)
            int inserted = 0;
            using (var uow = _uowManager.Begin())
            {
                var dbContext = uow.ServiceProvider.GetRequiredService<TradingPilotDbContext>();
                var timeframeInt = (int)timeframe;

                foreach (var bar in bars)
                {
                    int rows = await dbContext.Database.ExecuteSqlRawAsync(@"
                        INSERT INTO ""SymbolBars"" (""Id"", ""SymbolId"", ""Timeframe"", ""Timestamp"", ""Open"", ""High"", ""Low"", ""Close"", ""Volume"", ""Vwap"", ""ChangeRatio"")
                        VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}, {9}, {10})
                        ON CONFLICT ON CONSTRAINT ""IX_SymbolBars_SymbolId_Timeframe_Timestamp"" DO NOTHING",
                        Guid.NewGuid(), symbol.Id, timeframeInt, bar.Timestamp,
                        bar.Open, bar.High, bar.Low, bar.Close, (long)bar.Volume, bar.Vwap ?? 0m, bar.ChangeRatio ?? 0m);
                    inserted += rows;
                }
                await uow.CompleteAsync();
            }

            _logger.LogInformation("Loaded {Inserted} new bars for {Timeframe} {Ticker} (API returned {Total})",
                inserted, tf, symbol.Id, bars.Count);
        }
    }

    private static SecurityType MapSecurityType(string? type) => type?.ToLowerInvariant() switch
    {
        "stock" => SecurityType.Stock,
        "etf" => SecurityType.Etf,
        "adr" => SecurityType.Adr,
        _ => SecurityType.Stock,
    };

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
