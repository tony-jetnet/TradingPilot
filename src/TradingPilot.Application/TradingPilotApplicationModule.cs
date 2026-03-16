using Volo.Abp.Application;
using Volo.Abp.Modularity;

namespace TradingPilot;

[DependsOn(
    typeof(TradingPilotDomainModule),
    typeof(TradingPilotApplicationContractsModule),
    typeof(AbpDddApplicationModule)
)]
public class TradingPilotApplicationModule : AbpModule
{
}
