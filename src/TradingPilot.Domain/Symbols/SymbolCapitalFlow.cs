using Volo.Abp.Domain.Entities;

namespace TradingPilot.Symbols;

public class SymbolCapitalFlow : Entity<Guid>
{
    public Guid SymbolId { get; set; }
    public DateOnly Date { get; set; }
    public decimal SuperLargeInflow { get; set; }
    public decimal SuperLargeOutflow { get; set; }
    public decimal LargeInflow { get; set; }
    public decimal LargeOutflow { get; set; }
    public decimal MediumInflow { get; set; }
    public decimal MediumOutflow { get; set; }
    public decimal SmallInflow { get; set; }
    public decimal SmallOutflow { get; set; }
    public DateTime CollectedAt { get; set; }
}
