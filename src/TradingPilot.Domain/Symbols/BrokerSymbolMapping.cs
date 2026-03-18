using Volo.Abp.Domain.Entities;

namespace TradingPilot.Symbols;

/// <summary>
/// Maps a Symbol to a broker-specific identifier.
/// Each broker (Webull, Questrade, etc.) has its own internal ID for the same ticker.
/// </summary>
public class BrokerSymbolMapping : Entity<Guid>
{
    public string SymbolId { get; set; } = null!; // FK → Symbol.Id (e.g. "AMD")
    public string BrokerName { get; set; } = null!; // e.g. "WebullPaper", "Questrade"
    public string BrokerSymbolId { get; set; } = null!; // e.g. "913254235" (Webull tickerId)
}
