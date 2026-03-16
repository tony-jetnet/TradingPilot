using Volo.Abp.Application;
using Volo.Abp.Modularity;

namespace TradingPilot;

[DependsOn(
    typeof(TradingPilotDomainSharedModule),
    typeof(AbpDddApplicationContractsModule)
)]
public class TradingPilotApplicationContractsModule : AbpModule
{
}
