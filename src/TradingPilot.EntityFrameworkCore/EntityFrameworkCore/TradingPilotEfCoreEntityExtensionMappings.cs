using Volo.Abp.Threading;

namespace TradingPilot.EntityFrameworkCore;

public static class TradingPilotEfCoreEntityExtensionMappings
{
    private static readonly OneTimeRunner OneTimeRunner = new OneTimeRunner();

    public static void Configure()
    {
        TradingPilotGlobalFeatureConfigurator.Configure();
        TradingPilotModuleExtensionConfigurator.Configure();

        OneTimeRunner.Run(() =>
        {
        });
    }
}
