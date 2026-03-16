using Hangfire;
using Microsoft.Extensions.Logging;
using TradingPilot.Symbols;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Linq;
using Volo.Abp.Uow;

namespace TradingPilot.Webull;

[AutomaticRetry(Attempts = 5)]
public class LoadHistoricalBarsJob
{
    private readonly IWebullApiClient _api;
    private readonly IRepository<Symbol, Guid> _symbolRepo;
    private readonly IRepository<SymbolBar, Guid> _barRepo;
    private readonly IAsyncQueryableExecuter _asyncExecuter;
    private readonly IUnitOfWorkManager _uowManager;
    private readonly ILogger<LoadHistoricalBarsJob> _logger;

    private static readonly string AuthFilePath = Path.Combine(
        @"D:\Third-Parties\WebullHook", "auth_header.json");

    // No date range filter — insert all bars the API returns, dedup handles duplicates

    public LoadHistoricalBarsJob(
        IWebullApiClient api,
        IRepository<Symbol, Guid> symbolRepo,
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
                symbol = await _symbolRepo.InsertAsync(new Symbol
                {
                    Ticker = tickerInfo.Symbol,
                    Name = tickerInfo.Name,
                    WebullTickerId = tickerInfo.TickerId,
                    WebullExchangeId = tickerInfo.ExchangeId,
                    Exchange = tickerInfo.ExchangeCode,
                    SecurityType = MapSecurityType(tickerInfo.Type),
                    Status = SymbolStatus.Active,
                    IsWatched = true,
                });
                _logger.LogInformation("Created new Symbol: {Ticker} (id={Id})", symbol.Ticker, symbol.Id);
            }
            else if (!symbol.IsWatched)
            {
                try
                {
                    symbol.IsWatched = true;
                    await _symbolRepo.UpdateAsync(symbol);
                    _logger.LogInformation("Set IsWatched=true for existing Symbol: {Ticker}", symbol.Ticker);
                }
                catch (Exception ex) when (ex is Volo.Abp.Data.AbpDbConcurrencyException)
                {
                    _logger.LogDebug("IsWatched update skipped (already set by another job) for {Ticker}", symbol.Ticker);
                }
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
                _logger.LogError(ex, "Bar refresh failed for {Ticker}", symbol.Ticker);
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

            // 1 API call per bar due to pagination; keep counts practical
            int count = tf switch
            {
                "d" => 30,    // ~30 trading days = 6 weeks
                "h1" => 50,   // ~7 trading days
                "m30" => 50,  // ~4 trading days
                "m15" => 100, // ~4 trading days
                "m5" => 200,  // ~2.5 trading days
                "m1" => 400,  // ~1 trading day
                _ => 100,
            };
            _logger.LogInformation("Fetching {Timeframe} bars for {Ticker} (count={Count})...", tf, symbol.Ticker, count);

            var bars = await _api.GetBarsAsync(authHeader, symbol.WebullTickerId, apiType, count);
            await Task.Delay(500); // rate limit

            _logger.LogInformation("Got {Total} bars for {Timeframe} {Ticker}", bars.Count, tf, symbol.Ticker);

            int inserted = 0, skipped = 0;
            using (var uow = _uowManager.Begin())
            {
                foreach (var bar in bars)
                {
                    var symbolId = symbol.Id;
                    var ts = bar.Timestamp;
                    bool exists = await _asyncExecuter.AnyAsync(
                        (await _barRepo.GetQueryableAsync()).Where(b =>
                            b.SymbolId == symbolId &&
                            b.Timeframe == timeframe &&
                            b.Timestamp == ts));

                    if (exists)
                    {
                        skipped++;
                        continue;
                    }

                    await _barRepo.InsertAsync(new SymbolBar
                    {
                        SymbolId = symbol.Id,
                        Timeframe = timeframe,
                        Timestamp = bar.Timestamp,
                        Open = bar.Open,
                        High = bar.High,
                        Low = bar.Low,
                        Close = bar.Close,
                        Volume = bar.Volume,
                        Vwap = bar.Vwap,
                        ChangeRatio = bar.ChangeRatio,
                    }, autoSave: false);
                    inserted++;
                }
                await uow.CompleteAsync();
            }

            _logger.LogInformation("Loaded {Inserted} new / skipped {Skipped} existing {Timeframe} bars for {Ticker} (API returned {Total})",
                inserted, skipped, tf, symbol.Ticker, bars.Count);
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
