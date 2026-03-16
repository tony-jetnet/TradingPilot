using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingPilot.Symbols;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Linq;
using Volo.Abp.Uow;

namespace TradingPilot.Webull;

/// <summary>
/// Processes raw MQTT messages from the Webull hook into structured data.
/// Logs raw payloads for unknown formats, parses known formats into DB entities.
/// </summary>
public class MqttMessageProcessor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly L2BookCache _l2Cache;
    private readonly ILogger<MqttMessageProcessor> _logger;

    // Track how many raw payloads we've logged per topic to avoid spam
    private readonly ConcurrentDictionary<string, int> _rawLogCounts = new();
    private const int MaxRawLogsPerTopic = 5;

    // Cache symbol lookups: tickerId → Symbol
    private readonly ConcurrentDictionary<long, Symbol?> _symbolCache = new();

    // Track topic patterns we've seen
    private readonly ConcurrentDictionary<string, int> _topicCounts = new();

    public MqttMessageProcessor(
        IServiceScopeFactory scopeFactory,
        L2BookCache l2Cache,
        ILogger<MqttMessageProcessor> logger)
    {
        _scopeFactory = scopeFactory;
        _l2Cache = l2Cache;
        _logger = logger;
    }

    public async Task ProcessMessageAsync(string topic, byte[] payload)
    {
        // Count messages per topic pattern
        string topicPattern = NormalizeTopicPattern(topic);
        int count = _topicCounts.AddOrUpdate(topicPattern, 1, (_, c) => c + 1);
        if (count == 1 || count == 10 || count == 100 || count % 1000 == 0)
            _logger.LogInformation("MQTT topic pattern '{Pattern}': {Count} messages so far", topicPattern, count);

        // Log raw payload for first N messages per topic pattern
        LogRawPayload(topicPattern, topic, payload);

        // Try to parse the payload
        try
        {
            // Attempt JSON parse first
            if (payload.Length > 0 && (payload[0] == '{' || payload[0] == '['))
            {
                await ProcessJsonMessageAsync(topic, payload);
            }
            else
            {
                // Binary payload — log structure for discovery
                LogBinaryStructure(topicPattern, payload);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to process MQTT message on topic {Topic}", topic);
        }
    }

    private async Task ProcessJsonMessageAsync(string topic, byte[] payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        // Check for L2/depth data patterns
        if (root.TryGetProperty("ntvAggAskList", out _) || root.TryGetProperty("ntvAggBidList", out _))
        {
            await ProcessL2DepthAsync(topic, root);
            return;
        }

        // Check for trade/Time&Sales patterns
        if (root.TryGetProperty("tradeTime", out _) && root.TryGetProperty("deal", out _))
        {
            ProcessTimeSales(topic, root);
            return;
        }

        // Check for quote update patterns
        if (root.TryGetProperty("tickerId", out _) && root.TryGetProperty("close", out _))
        {
            ProcessQuoteUpdate(topic, root);
            return;
        }

        // Check for array of data items
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (item.TryGetProperty("ntvAggAskList", out _) || item.TryGetProperty("ntvAggBidList", out _))
                {
                    await ProcessL2DepthAsync(topic, item);
                }
            }
        }
    }

    private async Task ProcessL2DepthAsync(string topic, JsonElement root)
    {
        long tickerId = 0;
        if (root.TryGetProperty("tickerId", out var tidProp))
            tickerId = tidProp.GetInt64();

        var bids = ParseDepthLevels(root, "ntvAggBidList");
        var asks = ParseDepthLevels(root, "ntvAggAskList");

        if (bids.Count == 0 && asks.Count == 0) return;

        _logger.LogInformation("MQTT L2 depth for tickerId={TickerId}: {Bids} bids, {Asks} asks",
            tickerId, bids.Count, asks.Count);

        var symbol = await GetSymbolByTickerIdAsync(tickerId);
        if (symbol == null)
        {
            _logger.LogDebug("No symbol found for tickerId={TickerId}, skipping L2 storage", tickerId);
            return;
        }

        var bidPrices = bids.Select(l => l.Price).ToArray();
        var bidSizes = bids.Select(l => l.Volume).ToArray();
        var askPrices = asks.Select(l => l.Price).ToArray();
        var askSizes = asks.Select(l => l.Volume).ToArray();

        decimal bestBid = bidPrices.Length > 0 ? bidPrices[0] : 0;
        decimal bestAsk = askPrices.Length > 0 ? askPrices[0] : 0;
        decimal totalBidSize = bidSizes.Sum();
        decimal totalAskSize = askSizes.Sum();

        var snapshot = new SymbolBookSnapshot
        {
            SymbolId = symbol.Id,
            Timestamp = DateTime.UtcNow,
            BidPrices = bidPrices,
            BidSizes = bidSizes,
            AskPrices = askPrices,
            AskSizes = askSizes,
            Spread = bestAsk - bestBid,
            MidPrice = (bestBid + bestAsk) / 2,
            Imbalance = totalBidSize + totalAskSize > 0
                ? (totalBidSize - totalAskSize) / (totalBidSize + totalAskSize) : 0,
            Depth = Math.Max(bidPrices.Length, askPrices.Length),
        };

        _l2Cache.AddSnapshot(tickerId, snapshot);

        // Store to DB
        using var scope = _scopeFactory.CreateScope();
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var snapshotRepo = scope.ServiceProvider.GetRequiredService<IRepository<SymbolBookSnapshot, Guid>>();

        using var uow = uowManager.Begin();
        await snapshotRepo.InsertAsync(snapshot, autoSave: false);
        await uow.CompleteAsync();
    }

    private void ProcessTimeSales(string topic, JsonElement root)
    {
        // Log discovered Time&Sales data for future parsing
        long tickerId = root.TryGetProperty("tickerId", out var tid) ? tid.GetInt64() : 0;
        string tradeTime = root.TryGetProperty("tradeTime", out var tt) ? tt.GetString() ?? "" : "";
        string deal = root.TryGetProperty("deal", out var d) ? d.ToString() : "";

        _logger.LogInformation("MQTT Time&Sales: tickerId={TickerId} time={TradeTime} deal={Deal}",
            tickerId, tradeTime, deal);
    }

    private void ProcessQuoteUpdate(string topic, JsonElement root)
    {
        long tickerId = root.TryGetProperty("tickerId", out var tid) ? tid.GetInt64() : 0;
        decimal close = root.TryGetProperty("close", out var c) && decimal.TryParse(c.GetString() ?? c.ToString(), out var cv) ? cv : 0;
        decimal volume = root.TryGetProperty("volume", out var v) && decimal.TryParse(v.GetString() ?? v.ToString(), out var vv) ? vv : 0;

        _logger.LogInformation("MQTT Quote: tickerId={TickerId} price={Price} volume={Volume}",
            tickerId, close, volume);
    }

    private static List<(decimal Price, decimal Volume)> ParseDepthLevels(JsonElement root, string propertyName)
    {
        var levels = new List<(decimal, decimal)>();
        if (!root.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return levels;

        foreach (var item in arr.EnumerateArray())
        {
            decimal price = 0, volume = 0;
            if (item.TryGetProperty("price", out var p))
                decimal.TryParse(p.GetString() ?? p.ToString(), out price);
            if (item.TryGetProperty("volume", out var v))
                decimal.TryParse(v.GetString() ?? v.ToString(), out volume);
            levels.Add((price, volume));
        }
        return levels;
    }

    private async Task<Symbol?> GetSymbolByTickerIdAsync(long tickerId)
    {
        if (tickerId == 0) return null;

        if (_symbolCache.TryGetValue(tickerId, out var cached))
            return cached;

        using var scope = _scopeFactory.CreateScope();
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var symbolRepo = scope.ServiceProvider.GetRequiredService<IRepository<Symbol, Guid>>();
        var asyncExecuter = scope.ServiceProvider.GetRequiredService<IAsyncQueryableExecuter>();

        using var uow = uowManager.Begin();
        var symbol = await asyncExecuter.FirstOrDefaultAsync(
            (await symbolRepo.GetQueryableAsync()).Where(s => s.WebullTickerId == tickerId));
        await uow.CompleteAsync();

        _symbolCache[tickerId] = symbol;
        return symbol;
    }

    private void LogRawPayload(string topicPattern, string topic, byte[] payload)
    {
        int logCount = _rawLogCounts.AddOrUpdate(topicPattern, 1, (_, c) => c + 1);
        if (logCount > MaxRawLogsPerTopic) return;

        string preview;
        if (payload.Length > 0 && (payload[0] == '{' || payload[0] == '['))
        {
            preview = Encoding.UTF8.GetString(payload, 0, Math.Min(payload.Length, 1000));
        }
        else
        {
            preview = $"[binary {payload.Length}B] hex={Convert.ToHexString(payload, 0, Math.Min(payload.Length, 100))}";
        }

        _logger.LogInformation("MQTT RAW #{Count} topic='{Topic}' ({Bytes}B): {Preview}",
            logCount, topic, payload.Length, preview);
    }

    private void LogBinaryStructure(string topicPattern, byte[] payload)
    {
        int logCount = _rawLogCounts.GetValueOrDefault(topicPattern, 0);
        if (logCount > 2) return; // Only log structure for first 2

        if (payload.Length >= 4)
        {
            _logger.LogInformation("MQTT binary structure: len={Len} first4={First4:X8} last4={Last4:X8}",
                payload.Length,
                BitConverter.ToInt32(payload, 0),
                payload.Length >= 4 ? BitConverter.ToInt32(payload, payload.Length - 4) : 0);
        }
    }

    private static string NormalizeTopicPattern(string topic)
    {
        // Normalize topics by replacing numeric segments with {id} for grouping
        // e.g., "/ticker/913254235/quote" → "/ticker/{id}/quote"
        var parts = topic.Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            if (long.TryParse(parts[i], out _))
                parts[i] = "{id}";
        }
        return string.Join('/', parts);
    }
}
