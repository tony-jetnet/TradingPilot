using Volo.Abp.Modularity;

namespace TradingPilot;

public abstract class TradingPilotApplicationTestBase<TStartupModule> : TradingPilotTestBase<TStartupModule>
    where TStartupModule : IAbpModule
{

}
