using Volo.Abp.Domain.Entities;

namespace TradingPilot.Symbols;

public class SymbolBar : Entity<Guid>
{
    public Guid SymbolId { get; set; }
    public BarTimeframe Timeframe { get; set; }
    public DateTime Timestamp { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public long Volume { get; set; }
    public decimal? Vwap { get; set; }
    public decimal? ChangeRatio { get; set; }
}
