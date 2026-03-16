using Microsoft.EntityFrameworkCore;
using Volo.Abp.Data;
using Volo.Abp.EntityFrameworkCore;

namespace TradingPilot.EntityFrameworkCore;

[ConnectionStringName("Default")]
public class TradingPilotDbContext : AbpDbContext<TradingPilotDbContext>
{
    /* Add DbSet properties for your Aggregate Roots / Entities here. */

    public TradingPilotDbContext(DbContextOptions<TradingPilotDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        /* Configure your own tables/entities inside here */
    }
}
