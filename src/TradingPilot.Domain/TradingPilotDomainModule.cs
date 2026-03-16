using Volo.Abp.Caching.StackExchangeRedis;
using Volo.Abp.Domain;
using Volo.Abp.Modularity;

namespace TradingPilot;

[DependsOn(
    typeof(TradingPilotDomainSharedModule),
    typeof(AbpCachingStackExchangeRedisModule),
    typeof(AbpDddDomainModule)
)]
public class TradingPilotDomainModule : AbpModule
{
}
