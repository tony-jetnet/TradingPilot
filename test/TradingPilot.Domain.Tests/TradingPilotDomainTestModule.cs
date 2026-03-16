using Volo.Abp.Modularity;

namespace TradingPilot;

[DependsOn(
    typeof(TradingPilotDomainModule),
    typeof(TradingPilotTestBaseModule)
)]
public class TradingPilotDomainTestModule : AbpModule
{

}
