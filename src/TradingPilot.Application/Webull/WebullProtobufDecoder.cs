using System.Globalization;
using System.Text;

namespace TradingPilot.Webull;

/// <summary>
/// Decodes Webull's protobuf-encoded MQTT binary messages without requiring .proto files.
/// Handles three message types: QuoteTick (~42B), QuoteUpdate (~150-230B), and L2Depth (~2000-4000B).
/// </summary>
public static class WebullProtobufDecoder
{
    /// <summary>
    /// Attempts to decode a raw protobuf MQTT payload into a structured message.
    /// Returns null if the data is not a valid Webull protobuf message.
    /// </summary>
    public static WebullMqttMessage? TryDecode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            return null;

        try
        {
            var msg = new WebullMqttMessage();
            int offset = 0;

            while (offset < data.Length)
            {
                if (!TryReadTag(data, ref offset, out int fieldNumber, out int wireType))
                    break;

                switch (wireType)
                {
                    case 0: // varint
                        if (!TryReadVarint(data, ref offset, out _))
                            return null;
                        break;

                    case 2: // length-delimited
                        if (!TryReadLength(data, ref offset, out int length))
                            return null;
                        if (offset + length > data.Length)
                            return null;

                        var subData = data.Slice(offset, length);

                        switch (fieldNumber)
                        {
                            case 1: // Header sub-message
                                ParseHeader(subData, msg);
                                break;
                            case 2: // Quote data sub-message
                                ParseQuoteData(subData, msg);
                                break;
                            case 3: // Trade/candle data
                                ParseTradeData(subData, msg);
                                break;
                            case 4: // Another quote entry (same structure as field 2)
                                ParseQuoteData(subData, msg);
                                break;
                            case 6: // L2 depth data (repeated)
                                ParseDepthEntry(subData, msg);
                                break;
                            case 9: // Price tick data
                                ParseTickData(subData, msg);
                                break;
                        }

                        offset += length;
                        break;

                    case 1: // fixed64
                        offset += 8;
                        if (offset > data.Length) return null;
                        break;

                    case 5: // fixed32
                        offset += 4;
                        if (offset > data.Length) return null;
                        break;

                    default:
                        // Unknown wire type — bail out
                        return null;
                }
            }

            // Determine message type based on what we found
            msg.Type = DetermineMessageType(msg, data.Length);

            if (msg.Type == WebullMqttMessageType.Unknown)
                return null;

            return msg;
        }
        catch
        {
            return null;
        }
    }

    private static WebullMqttMessageType DetermineMessageType(WebullMqttMessage msg, int dataLength)
    {
        if ((msg.Asks != null && msg.Asks.Count > 0) || (msg.Bids != null && msg.Bids.Count > 0))
            return WebullMqttMessageType.L2Depth;

        if (msg.Open.HasValue || msg.High.HasValue || msg.Low.HasValue ||
            msg.Volume.HasValue || msg.TradeTime != null)
            return WebullMqttMessageType.QuoteUpdate;

        if (msg.Price.HasValue)
            return WebullMqttMessageType.QuoteTick;

        return WebullMqttMessageType.Unknown;
    }

    #region Sub-message parsers

    /// <summary>
    /// Parse header sub-message (outer field 1).
    /// Contains tickerId (field 3 varint), exchange code (field 4), timestamp (field 8), etc.
    /// </summary>
    private static void ParseHeader(ReadOnlySpan<byte> data, WebullMqttMessage msg)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            if (!TryReadTag(data, ref offset, out int fieldNumber, out int wireType))
                break;

            switch (wireType)
            {
                case 0: // varint
                    if (!TryReadVarint(data, ref offset, out long value))
                        return;
                    switch (fieldNumber)
                    {
                        case 3:
                            msg.TickerId = value;
                            break;
                        case 8:
                            msg.Timestamp = value;
                            break;
                    }
                    break;

                case 2: // length-delimited
                    if (!TryReadLength(data, ref offset, out int length))
                        return;
                    if (offset + length > data.Length)
                        return;
                    // Skip sub-message content (exchange code etc.)
                    offset += length;
                    break;

                case 1: // fixed64
                    offset += 8;
                    break;

                case 5: // fixed32
                    offset += 4;
                    break;

                default:
                    return;
            }
        }
    }

    /// <summary>
    /// Parse quote data sub-message (outer field 2 or 4).
    /// Contains: price (field 1), change amount (field 2), change ratio (field 3), volume (field 7).
    /// </summary>
    private static void ParseQuoteData(ReadOnlySpan<byte> data, WebullMqttMessage msg)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            if (!TryReadTag(data, ref offset, out int fieldNumber, out int wireType))
                break;

            switch (wireType)
            {
                case 0: // varint
                    if (!TryReadVarint(data, ref offset, out _))
                        return;
                    break;

                case 2: // length-delimited
                    if (!TryReadLength(data, ref offset, out int length))
                        return;
                    if (offset + length > data.Length)
                        return;

                    string strVal = GetUtf8String(data.Slice(offset, length));
                    switch (fieldNumber)
                    {
                        case 1: // last price
                            if (TryParseDecimal(strVal, out var price))
                                msg.Price ??= price;
                            break;
                        case 2: // change amount
                            if (TryParseDecimal(strVal, out var changeAmt))
                                msg.ChangeAmount ??= changeAmt;
                            break;
                        case 3: // change ratio
                            if (TryParseDecimal(strVal, out var changeRatio))
                                msg.ChangeRatio ??= changeRatio;
                            break;
                        case 7: // volume
                            if (long.TryParse(strVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var vol))
                                msg.Volume ??= vol;
                            break;
                    }

                    offset += length;
                    break;

                case 1:
                    offset += 8;
                    break;
                case 5:
                    offset += 4;
                    break;
                default:
                    return;
            }
        }
    }

    /// <summary>
    /// Parse trade/candle data sub-message (outer field 3).
    /// Contains: timestamp string (field 1), volume (field 3), change ratio (field 5),
    /// change amount (field 6), turnover (field 7), open (field 8), high (field 9), low (field 10).
    /// </summary>
    private static void ParseTradeData(ReadOnlySpan<byte> data, WebullMqttMessage msg)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            if (!TryReadTag(data, ref offset, out int fieldNumber, out int wireType))
                break;

            switch (wireType)
            {
                case 0:
                    if (!TryReadVarint(data, ref offset, out _))
                        return;
                    break;

                case 2:
                    if (!TryReadLength(data, ref offset, out int length))
                        return;
                    if (offset + length > data.Length)
                        return;

                    string strVal = GetUtf8String(data.Slice(offset, length));
                    switch (fieldNumber)
                    {
                        case 1: // timestamp string
                            msg.TradeTime = strVal;
                            break;
                        case 3: // volume
                            if (long.TryParse(strVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var vol))
                                msg.Volume ??= vol;
                            break;
                        case 5: // change ratio
                            if (TryParseDecimal(strVal, out var cr))
                                msg.ChangeRatio ??= cr;
                            break;
                        case 6: // change amount
                            if (TryParseDecimal(strVal, out var ca))
                                msg.ChangeAmount ??= ca;
                            break;
                        case 8: // open
                            if (TryParseDecimal(strVal, out var open))
                                msg.Open ??= open;
                            break;
                        case 9: // high
                            if (TryParseDecimal(strVal, out var high))
                                msg.High ??= high;
                            break;
                        case 10: // low
                            if (TryParseDecimal(strVal, out var low))
                                msg.Low ??= low;
                            break;
                    }

                    offset += length;
                    break;

                case 1:
                    offset += 8;
                    break;
                case 5:
                    offset += 4;
                    break;
                default:
                    return;
            }
        }
    }

    /// <summary>
    /// Parse the L2 depth block (outer field 6). Structure:
    /// - field 1 (repeated) = ask level sub-messages (ascending prices)
    /// - field 2 (repeated) = bid level sub-messages (descending prices)
    /// - field 3 = flag (varint)
    /// </summary>
    private static void ParseDepthEntry(ReadOnlySpan<byte> data, WebullMqttMessage msg)
    {
        int offset = 0;
        msg.Asks ??= [];
        msg.Bids ??= [];

        while (offset < data.Length)
        {
            if (!TryReadTag(data, ref offset, out int fieldNumber, out int wireType))
                break;

            if (wireType == 2 && (fieldNumber == 1 || fieldNumber == 2))
            {
                if (!TryReadLength(data, ref offset, out int length))
                    break;
                if (offset + length > data.Length)
                    break;

                var levelData = data.Slice(offset, length);
                var level = ParseSingleDepthLevel(levelData);
                if (level != null)
                {
                    if (fieldNumber == 1)
                        msg.Asks.Add(level);
                    else
                        msg.Bids.Add(level);
                }
                offset += length;
            }
            else
            {
                if (!SkipField(data, ref offset, wireType))
                    break;
            }
        }
    }

    /// <summary>Parse a single depth level: field 1 = price string, field 2 = volume string.</summary>
    private static WebullPriceLevel? ParseSingleDepthLevel(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        string? price = null;
        string? volume = null;

        while (offset < data.Length)
        {
            if (!TryReadTag(data, ref offset, out int fieldNumber, out int wireType))
                break;

            if (wireType == 2)
            {
                if (!TryReadLength(data, ref offset, out int length))
                    return null;
                if (offset + length > data.Length)
                    return null;

                if (fieldNumber == 1) price = GetUtf8String(data.Slice(offset, length));
                else if (fieldNumber == 2) volume = GetUtf8String(data.Slice(offset, length));

                offset += length;
            }
            else if (!SkipField(data, ref offset, wireType))
            {
                break;
            }
        }

        if (price != null && volume != null &&
            TryParseDecimal(price, out var priceDec) &&
            TryParseDecimal(volume, out var volumeDec))
        {
            return new WebullPriceLevel { Price = priceDec, Volume = volumeDec };
        }
        return null;
    }

    /// <summary>
    /// Parse tick data sub-message (outer field 9).
    /// Contains: price string (field 2).
    /// </summary>
    private static void ParseTickData(ReadOnlySpan<byte> data, WebullMqttMessage msg)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            if (!TryReadTag(data, ref offset, out int fieldNumber, out int wireType))
                break;

            switch (wireType)
            {
                case 0:
                    if (!TryReadVarint(data, ref offset, out _))
                        return;
                    break;

                case 2:
                    if (!TryReadLength(data, ref offset, out int length))
                        return;
                    if (offset + length > data.Length)
                        return;

                    if (fieldNumber == 2)
                    {
                        string strVal = GetUtf8String(data.Slice(offset, length));
                        if (TryParseDecimal(strVal, out var price))
                            msg.Price ??= price;
                    }

                    offset += length;
                    break;

                case 1:
                    offset += 8;
                    break;
                case 5:
                    offset += 4;
                    break;
                default:
                    return;
            }
        }
    }

    #endregion

    #region Depth splitting

    /// <summary>
    /// Splits the raw depth levels (all in one block) into asks and bids.
    /// Asks are ascending by price, bids are descending by price.
    /// The transition point is where prices switch from ascending to descending.
    /// </summary>
    public static void SplitDepthLevels(WebullMqttMessage msg)
    {
        if (msg.RawDepthLevels == null || msg.RawDepthLevels.Count == 0)
            return;

        var levels = msg.RawDepthLevels;

        // Find the transition point: where price stops increasing and starts decreasing.
        // Asks come first (ascending), then bids (descending).
        int splitIndex = levels.Count;
        for (int i = 1; i < levels.Count; i++)
        {
            // A significant drop indicates the transition from asks to bids.
            // We look for a price that is lower than the previous price by more than
            // a small tick (to avoid noise from equal prices).
            if (levels[i].Price < levels[i - 1].Price)
            {
                splitIndex = i;
                break;
            }
        }

        msg.Asks = levels.GetRange(0, splitIndex);
        msg.Bids = levels.GetRange(splitIndex, levels.Count - splitIndex);
    }

    #endregion

    #region Protobuf wire format helpers

    private static bool TryReadTag(ReadOnlySpan<byte> data, ref int offset, out int fieldNumber, out int wireType)
    {
        fieldNumber = 0;
        wireType = 0;

        if (!TryReadVarint(data, ref offset, out long tagValue))
            return false;

        fieldNumber = (int)(tagValue >> 3);
        wireType = (int)(tagValue & 0x07);
        return fieldNumber > 0;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int offset, out long value)
    {
        value = 0;
        int shift = 0;

        while (offset < data.Length)
        {
            byte b = data[offset++];
            value |= (long)(b & 0x7F) << shift;

            if ((b & 0x80) == 0)
                return true;

            shift += 7;
            if (shift > 63)
                return false; // Malformed varint
        }

        return false; // Ran out of data
    }

    private static bool TryReadLength(ReadOnlySpan<byte> data, ref int offset, out int length)
    {
        length = 0;
        if (!TryReadVarint(data, ref offset, out long lenValue))
            return false;

        if (lenValue < 0 || lenValue > int.MaxValue)
            return false;

        length = (int)lenValue;
        return true;
    }

    private static string GetUtf8String(ReadOnlySpan<byte> data)
    {
#if NET8_0_OR_GREATER
        return Encoding.UTF8.GetString(data);
#else
        return Encoding.UTF8.GetString(data.ToArray());
#endif
    }

    private static bool TryParseDecimal(string s, out decimal value)
    {
        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Skip a protobuf field value based on wire type.</summary>
    private static bool SkipField(ReadOnlySpan<byte> data, ref int offset, int wireType)
    {
        switch (wireType)
        {
            case 0: // varint
                return TryReadVarint(data, ref offset, out _);
            case 1: // 64-bit
                if (offset + 8 > data.Length) return false;
                offset += 8;
                return true;
            case 2: // length-delimited
                if (!TryReadLength(data, ref offset, out int len)) return false;
                if (offset + len > data.Length) return false;
                offset += len;
                return true;
            case 5: // 32-bit
                if (offset + 4 > data.Length) return false;
                offset += 4;
                return true;
            default:
                return false;
        }
    }

    #endregion
}

public class WebullMqttMessage
{
    public long TickerId { get; set; }
    public WebullMqttMessageType Type { get; set; }

    // Quote fields
    public decimal? Price { get; set; }
    public decimal? Open { get; set; }
    public decimal? High { get; set; }
    public decimal? Low { get; set; }
    public decimal? Close { get; set; }
    public long? Volume { get; set; }
    public decimal? ChangeAmount { get; set; }
    public decimal? ChangeRatio { get; set; }
    public string? TradeTime { get; set; }
    public long? Timestamp { get; set; }

    // L2 Depth fields (populated after SplitDepthLevels)
    public List<WebullPriceLevel>? Asks { get; set; }
    public List<WebullPriceLevel>? Bids { get; set; }

    /// <summary>
    /// Raw depth levels before splitting into asks/bids.
    /// Call <see cref="WebullProtobufDecoder.SplitDepthLevels"/> to populate Asks/Bids.
    /// </summary>
    public List<WebullPriceLevel>? RawDepthLevels { get; set; }
}

public class WebullPriceLevel
{
    public decimal Price { get; set; }
    public decimal Volume { get; set; }
}

public enum WebullMqttMessageType
{
    Unknown,
    QuoteTick,
    QuoteUpdate,
    L2Depth,
}
