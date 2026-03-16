using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TradingPilot.Webull;

public interface IWebullApiClient
{
    Task<WebullTickerInfo?> SearchTickerAsync(string authHeaderJson, string ticker, CancellationToken ct = default);
    Task<List<WebullBarData>> GetBarsAsync(string authHeaderJson, long tickerId, string type, int count, CancellationToken ct = default);
    Task<WebullDepthData?> GetDepthAsync(string authHeaderJson, long tickerId, CancellationToken ct = default);
    Task<List<WebullNewsItem>> GetTickerNewsAsync(string authHeaderJson, long tickerId, int count = 50, CancellationToken ct = default);
    Task<WebullCapitalFlowData?> GetCapitalFlowAsync(string authHeaderJson, long tickerId, CancellationToken ct = default);
    Task<WebullFinancialData?> GetFinancialIndexAsync(string authHeaderJson, long tickerId, CancellationToken ct = default);
}

public class WebullApiClient : IWebullApiClient
{
    private readonly HttpClient _http;
    private readonly ILogger<WebullApiClient> _logger;

    public WebullApiClient(HttpClient http, ILogger<WebullApiClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<WebullTickerInfo?> SearchTickerAsync(string authHeaderJson, string ticker, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/search/pc/tickers?keyword={Uri.EscapeDataString(ticker)}&pageIndex=1&pageSize=20");
        ApplyAuthHeaders(request, authHeaderJson);

        var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        _logger.LogDebug("Search response for {Ticker}: {Json}", ticker, json.ToString()[..Math.Min(json.ToString().Length, 200)]);

        if (!json.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in data.EnumerateArray())
        {
            string? symbol = item.GetProperty("symbol").GetString();
            if (string.Equals(symbol, ticker, StringComparison.OrdinalIgnoreCase))
            {
                return new WebullTickerInfo
                {
                    TickerId = item.GetProperty("tickerId").GetInt64(),
                    Symbol = symbol!,
                    Name = item.GetProperty("name").GetString() ?? "",
                    ExchangeCode = item.TryGetProperty("exchangeCode", out var ec) ? ec.GetString() : null,
                    ExchangeId = item.TryGetProperty("exchangeId", out var eid) ? eid.GetInt32() : null,
                    Type = item.TryGetProperty("type", out var t) ? t.ToString() : null,
                };
            }
        }

        return null;
    }

    public async Task<List<WebullBarData>> GetBarsAsync(string authHeaderJson, long tickerId, string type, int count, CancellationToken ct = default)
    {
        var allBars = new List<WebullBarData>();

        // The API returns 1 bar per call. Paginate backwards using timestamp cursor.
        // Start from now, work backwards until we have enough bars or no more data.
        long? timestamp = null;
        int emptyCount = 0;

        for (int page = 0; page < count && emptyCount < 3; page++)
        {
            string url = $"/api/quote/charts/query?tickerIds={tickerId}&type={type}&count={count}&extendTrading=0";
            if (timestamp.HasValue)
                url += $"&timestamp={timestamp.Value}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyAuthHeaders(request, authHeaderJson);

            var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("GetBars failed {Status}: {Body}", (int)response.StatusCode, body[..Math.Min(body.Length, 500)]);
                break;
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (json.ValueKind != JsonValueKind.Array) break;

            int barsThisPage = 0;
            long minTimestamp = long.MaxValue;

            foreach (var item in json.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("data", out var dataArr) || dataArr.ValueKind != JsonValueKind.Array) continue;

                foreach (var barStr in dataArr.EnumerateArray())
                {
                    string raw = barStr.GetString() ?? "";
                    if (page == 0 && barsThisPage == 0)
                        _logger.LogInformation("First raw bar string ({Type}): {Raw}", type, raw);

                    var bar = ParseBarString(raw);
                    if (bar != null)
                    {
                        allBars.Add(bar);
                        barsThisPage++;
                        long ts = new DateTimeOffset(bar.Timestamp, TimeSpan.Zero).ToUnixTimeSeconds();
                        if (ts < minTimestamp) minTimestamp = ts;
                    }
                }

                // Check hasMore
                bool hasMore = item.TryGetProperty("hasMore", out var hm) && hm.GetInt32() == 1;
                if (!hasMore)
                {
                    _logger.LogInformation("GetBars: no more data for {Type} (page {Page}, total {Total} bars)", type, page, allBars.Count);
                    return allBars;
                }
            }

            if (barsThisPage == 0)
            {
                emptyCount++;
                continue;
            }

            // Move cursor back: use 1 second before the earliest bar we got
            timestamp = minTimestamp - 1;

            // Rate limit between pages
            if (page < count - 1)
                await Task.Delay(200, ct);
        }

        return allBars;
    }

    public async Task<WebullDepthData?> GetDepthAsync(string authHeaderJson, long tickerId, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/stock/tickerRealTime/getDepth?tickerId={tickerId}");
        ApplyAuthHeaders(request, authHeaderJson);

        var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("GetDepth failed {Status}: {Body}", (int)response.StatusCode, body[..Math.Min(body.Length, 500)]);
            return null;
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        _logger.LogDebug("GetDepth raw for tickerId={TickerId}: {Json}", tickerId, json.ToString()[..Math.Min(json.ToString().Length, 500)]);

        var result = new WebullDepthData();
        if (json.TryGetProperty("ntvAggAskList", out var asks) && asks.ValueKind == JsonValueKind.Array)
            result.Asks = ParseDepthLevels(asks);
        if (json.TryGetProperty("ntvAggBidList", out var bids) && bids.ValueKind == JsonValueKind.Array)
            result.Bids = ParseDepthLevels(bids);

        return result;
    }

    private static List<WebullDepthLevel> ParseDepthLevels(JsonElement arr)
    {
        var levels = new List<WebullDepthLevel>();
        foreach (var item in arr.EnumerateArray())
        {
            var level = new WebullDepthLevel();
            if (item.TryGetProperty("price", out var p))
                level.Price = decimal.TryParse(p.GetString() ?? p.ToString(), out var pv) ? pv : 0;
            if (item.TryGetProperty("volume", out var v))
                level.Volume = decimal.TryParse(v.GetString() ?? v.ToString(), out var vv) ? vv : 0;
            levels.Add(level);
        }
        return levels;
    }

    public async Task<List<WebullNewsItem>> GetTickerNewsAsync(string authHeaderJson, long tickerId, int count = 50, CancellationToken ct = default)
    {
        // News API is on a different domain than quotes
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/information/news/tickerNewses/v9?tickerId={tickerId}&currentNewsId=0&pageSize={count}");
        ApplyAuthHeaders(request, authHeaderJson);

        var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("GetTickerNews failed {Status}: {Body}", (int)response.StatusCode, body[..Math.Min(body.Length, 500)]);
            return [];
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        _logger.LogDebug("GetTickerNews raw for tickerId={TickerId}: {Json}", tickerId, json.ToString()[..Math.Min(json.ToString().Length, 500)]);

        var items = new List<WebullNewsItem>();

        // Response may be an array directly or { "data": [...] }
        JsonElement arr;
        if (json.ValueKind == JsonValueKind.Array)
            arr = json;
        else if (json.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            arr = data;
        else
            return items;

        foreach (var item in arr.EnumerateArray())
        {
            var news = new WebullNewsItem
            {
                NewsId = item.TryGetProperty("id", out var id) ? id.GetInt64() : 0,
                Title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                Summary = item.TryGetProperty("summary", out var s) ? s.GetString() : null,
                SourceName = item.TryGetProperty("sourceName", out var sn) ? sn.GetString() : null,
                Url = item.TryGetProperty("newsUrl", out var u) ? u.GetString() : null,
            };
            if (item.TryGetProperty("newsTime", out var nt) && nt.GetString() is { } nts
                && DateTime.TryParse(nts, out var parsed))
                news.PublishedAt = parsed.ToUniversalTime();

            if (news.NewsId != 0)
                items.Add(news);
        }

        return items;
    }

    public async Task<WebullCapitalFlowData?> GetCapitalFlowAsync(string authHeaderJson, long tickerId, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/stock/capitalflow/ticker?tickerId={tickerId}&showDm=false");
        ApplyAuthHeaders(request, authHeaderJson);

        var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("GetCapitalFlow failed {Status}", (int)response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        _logger.LogDebug("GetCapitalFlow raw: {Json}", json.ToString()[..Math.Min(json.ToString().Length, 300)]);

        if (!json.TryGetProperty("latest", out var latest)) return null;
        if (!latest.TryGetProperty("item", out var item)) return null;

        string? date = latest.TryGetProperty("date", out var d) ? d.GetString() : null;

        return new WebullCapitalFlowData
        {
            Date = date,
            SuperLargeInflow = GetDecimal(item, "superLargeInflow"),
            SuperLargeOutflow = GetDecimal(item, "superLargeOutflow"),
            LargeInflow = GetDecimal(item, "largeInflow"),
            LargeOutflow = GetDecimal(item, "largeOutflow"),
            MediumInflow = GetDecimal(item, "mediumInflow"),
            MediumOutflow = GetDecimal(item, "mediumOutflow"),
            SmallInflow = GetDecimal(item, "smallInflow"),
            SmallOutflow = GetDecimal(item, "smallOutflow"),
        };
    }

    public async Task<WebullFinancialData?> GetFinancialIndexAsync(string authHeaderJson, long tickerId, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/information/financial/index?tickerId={tickerId}");
        ApplyAuthHeaders(request, authHeaderJson);

        var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("GetFinancialIndex failed {Status}", (int)response.StatusCode);
            return null;
        }

        string rawJson = await response.Content.ReadAsStringAsync(ct);
        _logger.LogDebug("GetFinancialIndex raw: {Json}", rawJson[..Math.Min(rawJson.Length, 300)]);

        var json = JsonDocument.Parse(rawJson).RootElement;
        var result = new WebullFinancialData { RawJson = rawJson };

        // Parse known fields from the financial index
        if (json.TryGetProperty("latestEarningsDate", out var earnDate))
            result.NextEarningsDate = earnDate.GetString();
        if (json.TryGetProperty("projEps", out var projEps))
            result.EstEps = ParseDecimalField(projEps);
        if (json.TryGetProperty("pe", out var pe))
            result.Pe = ParseDecimalField(pe);
        if (json.TryGetProperty("forwardPe", out var fpe))
            result.ForwardPe = ParseDecimalField(fpe);
        if (json.TryGetProperty("eps", out var eps))
            result.Eps = ParseDecimalField(eps);
        if (json.TryGetProperty("marketCap", out var mc))
            result.MarketCap = ParseDecimalField(mc);
        if (json.TryGetProperty("totalShares", out var ts))
            result.Volume = ParseDecimalField(ts);
        if (json.TryGetProperty("avgVol10D", out var av10))
            result.AvgVolume = ParseDecimalField(av10);
        if (json.TryGetProperty("fiftyTwoWkHigh", out var h52))
            result.High52w = ParseDecimalField(h52);
        if (json.TryGetProperty("fiftyTwoWkLow", out var l52))
            result.Low52w = ParseDecimalField(l52);
        if (json.TryGetProperty("beta", out var beta))
            result.Beta = ParseDecimalField(beta);
        if (json.TryGetProperty("yield", out var yld))
            result.DividendYield = ParseDecimalField(yld);
        if (json.TryGetProperty("shortFloat", out var sf))
            result.ShortFloat = ParseDecimalField(sf);

        // Also try nested "remind" object
        if (json.TryGetProperty("remind", out var remind))
        {
            if (result.NextEarningsDate == null && remind.TryGetProperty("openingTime", out var ot))
                result.NextEarningsDate = ot.GetString();
            if (result.EstEps == null && remind.TryGetProperty("projEps", out var rpe))
                result.EstEps = ParseDecimalField(rpe);
        }

        return result;
    }

    private static decimal GetDecimal(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number) return v.GetDecimal();
            if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), out var d)) return d;
        }
        return 0;
    }

    private static decimal? ParseDecimalField(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number) return el.GetDecimal();
        if (el.ValueKind == JsonValueKind.String && decimal.TryParse(el.GetString(), out var d)) return d;
        return null;
    }

    private static WebullBarData? ParseBarString(string raw)
    {
        // Webull bar format: "timestamp,open,close,high,low,volume,vwap,changeRatio"
        // Format: timestamp(s),open,close,high,low,preClose,volume,vwap
        var parts = raw.Split(',');
        if (parts.Length < 7) return null;

        return new WebullBarData
        {
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(long.Parse(parts[0])).UtcDateTime,
            Open = decimal.Parse(parts[1]),
            Close = decimal.Parse(parts[2]),
            High = decimal.Parse(parts[3]),
            Low = decimal.Parse(parts[4]),
            Volume = long.Parse(parts[6]),
            Vwap = parts.Length > 7 && decimal.TryParse(parts[7], out var vwap) ? vwap : null,
        };
    }

    private static void ApplyAuthHeaders(HttpRequestMessage request, string authHeaderJson)
    {
        using var doc = JsonDocument.Parse(authHeaderJson);
        var root = doc.RootElement;

        // Extract auth credentials from the captured MQTT header
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

        // Use web-style identifiers (desktop headers cause API to return minimal data)
        request.Headers.TryAddWithoutValidation("app", "global");
        request.Headers.TryAddWithoutValidation("appid", "wb_web_app");
        request.Headers.TryAddWithoutValidation("device-type", "Web");
        request.Headers.TryAddWithoutValidation("platform", "web");
        // ver header is required for news API to return data
        if (root.TryGetProperty("ver", out var ver))
            request.Headers.TryAddWithoutValidation("ver", ver.GetString());
    }
}

public class WebullTickerInfo
{
    public long TickerId { get; set; }
    public string Symbol { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ExchangeCode { get; set; }
    public int? ExchangeId { get; set; }
    public string? Type { get; set; }
}

public class WebullBarData
{
    public DateTime Timestamp { get; set; }
    public decimal Open { get; set; }
    public decimal Close { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public long Volume { get; set; }
    public decimal? Vwap { get; set; }
    public decimal? ChangeRatio { get; set; }
}

public class WebullDepthData
{
    public List<WebullDepthLevel> Bids { get; set; } = [];
    public List<WebullDepthLevel> Asks { get; set; } = [];
}

public class WebullDepthLevel
{
    public decimal Price { get; set; }
    public decimal Volume { get; set; }
}

public class WebullNewsItem
{
    public long NewsId { get; set; }
    public string Title { get; set; } = "";
    public string? Summary { get; set; }
    public string? SourceName { get; set; }
    public string? Url { get; set; }
    public DateTime PublishedAt { get; set; }
}

public class WebullCapitalFlowData
{
    public string? Date { get; set; }
    public decimal SuperLargeInflow { get; set; }
    public decimal SuperLargeOutflow { get; set; }
    public decimal LargeInflow { get; set; }
    public decimal LargeOutflow { get; set; }
    public decimal MediumInflow { get; set; }
    public decimal MediumOutflow { get; set; }
    public decimal SmallInflow { get; set; }
    public decimal SmallOutflow { get; set; }
}

public class WebullFinancialData
{
    public decimal? Pe { get; set; }
    public decimal? ForwardPe { get; set; }
    public decimal? Eps { get; set; }
    public decimal? EstEps { get; set; }
    public decimal? MarketCap { get; set; }
    public decimal? Volume { get; set; }
    public decimal? AvgVolume { get; set; }
    public decimal? High52w { get; set; }
    public decimal? Low52w { get; set; }
    public decimal? Beta { get; set; }
    public decimal? DividendYield { get; set; }
    public decimal? ShortFloat { get; set; }
    public string? NextEarningsDate { get; set; }
    public string? RawJson { get; set; }
}
