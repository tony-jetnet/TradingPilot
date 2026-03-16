using Volo.Abp.Domain.Entities;

namespace TradingPilot.Symbols;

public class SymbolNews : Entity<Guid>
{
    public Guid SymbolId { get; set; }
    public long WebullNewsId { get; set; }
    public string Title { get; set; } = null!;
    public string? Summary { get; set; }
    public string? SourceName { get; set; }
    public string? Url { get; set; }
    public DateTime PublishedAt { get; set; }
    public DateTime CollectedAt { get; set; }
}
