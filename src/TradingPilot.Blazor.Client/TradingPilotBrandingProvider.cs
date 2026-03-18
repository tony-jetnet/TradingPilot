using Microsoft.Extensions.Localization;
using TradingPilot.Localization;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Ui.Branding;

namespace TradingPilot.Blazor.Client;

[Dependency(ReplaceServices = true)]
public class TradingPilotBrandingProvider(IStringLocalizer<TradingPilotResource> localizer) : DefaultBrandingProvider
{
    public override string AppName => localizer["AppName"];
}
