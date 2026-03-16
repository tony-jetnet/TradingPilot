using Volo.Abp;
using Volo.Abp.Auditing;
using Volo.Abp.Domain.Entities;

namespace TradingPilot.Symbols;

public class Symbol : AggregateRoot<Guid>, ICreationAuditedObject, ISoftDelete
{
    public string Ticker { get; set; } = null!;
    public string Name { get; set; } = null!;
    public long WebullTickerId { get; set; }
    public int? WebullExchangeId { get; set; }
    public string? Exchange { get; set; }
    public SecurityType SecurityType { get; set; } = SecurityType.Stock;
    public string? Sector { get; set; }
    public string? Industry { get; set; }
    public SymbolStatus Status { get; set; } = SymbolStatus.Active;
    public DateOnly? ListDate { get; set; }
    public bool IsShortable { get; set; } = true;
    public bool IsMarginable { get; set; } = true;
    public bool IsWatched { get; set; }
    public DateTime CreationTime { get; set; }
    public Guid? CreatorId { get; set; }
    public bool IsDeleted { get; set; }
}
