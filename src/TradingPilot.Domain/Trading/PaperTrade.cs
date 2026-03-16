using Volo.Abp.Domain.Entities;

namespace TradingPilot.Trading;

public class PaperTrade : Entity<Guid>
{
    public Guid SymbolId { get; set; }
    public long TickerId { get; set; }
    public DateTime Timestamp { get; set; }
    public string Action { get; set; } = ""; // BUY or SELL
    public int Quantity { get; set; }
    public decimal SignalPrice { get; set; }  // Price when signal was generated
    public decimal? FilledPrice { get; set; } // Actual fill price
    public decimal Score { get; set; }        // Signal composite score
    public string Reason { get; set; } = "";  // Why we traded
    public long? WebullOrderId { get; set; }  // Webull's order ID
    public string? OrderStatus { get; set; }  // Status from Webull
    public Guid? SignalId { get; set; }       // Link to the TradingSignal that triggered this
}
