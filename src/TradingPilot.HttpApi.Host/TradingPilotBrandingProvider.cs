using Microsoft.Extensions.Localization;
using TradingPilot.Localization;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Ui.Branding;

namespace TradingPilot;

[Dependency(ReplaceServices = true)]
public class TradingPilotBrandingProvider : DefaultBrandingProvider
{
    private IStringLocalizer<TradingPilotResource> _localizer;

    public TradingPilotBrandingProvider(IStringLocalizer<TradingPilotResource> localizer)
    {
        _localizer = localizer;
    }

    public override string AppName => _localizer["AppName"];
}
