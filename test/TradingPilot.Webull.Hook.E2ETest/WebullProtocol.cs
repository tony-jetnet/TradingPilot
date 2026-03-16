using System.Text.Json;

namespace TradingPilot.Webull.Hook.E2ETest;

/// <summary>
/// Helper to build Webull subscription JSON from a captured auth header template.
/// </summary>
internal static class WebullProtocol
{
    /// <summary>All known subscription types to test.</summary>
    public static readonly int[] AllTypes = [91, 92, 100, 102, 104, 105];

    /// <summary>Known ticker IDs.</summary>
    public static class Tickers
    {
        public const long RKLB = 950178054;
        // AMD ticker ID to be discovered - use 0 as placeholder
        public const long AMD = 913254235;
    }

    /// <summary>
    /// Build a Webull subscription JSON command.
    /// </summary>
    public static string BuildSubscription(string headerJson, long tickerId, int type)
    {
        string flag = type is 91 or 105 ? "1,50,1" : "1";
        return $$"""{"flag":"{{flag}}","header":{{headerJson}},"module":"[\"OtherStocks\"]","tickerIds":[{{tickerId}}],"type":"{{type}}"}""";
    }

    /// <summary>
    /// Try to extract the auth header JSON from a captured subscription event payload.
    /// </summary>
    public static string? TryExtractHeader(string subscriptionJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(subscriptionJson);
            if (doc.RootElement.TryGetProperty("header", out var header))
                return header.ToString();
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Get a color for a subscription type for display purposes.
    /// </summary>
    public static ConsoleColor GetTypeColor(int type) => type switch
    {
        91 => ConsoleColor.Cyan,
        92 => ConsoleColor.Blue,
        100 => ConsoleColor.Green,
        102 => ConsoleColor.Yellow,
        104 => ConsoleColor.Magenta,
        105 => ConsoleColor.Red,
        _ => ConsoleColor.Gray
    };
}
