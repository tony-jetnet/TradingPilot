using Volo.Abp.Caching;
using Volo.Abp.Domain;
using Volo.Abp.Modularity;

namespace TradingPilot;

[DependsOn(
    typeof(TradingPilotDomainSharedModule),
    typeof(AbpCachingModule),
    typeof(AbpDddDomainModule)
)]
public class TradingPilotDomainModule : AbpModule
{
}
