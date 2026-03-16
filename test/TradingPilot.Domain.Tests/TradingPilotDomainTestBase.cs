using Volo.Abp.Modularity;

namespace TradingPilot;

/* Inherit from this class for your domain layer tests. */
public abstract class TradingPilotDomainTestBase<TStartupModule> : TradingPilotTestBase<TStartupModule>
    where TStartupModule : IAbpModule
{

}
