using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingPilot.Symbols;
using TradingPilot.Trading;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Linq;
using Volo.Abp.Uow;

namespace TradingPilot.Questrade;

/// <summary>
/// IBrokerClient implementation for Questrade live/practice trading.
/// Uses REST API for order management, WebSocket streaming for order status notifications.
/// OAuth2 refresh token flow for authentication.
/// </summary>
public class QuestradeBrokerClient : IBrokerClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<QuestradeBrokerClient> _logger;
    private readonly string? _initialRefreshToken;
    private readonly string _appSettingsPath;

    // OAuth2 state
    private string? _accessToken;
    private string? _refreshToken;
    private string? _apiServer; // e.g. "https://api01.iq.questrade.com"
    private DateTime _tokenExpiresAt;
    private string? _accountNumber;

    // Symbol ↔ SymbolId mapping
    private readonly ConcurrentDictionary<string, int> _symbolToId = new();
    private readonly ConcurrentDictionary<int, string> _idToSymbol = new();
    private bool _symbolsLoaded;

    // Streaming: order status cache (populated by WebSocket notifications)
    private readonly ConcurrentDictionary<string, BrokerOrder> _orderCache = new();
    private System.Net.WebSockets.ClientWebSocket? _streamSocket;
    private CancellationTokenSource? _streamCts;
    private Task? _streamTask;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string BrokerName => "Questrade";
    public bool IsAuthenticated => _accessToken != null && DateTime.UtcNow < _tokenExpiresAt;

    public QuestradeBrokerClient(
        HttpClient http,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<QuestradeBrokerClient> logger)
    {
        _http = http;
        _scopeFactory = scopeFactory;
        _logger = logger;
        // appsettings.json path for persisting refresh tokens
        var contentRoot = configuration.GetValue<string>("ContentRoot")
            ?? Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly()?.Location)
            ?? ".";
        _appSettingsPath = Path.Combine(contentRoot, "appsettings.json");

        _initialRefreshToken = configuration.GetValue<string>("BrokerQuestrade:RefreshToken");
        _refreshToken = _initialRefreshToken;
        _accountNumber = configuration.GetValue<string>("BrokerQuestrade:AccountNumber");
    }

    // ═══════════════════════════════════════════════════════════
    // IBrokerClient implementation
    // ═══════════════════════════════════════════════════════════

    public async Task<BrokerAccount?> GetAccountAsync()
    {
        if (!await EnsureAuthenticatedAsync()) return null;
        await EnsureSymbolsLoadedAsync();

        try
        {
            // Get positions
            var posResponse = await GetAsync<QuestradePositionsResponse>(
                $"/v1/accounts/{_accountNumber}/positions");

            // Get balances
            var balResponse = await GetAsync<QuestradeBalancesResponse>(
                $"/v1/accounts/{_accountNumber}/balances");

            // Get today's orders for P&L computation
            decimal dayPnl = 0;
            if (posResponse?.Positions != null)
                dayPnl = posResponse.Positions.Sum(p => p.ClosedPnl) +
                         posResponse.Positions.Sum(p => p.OpenPnl);

            var combined = balResponse?.CombinedBalances?.FirstOrDefault();
            var perCurrency = balResponse?.PerCurrencyBalances?.FirstOrDefault(b => b.Currency == "USD")
                ?? balResponse?.PerCurrencyBalances?.FirstOrDefault();

            return new BrokerAccount
            {
                NetLiquidation = combined?.TotalEquity ?? perCurrency?.TotalEquity ?? 0,
                UsableCash = combined?.Cash ?? perCurrency?.Cash ?? 0,
                DayPnl = dayPnl,
                Positions = (posResponse?.Positions ?? [])
                    .Where(p => p.OpenQuantity != 0)
                    .Select(p => new BrokerPosition
                    {
                        Symbol = p.Symbol,
                        Quantity = (int)p.OpenQuantity,
                        AvgPrice = p.AverageEntryPrice,
                        MarketValue = p.CurrentMarketValue,
                        UnrealizedPnl = p.OpenPnl,
                    }).ToList(),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Questrade: GetAccountAsync failed");
            return null;
        }
    }

    public async Task<BrokerOrderResult> PlaceOrderAsync(BrokerOrderRequest order)
    {
        if (!await EnsureAuthenticatedAsync())
            return new BrokerOrderResult { Success = false, Error = "Not authenticated" };

        await EnsureSymbolsLoadedAsync();
        int symbolId = ResolveQuestradeSymbolId(order.Symbol);
        if (symbolId == 0)
        {
            // Try to search for the symbol
            symbolId = await SearchAndCacheSymbolAsync(order.Symbol);
            if (symbolId == 0)
                return new BrokerOrderResult { Success = false, Error = $"Unknown symbol: {order.Symbol}" };
        }

        try
        {
            var body = new
            {
                symbolId,
                quantity = order.Quantity,
                limitPrice = order.Type == OrderType.Market ? (decimal?)null : order.LimitPrice,
                orderType = order.Type switch
                {
                    OrderType.Market => "Market",
                    OrderType.Limit => "Limit",
                    OrderType.StopLimit => "StopLimit",
                    OrderType.TrailingStop => "TrailingStopLimit",
                    _ => "Limit",
                },
                timeInForce = order.TimeInForce switch
                {
                    "DAY" => "Day",
                    "GTC" => "GoodTillCanceled",
                    _ => "Day",
                },
                action = order.Action == "BUY" ? "Buy" : "Sell",
                primaryRoute = "AUTO",
                secondaryRoute = "AUTO",
            };

            var response = await PostAsync<QuestradeOrderResponse>(
                $"/v1/accounts/{_accountNumber}/orders", body);

            if (response?.OrderId > 0)
            {
                return new BrokerOrderResult
                {
                    Success = true,
                    OrderId = response.OrderId.ToString(),
                };
            }

            return new BrokerOrderResult
            {
                Success = false,
                Error = "No orderId in response",
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Questrade: PlaceOrderAsync failed for {Symbol}", order.Symbol);
            return new BrokerOrderResult { Success = false, Error = ex.Message };
        }
    }

    public async Task<List<BrokerOrder>> GetOrdersAsync(int pageSize = 200)
    {
        if (!await EnsureAuthenticatedAsync()) return [];
        await EnsureSymbolsLoadedAsync();

        try
        {
            // Get today's orders
            var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            var todayEt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, eastern).Date;
            var startTime = todayEt.ToString("yyyy-MM-ddT00:00:00-05:00");
            var endTime = todayEt.AddDays(1).ToString("yyyy-MM-ddT00:00:00-05:00");

            var response = await GetAsync<QuestradeOrdersResponse>(
                $"/v1/accounts/{_accountNumber}/orders?startTime={startTime}&endTime={endTime}&stateFilter=All");

            return (response?.Orders ?? []).Select(o => MapOrder(o)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Questrade: GetOrdersAsync failed");
            return [];
        }
    }

    public async Task<BrokerOrder?> GetOrderAsync(string orderId)
    {
        // Check streaming cache first (instant)
        if (_orderCache.TryGetValue(orderId, out var cached))
            return cached;

        // Fall back to REST
        if (!await EnsureAuthenticatedAsync()) return null;

        try
        {
            var response = await GetAsync<QuestradeOrdersResponse>(
                $"/v1/accounts/{_accountNumber}/orders/{orderId}");

            var order = response?.Orders?.FirstOrDefault();
            if (order == null) return null;

            var mapped = MapOrder(order);
            _orderCache[orderId] = mapped; // Cache it
            return mapped;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Questrade: GetOrderAsync failed for orderId={OrderId}", orderId);
            return null;
        }
    }

    public async Task<bool> CancelOrderAsync(string orderId)
    {
        if (!await EnsureAuthenticatedAsync()) return false;

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Delete,
                $"{_apiServer}/v1/accounts/{_accountNumber}/orders/{orderId}");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

            var response = await _http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Questrade: cancelled order {OrderId}", orderId);
                return true;
            }

            var body = await response.Content.ReadAsStringAsync();
            _logger.LogError("Questrade: CancelOrder failed {Status}: {Body}", (int)response.StatusCode, body[..Math.Min(body.Length, 200)]);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Questrade: CancelOrderAsync failed for orderId={OrderId}", orderId);
            return false;
        }
    }

    public long ResolveInternalId(string symbol)
    {
        return _symbolToId.GetValueOrDefault(symbol, 0);
    }

    // ═══════════════════════════════════════════════════════════
    // Token Persistence (refresh token is single-use, must survive restart)
    // ═══════════════════════════════════════════════════════════

    private void PersistToken(string refreshToken)
    {
        try
        {
            if (!File.Exists(_appSettingsPath)) return;

            var json = File.ReadAllText(_appSettingsPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Rebuild JSON with updated refresh token
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name == "BrokerQuestrade")
                    {
                        writer.WritePropertyName("BrokerQuestrade");
                        writer.WriteStartObject();
                        foreach (var inner in prop.Value.EnumerateObject())
                        {
                            if (inner.Name == "RefreshToken")
                                writer.WriteString("RefreshToken", refreshToken);
                            else
                                inner.WriteTo(writer);
                        }
                        writer.WriteEndObject();
                    }
                    else
                    {
                        prop.WriteTo(writer);
                    }
                }
                writer.WriteEndObject();
            }

            File.WriteAllText(_appSettingsPath, System.Text.Encoding.UTF8.GetString(ms.ToArray()));
            _logger.LogInformation("Questrade: persisted refresh token to appsettings.json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Questrade: FAILED to persist refresh token to appsettings.json");
        }
    }

    // ═══════════════════════════════════════════════════════════
    // OAuth2 Authentication
    // ═══════════════════════════════════════════════════════════

    private async Task<bool> EnsureAuthenticatedAsync()
    {
        if (IsAuthenticated) return true;
        if (string.IsNullOrEmpty(_refreshToken))
        {
            _logger.LogWarning("Questrade: no refresh token configured");
            return false;
        }

        try
        {
            var response = await _http.GetAsync(
                $"https://login.questrade.com/oauth2/token?grant_type=refresh_token&refresh_token={_refreshToken}");

            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync();
                _logger.LogError("Questrade: token refresh failed {Status}: {Body}",
                    (int)response.StatusCode, errBody[..Math.Min(errBody.Length, 300)]);
                return false;
            }

            var token = await response.Content.ReadFromJsonAsync<QuestradeTokenResponse>(JsonOptions);
            if (token == null) return false;

            _accessToken = token.AccessToken;
            _refreshToken = token.RefreshToken; // New refresh token (old one is now invalid)
            _apiServer = token.ApiServer?.TrimEnd('/');
            _tokenExpiresAt = DateTime.UtcNow.AddSeconds(token.ExpiresIn - 30); // 30s buffer

            // Persist new refresh token to disk (single-use, must survive restart)
            PersistToken(token.RefreshToken);

            _logger.LogInformation("Questrade: authenticated, apiServer={ApiServer}, expires in {Seconds}s",
                _apiServer, token.ExpiresIn);

            // Discover account number if not configured
            if (string.IsNullOrEmpty(_accountNumber))
                await DiscoverAccountNumberAsync();

            // Start streaming if not already connected
            _ = StartStreamingAsync();

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Questrade: authentication failed");
            return false;
        }
    }

    private async Task DiscoverAccountNumberAsync()
    {
        var accounts = await GetAsync<QuestradeAccountsResponse>("/v1/accounts");
        var primary = accounts?.Accounts?.FirstOrDefault(a => a.IsPrimary)
            ?? accounts?.Accounts?.FirstOrDefault();

        if (primary != null)
        {
            _accountNumber = primary.Number;
            _logger.LogInformation("Questrade: discovered account {Number} ({Type})",
                primary.Number, primary.Type);
        }
    }

    // ═══════════════════════════════════════════════════════════
    // WebSocket Streaming (order notifications)
    // ═══════════════════════════════════════════════════════════

    private async Task StartStreamingAsync()
    {
        if (_streamSocket != null) return;

        try
        {
            // Get streaming port
            var portResponse = await GetAsync<QuestradeStreamPortResponse>("/v1/notifications?mode=WebSocket");
            if (portResponse == null || portResponse.StreamPort == 0)
            {
                _logger.LogWarning("Questrade: streaming port not available");
                return;
            }

            var wsUri = new Uri($"wss://{new Uri(_apiServer!).Host}:{portResponse.StreamPort}");
            _streamSocket = new System.Net.WebSockets.ClientWebSocket();
            _streamCts = new CancellationTokenSource();

            await _streamSocket.ConnectAsync(wsUri, _streamCts.Token);

            // Authenticate: send access token without "Bearer" prefix
            var tokenBytes = Encoding.UTF8.GetBytes(_accessToken!);
            await _streamSocket.SendAsync(tokenBytes, System.Net.WebSockets.WebSocketMessageType.Text, true, _streamCts.Token);

            // Read auth response
            var buffer = new byte[4096];
            var result = await _streamSocket.ReceiveAsync(buffer, _streamCts.Token);
            var authResponse = Encoding.UTF8.GetString(buffer, 0, result.Count);

            if (authResponse.Contains("\"success\":true", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Questrade: streaming connected on port {Port}", portResponse.StreamPort);
                _streamTask = Task.Run(() => StreamListenLoopAsync(_streamCts.Token));
            }
            else
            {
                _logger.LogError("Questrade: streaming auth failed: {Response}", authResponse);
                _streamSocket.Dispose();
                _streamSocket = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Questrade: failed to start streaming");
            _streamSocket?.Dispose();
            _streamSocket = null;
        }
    }

    private async Task StreamListenLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16384];
        var sb = new StringBuilder();

        try
        {
            while (!ct.IsCancellationRequested && _streamSocket?.State == System.Net.WebSockets.WebSocketState.Open)
            {
                sb.Clear();
                System.Net.WebSockets.WebSocketReceiveResult result;

                do
                {
                    result = await _streamSocket.ReceiveAsync(buffer, ct);
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                    break;

                ProcessStreamMessage(sb.ToString());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Questrade: streaming connection lost");
        }

        _logger.LogWarning("Questrade: streaming disconnected");
        _streamSocket?.Dispose();
        _streamSocket = null;
    }

    private void ProcessStreamMessage(string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;

            if (root.TryGetProperty("orders", out var ordersArr) && ordersArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var orderEl in ordersArr.EnumerateArray())
                {
                    var order = JsonSerializer.Deserialize<QuestradeOrder>(orderEl.GetRawText(), JsonOptions);
                    if (order != null)
                    {
                        var mapped = MapOrder(order);
                        _orderCache[mapped.OrderId] = mapped;
                        _logger.LogDebug("Questrade stream: order {OrderId} {Symbol} {State}",
                            order.Id, order.Symbol, order.State);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Questrade: failed to parse stream message");
        }
    }

    // ═══════════════════════════════════════════════════════════
    // Symbol Resolution
    // ═══════════════════════════════════════════════════════════

    private int ResolveQuestradeSymbolId(string symbol)
    {
        return _symbolToId.GetValueOrDefault(symbol, 0);
    }

    private async Task<int> SearchAndCacheSymbolAsync(string symbol)
    {
        try
        {
            var response = await GetAsync<QuestradeSymbolSearchResponse>(
                $"/v1/symbols/search?prefix={symbol}");

            var match = response?.Symbols?.FirstOrDefault(s =>
                s.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase) && s.IsTradable);

            if (match != null)
            {
                _symbolToId[symbol] = match.SymbolId;
                _idToSymbol[match.SymbolId] = symbol;
                _logger.LogInformation("Questrade: resolved {Symbol} → symbolId={SymbolId}", symbol, match.SymbolId);
                return match.SymbolId;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Questrade: symbol search failed for {Symbol}", symbol);
        }
        return 0;
    }

    private async Task EnsureSymbolsLoadedAsync()
    {
        if (_symbolsLoaded) return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            var mappingRepo = scope.ServiceProvider.GetRequiredService<IRepository<BrokerSymbolMapping, Guid>>();
            var asyncExecuter = scope.ServiceProvider.GetRequiredService<IAsyncQueryableExecuter>();

            using var uow = uowManager.Begin();
            var mappings = await asyncExecuter.ToListAsync(
                (await mappingRepo.GetQueryableAsync()).Where(m => m.BrokerName == BrokerName));
            await uow.CompleteAsync();

            foreach (var m in mappings)
            {
                if (int.TryParse(m.BrokerSymbolId, out int symbolId))
                {
                    _symbolToId[m.SymbolId] = symbolId;
                    _idToSymbol[symbolId] = m.SymbolId;
                }
            }

            _symbolsLoaded = mappings.Count > 0;
            _logger.LogInformation("Questrade: loaded {Count} symbol mappings", mappings.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Questrade: failed to load symbol mappings");
        }
    }

    // ═══════════════════════════════════════════════════════════
    // HTTP Helpers
    // ═══════════════════════════════════════════════════════════

    private async Task<T?> GetAsync<T>(string path) where T : class
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{_apiServer}{path}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

        var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            _logger.LogError("Questrade GET {Path} failed {Status}: {Body}",
                path, (int)response.StatusCode, body[..Math.Min(body.Length, 300)]);

            // Token expired — clear so next call re-authenticates
            if ((int)response.StatusCode == 401)
                _accessToken = null;

            return null;
        }

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions);
    }

    private async Task<T?> PostAsync<T>(string path, object body) where T : class
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_apiServer}{path}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);
        request.Content = new StringContent(
            JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");

        var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var respBody = await response.Content.ReadAsStringAsync();
            _logger.LogError("Questrade POST {Path} failed {Status}: {Body}",
                path, (int)response.StatusCode, respBody[..Math.Min(respBody.Length, 300)]);

            if ((int)response.StatusCode == 401)
                _accessToken = null;

            return null;
        }

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions);
    }

    // ═══════════════════════════════════════════════════════════
    // Order Mapping
    // ═══════════════════════════════════════════════════════════

    private BrokerOrder MapOrder(QuestradeOrder o)
    {
        string symbol = o.Symbol;
        // Strip exchange suffix (e.g. "AMD.US" → "AMD", "TD.TO" → "TD")
        if (symbol.Contains('.'))
            symbol = symbol[..symbol.IndexOf('.')];

        // Cache the symbolId mapping
        if (o.SymbolId > 0 && !_idToSymbol.ContainsKey(o.SymbolId))
        {
            _idToSymbol[o.SymbolId] = symbol;
            _symbolToId[symbol] = o.SymbolId;
        }

        return new BrokerOrder
        {
            OrderId = o.Id.ToString(),
            Symbol = symbol,
            Action = o.Side switch
            {
                "Buy" => "BUY",
                "Sell" => "SELL",
                _ => o.Side?.ToUpper() ?? "",
            },
            Status = o.State switch
            {
                "Executed" => "Filled",
                "Filled" => "Filled",
                "Canceled" => "Cancelled",
                "Expired" => "Expired",
                "Rejected" => "Rejected",
                "Accepted" => "Working",
                "Pending" => "Working",
                "Queued" => "Working",
                _ => o.State ?? "",
            },
            Quantity = (int)(o.FilledQuantity > 0 ? o.FilledQuantity : o.TotalQuantity),
            LimitPrice = o.LimitPrice,
            FilledPrice = o.AvgExecPrice > 0 ? o.AvgExecPrice : null,
            FilledTime = o.UpdateTime,
        };
    }

    public void Dispose()
    {
        _streamCts?.Cancel();
        _streamSocket?.Dispose();
    }
}

// ═══════════════════════════════════════════════════════════
// Questrade API DTOs (internal)
// ═══════════════════════════════════════════════════════════

internal class QuestradeTokenResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("token_type")] public string TokenType { get; set; } = "";
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = "";
    [JsonPropertyName("api_server")] public string ApiServer { get; set; } = "";
}

internal class QuestradeAccountsResponse
{
    public List<QuestradeAccount>? Accounts { get; set; }
}

internal class QuestradeAccount
{
    public string Type { get; set; } = "";
    public string Number { get; set; } = "";
    public string Status { get; set; } = "";
    public bool IsPrimary { get; set; }
}

internal class QuestradePositionsResponse
{
    public List<QuestradePosition>? Positions { get; set; }
}

internal class QuestradePosition
{
    public string Symbol { get; set; } = "";
    public int SymbolId { get; set; }
    public double OpenQuantity { get; set; }
    public decimal CurrentMarketValue { get; set; }
    public decimal CurrentPrice { get; set; }
    public decimal AverageEntryPrice { get; set; }
    public decimal ClosedPnl { get; set; }
    public decimal OpenPnl { get; set; }
}

internal class QuestradeBalancesResponse
{
    public List<QuestradeBalance>? PerCurrencyBalances { get; set; }
    public List<QuestradeBalance>? CombinedBalances { get; set; }
}

internal class QuestradeBalance
{
    public string Currency { get; set; } = "";
    public decimal Cash { get; set; }
    public decimal MarketValue { get; set; }
    public decimal TotalEquity { get; set; }
    public decimal BuyingPower { get; set; }
}

internal class QuestradeOrdersResponse
{
    public List<QuestradeOrder>? Orders { get; set; }
}

internal class QuestradeOrder
{
    public int Id { get; set; }
    public string Symbol { get; set; } = "";
    public int SymbolId { get; set; }
    public double TotalQuantity { get; set; }
    public double FilledQuantity { get; set; }
    public double CanceledQuantity { get; set; }
    public string? Side { get; set; }
    public string? State { get; set; }
    public decimal? LimitPrice { get; set; }
    public decimal? StopPrice { get; set; }
    public decimal? AvgExecPrice { get; set; }
    public decimal? LastExecPrice { get; set; }
    public decimal CommissionCharged { get; set; }
    public DateTime? CreationTime { get; set; }
    public DateTime? UpdateTime { get; set; }
}

internal class QuestradeOrderResponse
{
    public int OrderId { get; set; }
    public List<QuestradeOrder>? Orders { get; set; }
}

internal class QuestradeStreamPortResponse
{
    public int StreamPort { get; set; }
}

internal class QuestradeSymbolSearchResponse
{
    public List<QuestradeSymbolResult>? Symbols { get; set; }
}

internal class QuestradeSymbolResult
{
    public string Symbol { get; set; } = "";
    public int SymbolId { get; set; }
    public string Description { get; set; } = "";
    public bool IsTradable { get; set; }
    public string Currency { get; set; } = "";
}
