using Microsoft.EntityFrameworkCore;
using TradingPilot.Symbols;
using TradingPilot.Trading;
using Volo.Abp.Data;
using Volo.Abp.EntityFrameworkCore;

namespace TradingPilot.EntityFrameworkCore;

[ConnectionStringName("Default")]
public class TradingPilotDbContext : AbpDbContext<TradingPilotDbContext>
{
    public DbSet<Symbol> Symbols { get; set; }
    public DbSet<SymbolBar> SymbolBars { get; set; }
    public DbSet<SymbolBookSnapshot> SymbolBookSnapshots { get; set; }
    public DbSet<SymbolNews> SymbolNews { get; set; }
    public DbSet<SymbolCapitalFlow> SymbolCapitalFlows { get; set; }
    public DbSet<SymbolFinancialSnapshot> SymbolFinancialSnapshots { get; set; }
    public DbSet<TradingSignalRecord> TradingSignals { get; set; }
    public DbSet<PaperTrade> PaperTrades { get; set; }

    public TradingPilotDbContext(DbContextOptions<TradingPilotDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ConfigureTradingPilot();
    }
}
