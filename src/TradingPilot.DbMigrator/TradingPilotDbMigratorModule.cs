using TradingPilot.EntityFrameworkCore;
using Volo.Abp.Autofac;
using Volo.Abp.Modularity;

namespace TradingPilot.DbMigrator;

[DependsOn(
    typeof(AbpAutofacModule),
    typeof(TradingPilotEntityFrameworkCoreModule),
    typeof(TradingPilotApplicationContractsModule)
)]
public class TradingPilotDbMigratorModule : AbpModule
{
}
