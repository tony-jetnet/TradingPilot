using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingPilot.EntityFrameworkCore;
using TradingPilot.Symbols;
using TradingPilot.Trading;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Linq;
using Volo.Abp.Uow;

namespace TradingPilot.Trading;

/// <summary>
/// Pre-market scanner job. Runs at 9:00 AM ET weekdays.
/// Ranks all 50 watched symbols and selects top 10 for active trading.
/// Sets IsActiveForTrading flag on selected symbols.
/// Persists selections to DailyWatchlists table.
/// </summary>
[DisableConcurrentExecution(300)]
[AutomaticRetry(Attempts = 0)]
public class PreMarketScannerJob
{
    private readonly PreMarketScanner _scanner;
    private readonly BarIndicatorCache _barCache;
    private readonly TickDataCache _tickCache;
    private readonly SetupDetector _setupDetector;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PreMarketScannerJob> _logger;

    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    public PreMarketScannerJob(
        PreMarketScanner scanner,
        BarIndicatorCache barCache,
        TickDataCache tickCache,
        SetupDetector setupDetector,
        IServiceScopeFactory scopeFactory,
        ILogger<PreMarketScannerJob> logger)
    {
        _scanner = scanner;
        _barCache = barCache;
        _tickCache = tickCache;
        _setupDetector = setupDetector;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task ScanAsync()
    {
        _logger.LogWarning("=== PRE-MARKET SCANNER START ===");

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            var symbolRepo = scope.ServiceProvider.GetRequiredService<IRepository<Symbol, string>>();
            var newsRepo = scope.ServiceProvider.GetRequiredService<IRepository<SymbolNews, Guid>>();
            var flowRepo = scope.ServiceProvider.GetRequiredService<IRepository<SymbolCapitalFlow, Guid>>();
            var asyncExec = scope.ServiceProvider.GetRequiredService<IAsyncQueryableExecuter>();
            var dbContext = scope.ServiceProvider.GetRequiredService<TradingPilotDbContext>();

            using var uow = uowManager.Begin();

            // 1. Load all watched symbols
            var watched = await asyncExec.ToListAsync(
                (await symbolRepo.GetQueryableAsync()).Where(s => s.IsWatched));

            if (watched.Count == 0)
            {
                _logger.LogWarning("No watched symbols found");
                await uow.CompleteAsync();
                return;
            }

            _logger.LogInformation("Scanner: evaluating {Count} watched symbols", watched.Count);

            // 2. Build scanner inputs
            var candidates = new List<ScannerInput>();
            var todayEt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Eastern).Date;
            var cutoff24h = DateTime.UtcNow.AddHours(-24);

            foreach (var symbol in watched)
            {
                try
                {
                    var input = await BuildScannerInputAsync(symbol, asyncExec, newsRepo, flowRepo, cutoff24h);
                    candidates.Add(input);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Scanner: failed to build input for {Ticker}", symbol.Id);
                }
            }

            // 3. Rank and select top 10
            var selections = _scanner.Rank(candidates);

            // 4. Update IsActiveForTrading flags
            // Clear all first
            await dbContext.Database.ExecuteSqlRawAsync(
                @"UPDATE ""Symbols"" SET ""IsActiveForTrading"" = false WHERE ""IsWatched"" = true");

            // Set active on selected
            var selectedSymbols = selections.Select(s => s.Symbol).ToList();
            if (selectedSymbols.Count > 0)
            {
                foreach (var sym in selectedSymbols)
                {
                    await dbContext.Database.ExecuteSqlRawAsync(
                        @"UPDATE ""Symbols"" SET ""IsActiveForTrading"" = true WHERE ""Id"" = {0}", sym);
                }
            }

            // 5. Persist to DailyWatchlists
            var todayDate = DateOnly.FromDateTime(todayEt);
            var selectionsJson = JsonSerializer.Serialize(selections.Select(s => new
            {
                symbol = s.Symbol,
                tickerId = s.TickerId,
                rank = s.Rank,
                rankScore = s.Score,
                gapPct = s.GapPercent,
                premarketVolRatio = s.PremarketVolumeRatio,
                catalystType = s.CatalystType,
                setupQuality = s.SetupQuality,
                atrPct = s.AtrPct,
                reason = s.Reason,
            }), new JsonSerializerOptions { WriteIndented = false });

            // Upsert: delete existing for today, insert new
            await dbContext.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""DailyWatchlists"" WHERE ""Date"" = {0}", todayDate);

            dbContext.DailyWatchlists.Add(new DailyWatchlist
            {
                Date = todayDate,
                Selections = selectionsJson,
                CreatedAt = DateTime.UtcNow,
            });

            await uow.CompleteAsync();

            _logger.LogWarning("=== PRE-MARKET SCANNER COMPLETE: selected {Count} symbols [{Symbols}] ===",
                selections.Count, string.Join(", ", selections.Select(s => $"{s.Symbol}({s.Score:F2})")));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pre-market scanner failed");
        }
    }

    private async Task<ScannerInput> BuildScannerInputAsync(
        Symbol symbol,
        IAsyncQueryableExecuter asyncExec,
        IRepository<SymbolNews, Guid> newsRepo,
        IRepository<SymbolCapitalFlow, Guid> flowRepo,
        DateTime cutoff24h)
    {
        var input = new ScannerInput
        {
            Symbol = symbol.Id,
            TickerId = symbol.WebullTickerId,
        };

        // Gap: compare current price to yesterday's close
        var barIndicators = _barCache.GetIndicators(symbol.WebullTickerId);
        var tickData = _tickCache.GetData(symbol.WebullTickerId);

        decimal currentPrice = tickData?.LastPrice ?? barIndicators?.Ema9 ?? 0;
        decimal prevClose = barIndicators?.Ema20 ?? 0; // Rough proxy — EMA20 on 1m is close to prev close

        if (currentPrice > 0 && prevClose > 0)
            input.GapPercent = (currentPrice - prevClose) / prevClose;

        // ATR
        input.AtrPct = barIndicators?.Atr14Pct ?? 0;

        // Volume
        input.PremarketVolumeRatio = barIndicators?.VolumeRatio ?? 0;

        // News catalyst (last 24h)
        var recentNews = await asyncExec.ToListAsync(
            (await newsRepo.GetQueryableAsync())
                .Where(n => n.SymbolId == symbol.Id && n.PublishedAt >= cutoff24h)
                .OrderByDescending(n => n.PublishedAt)
                .Take(5));

        input.CatalystType = recentNews.FirstOrDefault(n => n.CatalystType != null)?.CatalystType;

        // Capital flow (last 3 days)
        var recentFlows = await asyncExec.ToListAsync(
            (await flowRepo.GetQueryableAsync())
                .Where(f => f.SymbolId == symbol.Id)
                .OrderByDescending(f => f.Date)
                .Take(3));

        if (recentFlows.Count > 0)
        {
            decimal totalNet = recentFlows.Sum(f =>
                (f.SuperLargeInflow + f.LargeInflow) - (f.SuperLargeOutflow + f.LargeOutflow));
            decimal totalVol = recentFlows.Sum(f =>
                f.SuperLargeInflow + f.LargeInflow + f.SuperLargeOutflow + f.LargeOutflow);
            input.CapitalFlowNet = totalVol > 0 ? Math.Clamp(totalNet / totalVol, -1m, 1m) : 0;
        }

        // Setup quality: run detector on current indicators
        if (barIndicators != null && currentPrice > 0)
        {
            var setups = _setupDetector.DetectSetups(symbol.WebullTickerId, barIndicators, currentPrice);
            input.SetupQuality = setups.Count > 0 ? setups.Max(s => s.Strength) : 0;
        }

        return input;
    }
}
