namespace TradingPilot.Trading;

/// <summary>
/// Broker-agnostic interface for order execution and account queries.
/// All identifiers use symbol/ticker names (e.g. "AMD", "TSLA"), never broker-specific IDs.
/// Implementations: WebullBrokerClient (paper), QuestradeBrokerClient (live, future).
/// </summary>
public interface IBrokerClient
{
    /// <summary>Get account info: positions, P&L, buying power.</summary>
    Task<BrokerAccount?> GetAccountAsync();

    /// <summary>Place an order (entry or exit).</summary>
    Task<BrokerOrderResult> PlaceOrderAsync(BrokerOrderRequest order);

    /// <summary>Get recent filled/working orders.</summary>
    Task<List<BrokerOrder>> GetOrdersAsync(int pageSize = 200);

    /// <summary>Get a single order by ID (for fill verification).</summary>
    Task<BrokerOrder?> GetOrderAsync(string orderId);

    /// <summary>Cancel an order.</summary>
    Task<bool> CancelOrderAsync(string orderId);

    /// <summary>
    /// Resolve a symbol to the broker's internal numeric ID (e.g. Webull tickerId, Questrade symbolId).
    /// Used for correlating with real-time data caches (L2, ticks) that are keyed by broker-specific IDs.
    /// Returns 0 if unknown.
    /// </summary>
    long ResolveInternalId(string symbol);

    bool IsAuthenticated { get; }
    string BrokerName { get; }
}

public class BrokerAccount
{
    public decimal NetLiquidation { get; set; }
    public decimal UsableCash { get; set; }
    public decimal DayPnl { get; set; }
    public List<BrokerPosition> Positions { get; set; } = [];
}

public class BrokerPosition
{
    public string Symbol { get; set; } = "";
    public int Quantity { get; set; }
    public decimal AvgPrice { get; set; }
    public decimal MarketValue { get; set; }
    public decimal UnrealizedPnl { get; set; }
}

public class BrokerOrderRequest
{
    public string Symbol { get; set; } = "";
    public string Action { get; set; } = "BUY";
    public OrderType Type { get; set; } = OrderType.Limit;
    public decimal? LimitPrice { get; set; }
    public int Quantity { get; set; }
    public bool ExtendedHours { get; set; } = true;
    public string TimeInForce { get; set; } = "DAY";
}

public class BrokerOrder
{
    public string OrderId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Action { get; set; } = "";
    public string Status { get; set; } = "";
    public int Quantity { get; set; }
    public decimal? LimitPrice { get; set; }
    public decimal? FilledPrice { get; set; }
    public DateTime? FilledTime { get; set; }
}

public class BrokerOrderResult
{
    public bool Success { get; set; }
    public string? OrderId { get; set; }
    public string? Error { get; set; }
}

public enum OrderType
{
    Market,
    Limit,
    StopLimit,
    TrailingStop,
}
