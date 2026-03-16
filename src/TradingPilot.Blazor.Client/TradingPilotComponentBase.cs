using TradingPilot.Localization;
using Volo.Abp.AspNetCore.Components;

namespace TradingPilot.Blazor.Client;

public abstract class TradingPilotComponentBase : AbpComponentBase
{
    protected TradingPilotComponentBase()
    {
        LocalizationResource = typeof(TradingPilotResource);
    }
}
