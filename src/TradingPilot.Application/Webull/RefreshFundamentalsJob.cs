using Hangfire;
using Microsoft.Extensions.Logging;
using TradingPilot.Symbols;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Linq;
using Volo.Abp.Uow;

namespace TradingPilot.Webull;

[DisableConcurrentExecution(600)]
[AutomaticRetry(Attempts = 1)]
public class RefreshFundamentalsJob
{
    private readonly IWebullApiClient _api;
    private readonly IRepository<Symbol, Guid> _symbolRepo;
    private readonly IRepository<SymbolCapitalFlow, Guid> _flowRepo;
    private readonly IRepository<SymbolFinancialSnapshot, Guid> _financialRepo;
    private readonly IAsyncQueryableExecuter _asyncExecuter;
    private readonly IUnitOfWorkManager _uowManager;
    private readonly ILogger<RefreshFundamentalsJob> _logger;

    public RefreshFundamentalsJob(
        IWebullApiClient api,
        IRepository<Symbol, Guid> symbolRepo,
        IRepository<SymbolCapitalFlow, Guid> flowRepo,
        IRepository<SymbolFinancialSnapshot, Guid> financialRepo,
        IAsyncQueryableExecuter asyncExecuter,
        IUnitOfWorkManager uowManager,
        ILogger<RefreshFundamentalsJob> logger)
    {
        _api = api;
        _symbolRepo = symbolRepo;
        _flowRepo = flowRepo;
        _financialRepo = financialRepo;
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
            _logger.LogWarning("No auth header available (memory or file), skipping fundamentals refresh");
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
                await RefreshCapitalFlowAsync(authHeader, symbol);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Capital flow refresh failed for {Ticker}", symbol.Ticker);
            }

            await Task.Delay(500); // rate limit

            try
            {
                await RefreshFinancialsAsync(authHeader, symbol);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Financials refresh failed for {Ticker}", symbol.Ticker);
            }

            await Task.Delay(500);
        }
    }

    private async Task RefreshCapitalFlowAsync(string authHeader, Symbol symbol)
    {
        var data = await _api.GetCapitalFlowAsync(authHeader, symbol.WebullTickerId);
        if (data?.Date == null) return;

        if (!DateOnly.TryParseExact(data.Date, "yyyyMMdd", out var date))
        {
            _logger.LogWarning("Cannot parse capital flow date '{Date}' for {Ticker}", data.Date, symbol.Ticker);
            return;
        }

        using var uow = _uowManager.Begin();
        var symbolId = symbol.Id;
        bool exists = await _asyncExecuter.AnyAsync(
            (await _flowRepo.GetQueryableAsync()).Where(f =>
                f.SymbolId == symbolId && f.Date == date));

        if (exists)
        {
            _logger.LogDebug("Capital flow already exists for {Ticker} {Date}", symbol.Ticker, date);
            await uow.CompleteAsync();
            return;
        }

        await _flowRepo.InsertAsync(new SymbolCapitalFlow
        {
            SymbolId = symbol.Id,
            Date = date,
            SuperLargeInflow = data.SuperLargeInflow,
            SuperLargeOutflow = data.SuperLargeOutflow,
            LargeInflow = data.LargeInflow,
            LargeOutflow = data.LargeOutflow,
            MediumInflow = data.MediumInflow,
            MediumOutflow = data.MediumOutflow,
            SmallInflow = data.SmallInflow,
            SmallOutflow = data.SmallOutflow,
            CollectedAt = DateTime.UtcNow,
        }, autoSave: false);
        await uow.CompleteAsync();

        _logger.LogInformation("Capital flow saved for {Ticker} {Date}: large net={LargeNet:F0}",
            symbol.Ticker, date, data.LargeInflow - data.LargeOutflow);
    }

    private async Task RefreshFinancialsAsync(string authHeader, Symbol symbol)
    {
        var data = await _api.GetFinancialIndexAsync(authHeader, symbol.WebullTickerId);
        if (data == null) return;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        using var uow = _uowManager.Begin();
        var symbolId = symbol.Id;
        bool exists = await _asyncExecuter.AnyAsync(
            (await _financialRepo.GetQueryableAsync()).Where(f =>
                f.SymbolId == symbolId && f.Date == today));

        if (exists)
        {
            _logger.LogDebug("Financial snapshot already exists for {Ticker} {Date}", symbol.Ticker, today);
            await uow.CompleteAsync();
            return;
        }

        await _financialRepo.InsertAsync(new SymbolFinancialSnapshot
        {
            SymbolId = symbol.Id,
            Date = today,
            Pe = data.Pe,
            ForwardPe = data.ForwardPe,
            Eps = data.Eps,
            EstEps = data.EstEps,
            MarketCap = data.MarketCap,
            Volume = data.Volume,
            AvgVolume = data.AvgVolume,
            High52w = data.High52w,
            Low52w = data.Low52w,
            Beta = data.Beta,
            DividendYield = data.DividendYield,
            ShortFloat = data.ShortFloat,
            NextEarningsDate = data.NextEarningsDate,
            RawJson = data.RawJson,
            CollectedAt = DateTime.UtcNow,
        }, autoSave: false);
        await uow.CompleteAsync();

        _logger.LogInformation("Financial snapshot saved for {Ticker}: PE={Pe}, EPS={Eps}, MCap={MCap}",
            symbol.Ticker, data.Pe, data.Eps, data.MarketCap);
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
