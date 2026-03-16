using Volo.Abp.Modularity;

namespace TradingPilot;

[DependsOn(
    typeof(TradingPilotApplicationModule),
    typeof(TradingPilotDomainTestModule)
)]
public class TradingPilotApplicationTestModule : AbpModule
{

}
