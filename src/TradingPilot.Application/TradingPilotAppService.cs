using TradingPilot.Localization;
using Volo.Abp.Application.Services;

namespace TradingPilot;

/* Inherit your application services from this class.
 */
public abstract class TradingPilotAppService : ApplicationService
{
    protected TradingPilotAppService()
    {
        LocalizationResource = typeof(TradingPilotResource);
    }
}
