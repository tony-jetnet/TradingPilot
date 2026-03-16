using Volo.Abp.Domain.Entities;

namespace TradingPilot.Symbols;

public class SymbolFinancialSnapshot : Entity<Guid>
{
    public Guid SymbolId { get; set; }
    public DateOnly Date { get; set; }
    public decimal? Pe { get; set; }
    public decimal? ForwardPe { get; set; }
    public decimal? Eps { get; set; }
    public decimal? EstEps { get; set; }
    public decimal? MarketCap { get; set; }
    public decimal? Volume { get; set; }
    public decimal? AvgVolume { get; set; }
    public decimal? High52w { get; set; }
    public decimal? Low52w { get; set; }
    public decimal? Beta { get; set; }
    public decimal? DividendYield { get; set; }
    public decimal? ShortFloat { get; set; }
    public string? NextEarningsDate { get; set; }
    public string? RawJson { get; set; }
    public DateTime CollectedAt { get; set; }
}
