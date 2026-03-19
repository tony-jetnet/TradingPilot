using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace TradingPilot.Webull;

public class WebullPaperTradingClient
{
    private readonly HttpClient _http;
    private readonly ILogger<WebullPaperTradingClient> _logger;
    private const string FintechBase = "https://act.webullfintech.com/webull-paper-center/api";
    private const string BrokerBase = "https://act.webullbroker.com/webull-paper-center/api";

    public WebullPaperTradingClient(HttpClient http, ILogger<WebullPaperTradingClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<PaperAccountInfo?> GetAccountAsync(string authHeaderJson, long accountId)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"{FintechBase}/paper/1/acc/{accountId}");
            ApplyAuthHeaders(request, authHeaderJson);

            var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync();
                _logger.LogError("GetAccount failed {Status}: {Body}", (int)response.StatusCode, body[..Math.Min(body.Length, 500)]);
                return null;
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            _logger.LogDebug("GetAccount raw: {Json}", json.ToString()[..Math.Min(json.ToString().Length, 500)]);

            var info = new PaperAccountInfo { AccountId = accountId };

            if (json.TryGetProperty("netLiquidation", out var nl))
                info.NetLiquidation = ParseDecimal(nl);
            // usableCash is in accountMembers array
            if (json.TryGetProperty("accountMembers", out var members) && members.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in members.EnumerateArray())
                {
                    var key = m.TryGetProperty("key", out var k) ? k.GetString() : "";
                    var val = m.TryGetProperty("value", out var v) ? ParseDecimal(v) : 0;
                    if (key == "usableCash") info.UsableCash = val;
                }
            }
            if (json.TryGetProperty("totalProfitLoss", out var tpl))
                info.TotalProfitLoss = ParseDecimal(tpl);

            if (json.TryGetProperty("positions", out var positions) && positions.ValueKind == JsonValueKind.Array)
            {
                foreach (var pos in positions.EnumerateArray())
                {
                    // Extract ticker symbol — can be string or object with "symbol" field
                    string tickerName = "";
                    long tickerId = 0;
                    if (pos.TryGetProperty("ticker", out var t))
                    {
                        if (t.ValueKind == JsonValueKind.String)
                            tickerName = t.GetString() ?? "";
                        else if (t.ValueKind == JsonValueKind.Object)
                        {
                            if (t.TryGetProperty("symbol", out var sym))
                                tickerName = sym.GetString() ?? "";
                            else if (t.TryGetProperty("tickerName", out var tn))
                                tickerName = tn.GetString() ?? "";
                            if (t.TryGetProperty("tickerId", out var ttid))
                                tickerId = ttid.GetInt64();
                        }
                    }
                    if (tickerId == 0 && pos.TryGetProperty("tickerId", out var tid2))
                        tickerId = tid2.GetInt64();

                    info.Positions.Add(new PaperPosition
                    {
                        TickerId = tickerId,
                        Ticker = tickerName,
                        Quantity = pos.TryGetProperty("position", out var q) ? (int)ParseDecimal(q) : 0,
                        CostPrice = pos.TryGetProperty("costPrice", out var cp) ? ParseDecimal(cp) : 0,
                        LastPrice = pos.TryGetProperty("lastPrice", out var lp2) ? ParseDecimal(lp2) : 0,
                        MarketValue = pos.TryGetProperty("marketValue", out var mv) ? ParseDecimal(mv) : 0,
                        UnrealizedPnl = pos.TryGetProperty("unrealizedProfitLoss", out var pnl) ? ParseDecimal(pnl) : 0,
                    });
                }
            }

            if (json.TryGetProperty("openOrders", out var orders) && orders.ValueKind == JsonValueKind.Array)
            {
                foreach (var ord in orders.EnumerateArray())
                {
                    info.OpenOrders.Add(ParseOrder(ord));
                }
            }

            return info;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetAccount exception for accountId={AccountId}", accountId);
            return null;
        }
    }

    public async Task<long?> GetAccountIdAsync(string authHeaderJson)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"{FintechBase}/myaccounts/true");
            ApplyAuthHeaders(request, authHeaderJson);

            var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync();
                _logger.LogError("GetAccountId failed {Status}: {Body}", (int)response.StatusCode, body[..Math.Min(body.Length, 500)]);
                return null;
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (json.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in json.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var id))
                        return id.GetInt64();
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetAccountId exception");
            return null;
        }
    }

    public async Task<PaperOrderResult?> PlaceOrderAsync(string authHeaderJson, long accountId, PaperOrderRequest order)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post,
                $"{BrokerBase}/paper/1/acc/{accountId}/orderop/place/{order.TickerId}");
            ApplyAuthHeaders(request, authHeaderJson);

            var body = new
            {
                action = order.Action,
                lmtPrice = order.LimitPrice,
                orderType = order.OrderType,
                outsideRegularTradingHour = order.OutsideRegularTradingHour,
                quantity = order.Quantity,
                serialId = Guid.NewGuid().ToString(),
                tickerId = order.TickerId,
                timeInForce = order.TimeInForce,
            };

            request.Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json");

            var response = await _http.SendAsync(request);
            string responseBody = await response.Content.ReadAsStringAsync();

            _logger.LogDebug("PlaceOrder response {Status}: {Body}", (int)response.StatusCode, responseBody[..Math.Min(responseBody.Length, 500)]);

            if (!response.IsSuccessStatusCode)
            {
                return new PaperOrderResult
                {
                    Success = false,
                    ErrorMessage = $"HTTP {(int)response.StatusCode}: {responseBody[..Math.Min(responseBody.Length, 200)]}"
                };
            }

            // Parse response for order ID
            var json = JsonDocument.Parse(responseBody).RootElement;
            long? orderId = null;
            if (json.TryGetProperty("orderId", out var oid))
                orderId = oid.GetInt64();
            else if (json.TryGetProperty("data", out var data) && data.TryGetProperty("orderId", out var oid2))
                orderId = oid2.GetInt64();

            return new PaperOrderResult
            {
                Success = true,
                OrderId = orderId
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PlaceOrder exception for tickerId={TickerId}", order.TickerId);
            return new PaperOrderResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<List<PaperOrder>> GetOrdersAsync(string authHeaderJson, long accountId, int pageSize = 50)
    {
        var result = new List<PaperOrder>();
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"{BrokerBase}/paper/1/acc/{accountId}/order?startTime=1970-0-1&dateType=ORDER&pageSize={pageSize}&status=");
            ApplyAuthHeaders(request, authHeaderJson);

            var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync();
                _logger.LogError("GetOrders failed {Status}: {Body}", (int)response.StatusCode, body[..Math.Min(body.Length, 500)]);
                return result;
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();

            JsonElement ordersArr;
            if (json.ValueKind == JsonValueKind.Array)
                ordersArr = json;
            else if (json.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                ordersArr = data;
            else if (json.TryGetProperty("orders", out var orders) && orders.ValueKind == JsonValueKind.Array)
                ordersArr = orders;
            else
                return result;

            foreach (var ord in ordersArr.EnumerateArray())
            {
                result.Add(ParseOrder(ord));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetOrders exception for accountId={AccountId}", accountId);
        }

        return result;
    }

    public async Task<bool> CancelOrderAsync(string authHeaderJson, long accountId, long orderId)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post,
                $"{BrokerBase}/paper/1/acc/{accountId}/orderop/cancel/{orderId}");
            ApplyAuthHeaders(request, authHeaderJson);

            var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync();
                _logger.LogError("CancelOrder failed {Status}: {Body}", (int)response.StatusCode, body[..Math.Min(body.Length, 500)]);
                return false;
            }

            _logger.LogInformation("Cancelled order {OrderId} on account {AccountId}", orderId, accountId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CancelOrder exception for orderId={OrderId}", orderId);
            return false;
        }
    }

    private static PaperOrder ParseOrder(JsonElement ord)
    {
        var order = new PaperOrder();
        if (ord.TryGetProperty("orderId", out var oid))
            order.OrderId = oid.GetInt64();

        // Extract ticker name from the top-level ticker object
        if (ord.TryGetProperty("ticker", out var ticker))
        {
            if (ticker.ValueKind == JsonValueKind.Object)
            {
                if (ticker.TryGetProperty("tickerId", out var ttid))
                    order.TickerId = ttid.GetInt64();
                if (ticker.TryGetProperty("symbol", out var sym))
                    order.TickerName = sym.GetString() ?? "";
                else if (ticker.TryGetProperty("tickerName", out var tn))
                    order.TickerName = tn.GetString() ?? "";
            }
            else if (ticker.ValueKind == JsonValueKind.String)
            {
                order.TickerName = ticker.GetString() ?? "";
            }
        }

        // Parse top-level fields (flat format from /order endpoint)
        if (ord.TryGetProperty("action", out var act))
            order.Action = act.GetString() ?? "";
        if (ord.TryGetProperty("orderType", out var ot))
            order.OrderType = ot.GetString() ?? "";
        if (ord.TryGetProperty("totalQuantity", out var tq))
            order.Quantity = (int)ParseDecimal(tq);
        if (ord.TryGetProperty("filledQuantity", out var fq))
            order.FilledQuantity = (int)ParseDecimal(fq);
        if (ord.TryGetProperty("lmtPrice", out var lp))
            order.LimitPrice = ParseDecimal(lp);
        if (ord.TryGetProperty("statusStr", out var ss))
            order.Status = ss.GetString() ?? "";
        if (ord.TryGetProperty("avgFilledPrice", out var afp))
            order.FilledPrice = ParseDecimal(afp);
        // Try filledTime0 (epoch ms) first — always reliable
        if (ord.TryGetProperty("filledTime0", out var ft0) && ft0.ValueKind == JsonValueKind.Number)
            order.FilledTime = DateTimeOffset.FromUnixTimeMilliseconds(ft0.GetInt64()).UtcDateTime;
        else if (ord.TryGetProperty("filledTime", out var ft2) && ft2.GetString() is { } ftStr2)
        {
            // Strip timezone abbreviation (EDT/EST) that DateTime.TryParse can't handle
            var cleaned = System.Text.RegularExpressions.Regex.Replace(ftStr2, @"\s+(EDT|EST|CDT|CST|PDT|PST)$", "");
            if (DateTime.TryParse(cleaned, out var parsedFt))
            {
                // Webull times are ET — convert to UTC
                var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                order.FilledTime = TimeZoneInfo.ConvertTimeToUtc(parsedFt, eastern);
            }
        }

        // Also support nested "orders" array format (from account endpoint)
        if (ord.TryGetProperty("orders", out var innerOrders) && innerOrders.ValueKind == JsonValueKind.Array)
        {
            foreach (var inner in innerOrders.EnumerateArray())
            {
                if (inner.TryGetProperty("orderId", out var ioid))
                    order.OrderId = ioid.GetInt64();
                if (inner.TryGetProperty("tickerId", out var itid))
                    order.TickerId = itid.GetInt64();
                if (string.IsNullOrEmpty(order.Action) && inner.TryGetProperty("action", out var ia))
                    order.Action = ia.GetString() ?? "";
                if (string.IsNullOrEmpty(order.OrderType) && inner.TryGetProperty("orderType", out var iot))
                    order.OrderType = iot.GetString() ?? "";
                if (order.Quantity == 0 && inner.TryGetProperty("totalQuantity", out var iq))
                    order.Quantity = (int)ParseDecimal(iq);
                if (!order.LimitPrice.HasValue && inner.TryGetProperty("lmtPrice", out var ilp))
                    order.LimitPrice = ParseDecimal(ilp);
                if (string.IsNullOrEmpty(order.Status) && inner.TryGetProperty("statusStr", out var ist))
                    order.Status = ist.GetString() ?? "";
                if (!order.FilledPrice.HasValue && inner.TryGetProperty("filledPrice", out var ifp))
                    order.FilledPrice = ParseDecimal(ifp);
                if (!order.FilledTime.HasValue && inner.TryGetProperty("filledTime", out var ift) && ift.GetString() is { } ftStr
                    && DateTime.TryParse(ftStr, out var ft))
                    order.FilledTime = ft.ToUniversalTime();
                if (string.IsNullOrEmpty(order.TickerName) && inner.TryGetProperty("symbol", out var isym))
                    order.TickerName = isym.GetString() ?? "";
                break; // just parse first inner order
            }
        }

        return order;
    }

    private static decimal ParseDecimal(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number) return el.GetDecimal();
        if (el.ValueKind == JsonValueKind.String && decimal.TryParse(el.GetString(), out var d)) return d;
        return 0;
    }

    private static void ApplyAuthHeaders(HttpRequestMessage request, string authHeaderJson)
    {
        using var doc = JsonDocument.Parse(authHeaderJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("did", out var did))
            request.Headers.TryAddWithoutValidation("did", did.GetString());
        if (root.TryGetProperty("access_token", out var token))
            request.Headers.TryAddWithoutValidation("access_token", token.GetString());
        if (root.TryGetProperty("hl", out var hl))
            request.Headers.TryAddWithoutValidation("hl", hl.GetString());
        if (root.TryGetProperty("locale", out var locale))
            request.Headers.TryAddWithoutValidation("locale", locale.GetString());
        if (root.TryGetProperty("tz", out var tz))
            request.Headers.TryAddWithoutValidation("tz", tz.GetString());

        request.Headers.TryAddWithoutValidation("app", "global");
        request.Headers.TryAddWithoutValidation("appid", "wb_web_app");
        request.Headers.TryAddWithoutValidation("device-type", "Web");
        request.Headers.TryAddWithoutValidation("platform", "web");

        if (root.TryGetProperty("ver", out var ver))
            request.Headers.TryAddWithoutValidation("ver", ver.GetString());
    }
}

public class PaperAccountInfo
{
    public long AccountId { get; set; }
    public decimal NetLiquidation { get; set; }
    public decimal UsableCash { get; set; }
    public decimal TotalProfitLoss { get; set; }
    public List<PaperPosition> Positions { get; set; } = [];
    public List<PaperOrder> OpenOrders { get; set; } = [];
}

public class PaperPosition
{
    public long TickerId { get; set; }
    public string Ticker { get; set; } = "";
    public int Quantity { get; set; }
    public decimal CostPrice { get; set; }
    public decimal LastPrice { get; set; }
    public decimal MarketValue { get; set; }
    public decimal UnrealizedPnl { get; set; }
}

public class PaperOrder
{
    public long OrderId { get; set; }
    public long TickerId { get; set; }
    public string TickerName { get; set; } = "";
    public string Action { get; set; } = "";
    public string OrderType { get; set; } = "";
    public int Quantity { get; set; }
    public int FilledQuantity { get; set; }
    public decimal? LimitPrice { get; set; }
    public string Status { get; set; } = "";
    public DateTime? FilledTime { get; set; }
    public decimal? FilledPrice { get; set; }
}

public class PaperOrderRequest
{
    public string Action { get; set; } = "BUY";
    public decimal LimitPrice { get; set; }
    public string OrderType { get; set; } = "MKT";
    public bool OutsideRegularTradingHour { get; set; } = true;
    public int Quantity { get; set; }
    public long TickerId { get; set; }
    public string TimeInForce { get; set; } = "DAY";
}

public class PaperOrderResult
{
    public bool Success { get; set; }
    public long? OrderId { get; set; }
    public string? ErrorMessage { get; set; }
}
