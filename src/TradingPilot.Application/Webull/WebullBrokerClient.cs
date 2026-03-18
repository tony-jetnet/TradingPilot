using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingPilot.Symbols;
using TradingPilot.Trading;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Linq;
using Volo.Abp.Uow;

namespace TradingPilot.Webull;

/// <summary>
/// IBrokerClient implementation for Webull paper trading.
/// Maps between broker-agnostic Symbol-based DTOs and Webull's tickerId-based API.
/// </summary>
public class WebullBrokerClient : IBrokerClient
{
    private readonly WebullPaperTradingClient _client;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WebullBrokerClient> _logger;
    private long _accountId;
    private readonly string _authHeaderPath;

    // Symbol ↔ TickerId mapping (loaded from DB, cached)
    private readonly ConcurrentDictionary<string, long> _symbolToTickerId = new();
    private readonly ConcurrentDictionary<long, string> _tickerIdToSymbol = new();
    private bool _symbolsLoaded;

    // Auth state
    private string? _authHeaderJson;
    private DateTime _authLoadedAt;
    private static readonly TimeSpan AuthRefreshInterval = TimeSpan.FromMinutes(5);

    public string BrokerName => "WebullPaper";
    public bool IsAuthenticated => _authHeaderJson != null;

    public WebullBrokerClient(
        WebullPaperTradingClient client,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<WebullBrokerClient> logger)
    {
        _client = client;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _accountId = configuration.GetValue<long>("Broker:AccountId", 58264537);
        _authHeaderPath = configuration.GetValue<string>("Broker:AuthHeaderPath")
            ?? @"D:\Third-Parties\WebullHook\auth_header.json";
    }

    public async Task<BrokerAccount?> GetAccountAsync()
    {
        RefreshAuthIfNeeded();
        if (_authHeaderJson == null) return null;
        await EnsureSymbolsLoadedAsync();

        var account = await _client.GetAccountAsync(_authHeaderJson, _accountId);
        if (account == null)
        {
            // Account may have been reset — try to discover the new ID
            var newId = await _client.GetAccountIdAsync(_authHeaderJson);
            if (newId.HasValue && newId.Value != _accountId)
            {
                _logger.LogWarning("Paper account reset detected. Old={OldId}, New={NewId}", _accountId, newId.Value);
                _accountId = newId.Value;
                account = await _client.GetAccountAsync(_authHeaderJson, _accountId);
            }
        }
        if (account == null) return null;

        // Compute day P&L from today's filled orders
        decimal dayPnl = await ComputeDayPnlAsync();

        return new BrokerAccount
        {
            NetLiquidation = account.NetLiquidation,
            UsableCash = account.UsableCash,
            DayPnl = dayPnl,
            Positions = account.Positions
                .Select(p => new BrokerPosition
                {
                    Symbol = ResolveSymbol(p.TickerId, p.Ticker),
                    Quantity = p.Quantity,
                    AvgPrice = p.CostPrice,
                    MarketValue = p.MarketValue,
                    UnrealizedPnl = p.UnrealizedPnl,
                })
                .Where(p => !p.Symbol.StartsWith("UNKNOWN_"))
                .ToList(),
        };
    }

    public async Task<BrokerOrderResult> PlaceOrderAsync(BrokerOrderRequest order)
    {
        RefreshAuthIfNeeded();
        if (_authHeaderJson == null)
            return new BrokerOrderResult { Success = false, Error = "No auth header" };

        await EnsureSymbolsLoadedAsync();
        long tickerId = ResolveTickerId(order.Symbol);
        if (tickerId == 0)
            return new BrokerOrderResult { Success = false, Error = $"Unknown symbol: {order.Symbol}" };

        var webullOrder = new PaperOrderRequest
        {
            Action = order.Action,
            TickerId = tickerId,
            Quantity = order.Quantity,
            LimitPrice = order.LimitPrice ?? 0,
            OrderType = order.Type switch
            {
                OrderType.Market => "MKT",
                OrderType.Limit => "LMT",
                OrderType.StopLimit => "STP LMT",
                _ => "LMT",
            },
            OutsideRegularTradingHour = order.ExtendedHours,
            TimeInForce = order.TimeInForce,
        };

        var result = await _client.PlaceOrderAsync(_authHeaderJson, _accountId, webullOrder);
        if (result == null)
            return new BrokerOrderResult { Success = false, Error = "No response from Webull" };

        return new BrokerOrderResult
        {
            Success = result.Success,
            OrderId = result.OrderId?.ToString(),
            Error = result.ErrorMessage,
        };
    }

    public async Task<List<BrokerOrder>> GetOrdersAsync(int pageSize = 200)
    {
        RefreshAuthIfNeeded();
        if (_authHeaderJson == null) return [];
        await EnsureSymbolsLoadedAsync();

        var orders = await _client.GetOrdersAsync(_authHeaderJson, _accountId, pageSize);
        if (orders == null) return [];
        return orders.Select(o => new BrokerOrder
        {
            OrderId = o.OrderId.ToString(),
            Symbol = ResolveSymbol(o.TickerId),
            Action = o.Action,
            Status = NormalizeStatus(o.Status),
            Quantity = o.Quantity,
            LimitPrice = o.LimitPrice,
            FilledPrice = o.FilledPrice,
            FilledTime = o.FilledTime,
        }).ToList();
    }

    public async Task<BrokerOrder?> GetOrderAsync(string orderId)
    {
        // Webull doesn't have a single-order endpoint — fetch all and filter
        var orders = await GetOrdersAsync(500);
        return orders.FirstOrDefault(o => o.OrderId == orderId);
    }

    public async Task<bool> CancelOrderAsync(string orderId)
    {
        RefreshAuthIfNeeded();
        if (_authHeaderJson == null) return false;

        if (!long.TryParse(orderId, out var numericId)) return false;
        return await _client.CancelOrderAsync(_authHeaderJson, _accountId, numericId);
    }

    /// <summary>
    /// Resolve Webull tickerId to symbol name. Public for signal pipeline integration.
    /// </summary>
    public string ResolveSymbol(long tickerId, string? fallback = null)
    {
        // Webull sometimes returns tickerId=0 in position/order responses — skip the warning
        if (tickerId <= 0)
        {
            if (!string.IsNullOrWhiteSpace(fallback) && !long.TryParse(fallback, out _))
                return fallback;
            return string.Empty;
        }

        if (_tickerIdToSymbol.TryGetValue(tickerId, out var symbol))
            return symbol;

        // Use the Webull-provided ticker name if available
        if (!string.IsNullOrWhiteSpace(fallback) && !long.TryParse(fallback, out _))
            return fallback;

        // Last resort: log warning and return empty to signal unknown
        _logger.LogWarning("Unknown tickerId {TickerId} with no symbol mapping", tickerId);
        return $"UNKNOWN_{tickerId}";
    }

    /// <summary>
    /// Resolve symbol name to Webull tickerId. Public for signal pipeline integration.
    /// </summary>
    public long ResolveInternalId(string symbol)
    {
        return _symbolToTickerId.GetValueOrDefault(symbol, 0);
    }

    public long ResolveTickerId(string symbol) => ResolveInternalId(symbol);

    private async Task<decimal> ComputeDayPnlAsync()
    {
        try
        {
            var orders = await GetOrdersAsync(500);
            var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            var todayEt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, eastern).Date;

            var todayFilled = orders
                .Where(o => o.Status == "Filled" && o.FilledPrice.HasValue && o.FilledTime.HasValue)
                .Where(o => TimeZoneInfo.ConvertTimeFromUtc(o.FilledTime!.Value, eastern).Date == todayEt)
                .OrderBy(o => o.FilledTime)
                .ToList();

            // Pair entry/exit by symbol
            var positions = new Dictionary<string, (string Action, decimal Price, int Qty)>();
            decimal totalPnl = 0;

            foreach (var order in todayFilled)
            {
                if (positions.TryGetValue(order.Symbol, out var entry))
                {
                    decimal pnl = entry.Action == "BUY"
                        ? (order.FilledPrice!.Value - entry.Price) * entry.Qty
                        : (entry.Price - order.FilledPrice!.Value) * entry.Qty;
                    totalPnl += pnl;
                    positions.Remove(order.Symbol);
                }
                else
                {
                    positions[order.Symbol] = (order.Action, order.FilledPrice!.Value, order.Quantity);
                }
            }

            return totalPnl;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compute day P&L from broker orders");
            return 0;
        }
    }

    private static string NormalizeStatus(string webullStatus)
    {
        return webullStatus switch
        {
            "Filled" => "Filled",
            "Working" => "Working",
            "Cancelled" => "Cancelled",
            "Pending" => "Working",
            "PendingCancel" => "Cancelled",
            "Failed" => "Rejected",
            "Rejected" => "Rejected",
            "Expired" => "Expired",
            _ => webullStatus,
        };
    }

    private async Task EnsureSymbolsLoadedAsync()
    {
        if (_symbolsLoaded) return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            var mappingRepo = scope.ServiceProvider.GetRequiredService<IRepository<BrokerSymbolMapping, Guid>>();
            var symbolRepo = scope.ServiceProvider.GetRequiredService<IRepository<Symbol, string>>();
            var asyncExecuter = scope.ServiceProvider.GetRequiredService<IAsyncQueryableExecuter>();

            using var uow = uowManager.Begin();

            // Load from BrokerSymbolMappings table first
            var mappings = await asyncExecuter.ToListAsync(
                (await mappingRepo.GetQueryableAsync()).Where(m => m.BrokerName == BrokerName));

            if (mappings.Count > 0)
            {
                foreach (var m in mappings)
                {
                    if (long.TryParse(m.BrokerSymbolId, out long tickerId))
                    {
                        _symbolToTickerId[m.SymbolId] = tickerId;
                        _tickerIdToSymbol[tickerId] = m.SymbolId;
                    }
                }
                _logger.LogInformation("WebullBrokerClient: loaded {Count} symbol mappings from BrokerSymbolMappings", mappings.Count);
            }
            else
            {
                // Fallback: migrate from legacy Symbol.WebullTickerId column
                var symbols = await asyncExecuter.ToListAsync(
                    (await symbolRepo.GetQueryableAsync()).Where(s => s.IsWatched && s.WebullTickerId > 0));

                foreach (var s in symbols)
                {
                    _symbolToTickerId[s.Id] = s.WebullTickerId;
                    _tickerIdToSymbol[s.WebullTickerId] = s.Id;

                    // Auto-migrate: insert into BrokerSymbolMappings
                    await mappingRepo.InsertAsync(new BrokerSymbolMapping
                    {
                        SymbolId = s.Id,
                        BrokerName = BrokerName,
                        BrokerSymbolId = s.WebullTickerId.ToString(),
                    }, autoSave: false);
                }
                _logger.LogWarning("WebullBrokerClient: migrated {Count} symbol mappings from legacy WebullTickerId column", symbols.Count);
            }

            await uow.CompleteAsync();
            _symbolsLoaded = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load symbol mappings");
        }
    }

    private void RefreshAuthIfNeeded()
    {
        if (_authHeaderJson != null && (DateTime.UtcNow - _authLoadedAt) < AuthRefreshInterval)
            return;

        // Try in-memory captured header first
        var header = WebullHookAppService.CapturedAuthHeader;
        if (!string.IsNullOrWhiteSpace(header))
        {
            _authHeaderJson = header;
            _authLoadedAt = DateTime.UtcNow;
            return;
        }

        // Fall back to file
        try
        {
            if (File.Exists(_authHeaderPath))
            {
                var content = File.ReadAllText(_authHeaderPath).Trim();
                if (!string.IsNullOrWhiteSpace(content))
                {
                    _authHeaderJson = content;
                    _authLoadedAt = DateTime.UtcNow;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load auth header from {Path}", _authHeaderPath);
        }
    }
}
