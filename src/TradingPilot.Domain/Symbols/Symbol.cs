using Volo.Abp.Auditing;
using Volo.Abp.Domain.Entities;

namespace TradingPilot.Symbols;

/// <summary>
/// Financial instrument. Id is the ticker symbol (e.g., "AMD", "RKLB").
/// </summary>
public class Symbol : BasicAggregateRoot<string>, ICreationAuditedObject
{
    public Symbol() { }

    public Symbol(string id)
    {
        Id = id;
    }

    public string Name { get; set; } = null!;
    public long WebullTickerId { get; set; }
    public int? WebullExchangeId { get; set; }
    public string? Exchange { get; set; }
    public SecurityType SecurityType { get; set; } = SecurityType.Stock;
    public string? Sector { get; set; }
    public string? Industry { get; set; }
    public SymbolStatus Status { get; set; } = SymbolStatus.Active;
    public bool IsShortable { get; set; } = true;
    public bool IsMarginable { get; set; } = true;
    public bool IsWatched { get; set; }
    /// <summary>Set daily by PreMarketScannerJob. Top 10 from 50 watched symbols trade that day.</summary>
    public bool IsActiveForTrading { get; set; }
    public DateTime CreationTime { get; set; }
    public Guid? CreatorId { get; set; }
}
