using Volo.Abp.Domain.Entities;

namespace TradingPilot.Symbols;

public class SymbolBookSnapshot : Entity<Guid>
{
    public string SymbolId { get; set; } = null!;
    public DateTime Timestamp { get; set; }
    public decimal[] BidPrices { get; set; } = [];
    public decimal[] BidSizes { get; set; } = [];
    public decimal[] AskPrices { get; set; } = [];
    public decimal[] AskSizes { get; set; } = [];
    public decimal Spread { get; set; }
    public decimal MidPrice { get; set; }
    public decimal Imbalance { get; set; }
    public int Depth { get; set; }
}
