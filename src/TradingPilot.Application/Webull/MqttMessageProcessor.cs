using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingPilot.Symbols;
using TradingPilot.Trading;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Linq;
using Volo.Abp.Uow;
using System.IO;

namespace TradingPilot.Webull;

/// <summary>
/// Processes raw MQTT messages from the Webull hook into structured data.
/// Logs raw payloads for unknown formats, parses known formats into DB entities.
/// </summary>
public class MqttMessageProcessor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly L2BookCache _l2Cache;
    private readonly MarketMicrostructureAnalyzer _analyzer;
    private readonly SignalStore _signalStore;
    private readonly ILogger<MqttMessageProcessor> _logger;

    // Track how many raw payloads we've logged per topic to avoid spam
    private readonly ConcurrentDictionary<string, int> _rawLogCounts = new();
    private const int MaxRawLogsPerTopic = 5;

    // Cache symbol lookups: tickerId → Symbol
    private readonly ConcurrentDictionary<long, Symbol?> _symbolCache = new();

    // Track topic patterns we've seen
    private readonly ConcurrentDictionary<string, int> _topicCounts = new();

    // Binary capture: track saved samples per size bucket (max 20 unique)
    private readonly ConcurrentDictionary<string, int> _binaryCaptureCountPerBucket = new();
    private const int MaxCapturesPerBucket = 4; // 5 buckets × 4 = 20 total
    private const string CaptureDir = @"D:\Third-Parties\WebullHook\captures";
    private static readonly byte[] ProtobufFieldTags = [0x08, 0x10, 0x18, 0x20, 0x22, 0x2A, 0x0A, 0x12];
    private int _totalBinaryCaptureCount;

    public MqttMessageProcessor(
        IServiceScopeFactory scopeFactory,
        L2BookCache l2Cache,
        MarketMicrostructureAnalyzer analyzer,
        SignalStore signalStore,
        ILogger<MqttMessageProcessor> logger)
    {
        _scopeFactory = scopeFactory;
        _l2Cache = l2Cache;
        _analyzer = analyzer;
        _signalStore = signalStore;
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
                // Binary payload — try protobuf decode
                var decoded = WebullProtobufDecoder.TryDecode(payload);
                if (decoded != null)
                {
                    switch (decoded.Type)
                    {
                        case WebullMqttMessageType.L2Depth:
                            WebullProtobufDecoder.SplitDepthLevels(decoded);
                            await ProcessDecodedL2DepthAsync(decoded);
                            break;
                        case WebullMqttMessageType.QuoteUpdate:
                            ProcessDecodedQuote(decoded);
                            break;
                        case WebullMqttMessageType.QuoteTick:
                            ProcessDecodedTick(decoded);
                            break;
                    }
                }
                else
                {
                    // Unknown binary — log structure for discovery
                    LogBinaryStructure(topicPattern, payload);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to process MQTT message on topic {Topic}", topic);
        }
    }

    #region Decoded protobuf message handlers

    // Track log counts per decoded message type (log first N at INFO for verification)
    private readonly ConcurrentDictionary<WebullMqttMessageType, int> _decodedLogCounts = new();
    private const int MaxDecodedLogsPerType = 10;

    private async Task ProcessDecodedL2DepthAsync(WebullMqttMessage decoded)
    {
        int askCount = decoded.Asks?.Count ?? 0;
        int bidCount = decoded.Bids?.Count ?? 0;

        int logCount = _decodedLogCounts.AddOrUpdate(WebullMqttMessageType.L2Depth, 1, (_, c) => c + 1);
        if (logCount <= MaxDecodedLogsPerType)
        {
            _logger.LogInformation(
                "Decoded protobuf L2 depth: tickerId={TickerId} asks={Asks} bids={Bids}" +
                (askCount > 0 ? " bestAsk={BestAsk}" : "") +
                (bidCount > 0 ? " bestBid={BestBid}" : ""),
                decoded.TickerId, askCount, bidCount,
                askCount > 0 ? decoded.Asks![0].Price : 0m,
                bidCount > 0 ? decoded.Bids![0].Price : 0m);
        }

        if (askCount == 0 && bidCount == 0) return;

        var symbol = await GetSymbolByTickerIdAsync(decoded.TickerId);
        if (symbol == null)
        {
            _logger.LogDebug("No symbol found for tickerId={TickerId}, skipping decoded L2 storage", decoded.TickerId);
            return;
        }

        var bidPrices = decoded.Bids?.Select(l => l.Price).ToArray() ?? [];
        var bidSizes = decoded.Bids?.Select(l => l.Volume).ToArray() ?? [];
        var askPrices = decoded.Asks?.Select(l => l.Price).ToArray() ?? [];
        var askSizes = decoded.Asks?.Select(l => l.Volume).ToArray() ?? [];

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
            Spread = (bestBid > 0 && bestAsk > 0) ? bestAsk - bestBid : 0,
            MidPrice = (bestBid > 0 && bestAsk > 0) ? (bestBid + bestAsk) / 2 : (bestBid > 0 ? bestBid : bestAsk),
            Imbalance = totalBidSize + totalAskSize > 0
                ? (totalBidSize - totalAskSize) / (totalBidSize + totalAskSize) : 0,
            Depth = Math.Max(bidPrices.Length, askPrices.Length),
        };

        _l2Cache.AddSnapshot(decoded.TickerId, snapshot);

        // Analyze for trading signals
        var signal = _analyzer.AnalyzeSnapshot(decoded.TickerId, symbol.Ticker, snapshot);
        if (signal != null && signal.Type != SignalType.Hold)
        {
            _signalStore.AddSignal(signal);
            await PersistSignalAsync(signal, symbol.Id, snapshot);
        }

        // Store to DB
        using var scope = _scopeFactory.CreateScope();
        var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var snapshotRepo = scope.ServiceProvider.GetRequiredService<IRepository<SymbolBookSnapshot, Guid>>();

        using var uow = uowManager.Begin();
        await snapshotRepo.InsertAsync(snapshot, autoSave: false);
        await uow.CompleteAsync();
    }

    private void ProcessDecodedQuote(WebullMqttMessage decoded)
    {
        int logCount = _decodedLogCounts.AddOrUpdate(WebullMqttMessageType.QuoteUpdate, 1, (_, c) => c + 1);
        if (logCount <= MaxDecodedLogsPerType)
        {
            _logger.LogInformation(
                "Decoded protobuf quote: tickerId={TickerId} price={Price} open={Open} high={High} low={Low} vol={Volume} change={Change} ratio={Ratio} time={TradeTime}",
                decoded.TickerId, decoded.Price, decoded.Open, decoded.High, decoded.Low,
                decoded.Volume, decoded.ChangeAmount, decoded.ChangeRatio, decoded.TradeTime);
        }
    }

    private void ProcessDecodedTick(WebullMqttMessage decoded)
    {
        int logCount = _decodedLogCounts.AddOrUpdate(WebullMqttMessageType.QuoteTick, 1, (_, c) => c + 1);
        if (logCount <= MaxDecodedLogsPerType)
        {
            _logger.LogInformation(
                "Decoded protobuf tick: tickerId={TickerId} price={Price} ts={Timestamp}",
                decoded.TickerId, decoded.Price, decoded.Timestamp);
        }
    }

    #endregion

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
            Spread = (bestBid > 0 && bestAsk > 0) ? bestAsk - bestBid : 0,
            MidPrice = (bestBid > 0 && bestAsk > 0) ? (bestBid + bestAsk) / 2 : (bestBid > 0 ? bestBid : bestAsk),
            Imbalance = totalBidSize + totalAskSize > 0
                ? (totalBidSize - totalAskSize) / (totalBidSize + totalAskSize) : 0,
            Depth = Math.Max(bidPrices.Length, askPrices.Length),
        };

        _l2Cache.AddSnapshot(tickerId, snapshot);

        // Analyze for trading signals
        var signal = _analyzer.AnalyzeSnapshot(tickerId, symbol.Ticker, snapshot);
        if (signal != null && signal.Type != SignalType.Hold)
        {
            _signalStore.AddSignal(signal);
            await PersistSignalAsync(signal, symbol.Id, snapshot);
        }

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

    private async Task PersistSignalAsync(TradingSignal signal, Guid symbolId, SymbolBookSnapshot snapshot)
    {
        try
        {
            var record = new TradingSignalRecord
            {
                SymbolId = symbolId,
                TickerId = signal.TickerId,
                Timestamp = signal.Timestamp,
                Type = signal.Type,
                Strength = signal.Strength,
                Price = signal.Price,
                Score = signal.Indicators.GetValueOrDefault("CompositeScore"),
                Reason = signal.Reason,
                ObiSmoothed = signal.Indicators.GetValueOrDefault("OBI"),
                Wobi = signal.Indicators.GetValueOrDefault("WOBI"),
                PressureRoc = signal.Indicators.GetValueOrDefault("PressureROC"),
                SpreadSignal = signal.Indicators.GetValueOrDefault("SpreadSignal"),
                LargeOrderSignal = signal.Indicators.GetValueOrDefault("LargeOrderSignal"),
                Spread = snapshot.Spread,
                Imbalance = snapshot.Imbalance,
                BidLevels = snapshot.BidPrices.Length,
                AskLevels = snapshot.AskPrices.Length,
            };

            using var scope = _scopeFactory.CreateScope();
            var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            var repo = scope.ServiceProvider.GetRequiredService<IRepository<TradingSignalRecord, Guid>>();
            using var uow = uowManager.Begin();
            await repo.InsertAsync(record, autoSave: false);
            await uow.CompleteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist trading signal for tickerId={TickerId}", signal.TickerId);
        }
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
        if (payload.Length < 2) return;

        // 1. Save binary captures by size bucket for reverse-engineering
        SaveBinaryCapture(payload);

        // 2. Detect likely protobuf payloads
        if (Array.IndexOf(ProtobufFieldTags, payload[0]) >= 0)
        {
            var fields = AnalyzeProtobufFields(payload);
            _logger.LogInformation(
                "MQTT binary (likely protobuf): len={Len} firstByte=0x{First:X2} fields=[{Fields}]",
                payload.Length, payload[0], string.Join(", ", fields));
        }

        // 3. Extract readable ASCII substrings for pattern recognition
        var asciiStrings = ExtractAsciiStrings(payload, minLength: 3);
        if (asciiStrings.Count > 0)
        {
            _logger.LogInformation(
                "MQTT binary ASCII strings ({Len}B): [{Strings}]",
                payload.Length, string.Join(", ", asciiStrings.Select(s => $"\"{s}\"")));
        }

        // 4. Basic structure logging
        if (payload.Length >= 4)
        {
            _logger.LogInformation("MQTT binary structure: len={Len} first4={First4:X8} last4={Last4:X8}",
                payload.Length,
                BitConverter.ToInt32(payload, 0),
                BitConverter.ToInt32(payload, payload.Length - 4));
        }
    }

    private void SaveBinaryCapture(byte[] payload)
    {
        if (Interlocked.CompareExchange(ref _totalBinaryCaptureCount, 0, 0) >= 20)
            return;

        string bucket = payload.Length switch
        {
            <= 50 => "0-50",
            <= 200 => "50-200",
            <= 1000 => "200-1000",
            <= 5000 => "1000-5000",
            _ => "5000+"
        };

        int count = _binaryCaptureCountPerBucket.AddOrUpdate(bucket, 1, (_, c) => c + 1);
        if (count > MaxCapturesPerBucket) return;

        int total = Interlocked.Increment(ref _totalBinaryCaptureCount);
        if (total > 20) return;

        try
        {
            Directory.CreateDirectory(CaptureDir);
            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            string baseName = $"mqtt_{bucket}_{timestamp}_{payload.Length}B";

            // Save raw binary
            string binPath = Path.Combine(CaptureDir, baseName + ".bin");
            File.WriteAllBytes(binPath, payload);

            // Save hex dump companion
            string txtPath = Path.Combine(CaptureDir, baseName + ".txt");
            var sb = new StringBuilder();
            sb.AppendLine($"MQTT Binary Capture - {payload.Length} bytes - Bucket: {bucket}");
            sb.AppendLine($"Captured: {DateTime.UtcNow:O}");
            sb.AppendLine();

            // Hex dump with ASCII sidebar
            for (int offset = 0; offset < payload.Length; offset += 16)
            {
                sb.Append($"{offset:X8}  ");
                int lineLen = Math.Min(16, payload.Length - offset);

                // Hex
                for (int i = 0; i < 16; i++)
                {
                    if (i < lineLen)
                        sb.Append($"{payload[offset + i]:X2} ");
                    else
                        sb.Append("   ");
                    if (i == 7) sb.Append(' ');
                }

                sb.Append(" |");
                // ASCII
                for (int i = 0; i < lineLen; i++)
                {
                    byte b = payload[offset + i];
                    sb.Append(b >= 32 && b <= 126 ? (char)b : '.');
                }
                sb.AppendLine("|");
            }

            // Add extracted ASCII strings
            var strings = ExtractAsciiStrings(payload, minLength: 3);
            if (strings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Extracted ASCII strings:");
                foreach (var s in strings)
                    sb.AppendLine($"  \"{s}\"");
            }

            // Protobuf field analysis
            if (Array.IndexOf(ProtobufFieldTags, payload[0]) >= 0)
            {
                sb.AppendLine();
                sb.AppendLine("Likely protobuf - field analysis:");
                foreach (var field in AnalyzeProtobufFields(payload))
                    sb.AppendLine($"  {field}");
            }

            File.WriteAllText(txtPath, sb.ToString());

            _logger.LogInformation("Binary capture saved: {Path} ({Bytes}B, bucket={Bucket}, total={Total}/20)",
                binPath, payload.Length, bucket, total);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to save binary capture");
        }
    }

    private static List<string> AnalyzeProtobufFields(byte[] payload)
    {
        var fields = new List<string>();
        int pos = 0;
        int maxFields = 20;

        while (pos < payload.Length && fields.Count < maxFields)
        {
            // Read varint tag
            if (pos >= payload.Length) break;
            int tag = 0;
            int shift = 0;
            int startPos = pos;
            while (pos < payload.Length)
            {
                byte b = payload[pos++];
                tag |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
                if (shift > 35) break;
            }

            int fieldNumber = tag >> 3;
            int wireType = tag & 0x07;

            string wireTypeName = wireType switch
            {
                0 => "varint",
                1 => "fixed64",
                2 => "length-delimited",
                5 => "fixed32",
                _ => $"unknown({wireType})"
            };

            switch (wireType)
            {
                case 0: // varint
                    long value = 0;
                    shift = 0;
                    while (pos < payload.Length)
                    {
                        byte b = payload[pos++];
                        value |= (long)(b & 0x7F) << shift;
                        if ((b & 0x80) == 0) break;
                        shift += 7;
                        if (shift > 63) break;
                    }
                    fields.Add($"field {fieldNumber} ({wireTypeName})={value}");
                    break;
                case 1: // fixed64
                    if (pos + 8 <= payload.Length)
                    {
                        double dval = BitConverter.ToDouble(payload, pos);
                        long lval = BitConverter.ToInt64(payload, pos);
                        fields.Add($"field {fieldNumber} ({wireTypeName})=0x{lval:X16} double={dval:G}");
                        pos += 8;
                    }
                    else goto done;
                    break;
                case 2: // length-delimited
                    int len = 0;
                    shift = 0;
                    while (pos < payload.Length)
                    {
                        byte b = payload[pos++];
                        len |= (b & 0x7F) << shift;
                        if ((b & 0x80) == 0) break;
                        shift += 7;
                        if (shift > 35) break;
                    }
                    if (len < 0 || pos + len > payload.Length) goto done;
                    string preview = len <= 50 && payload.AsSpan(pos, len).ToArray().All(b => b >= 32 && b < 127)
                        ? $"\"{Encoding.ASCII.GetString(payload, pos, len)}\""
                        : $"[{len}B]";
                    fields.Add($"field {fieldNumber} ({wireTypeName} len={len})={preview}");
                    pos += len;
                    break;
                case 5: // fixed32
                    if (pos + 4 <= payload.Length)
                    {
                        float fval = BitConverter.ToSingle(payload, pos);
                        int ival = BitConverter.ToInt32(payload, pos);
                        fields.Add($"field {fieldNumber} ({wireTypeName})=0x{ival:X8} float={fval:G}");
                        pos += 4;
                    }
                    else goto done;
                    break;
                default:
                    fields.Add($"field {fieldNumber} (wire={wireType}) @offset {startPos} - stopping");
                    goto done;
            }
        }
        done:
        return fields;
    }

    private static List<string> ExtractAsciiStrings(byte[] payload, int minLength)
    {
        var results = new List<string>();
        var current = new StringBuilder();

        foreach (byte b in payload)
        {
            if (b >= 32 && b <= 126)
            {
                current.Append((char)b);
            }
            else
            {
                if (current.Length >= minLength)
                    results.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length >= minLength)
            results.Add(current.ToString());

        // Limit to first 20 strings to avoid noise
        return results.Count > 20 ? results.GetRange(0, 20) : results;
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
