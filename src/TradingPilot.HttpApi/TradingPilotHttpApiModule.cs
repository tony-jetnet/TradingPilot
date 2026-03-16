using Localization.Resources.AbpUi;
using TradingPilot.Localization;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.Localization;
using Volo.Abp.Modularity;

namespace TradingPilot;

[DependsOn(
    typeof(TradingPilotApplicationContractsModule),
    typeof(AbpAspNetCoreMvcModule)
)]
public class TradingPilotHttpApiModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        Configure<AbpLocalizationOptions>(options =>
        {
            options.Resources
                .Get<TradingPilotResource>()
                .AddBaseTypes(typeof(AbpUiResource));
        });
    }
}
