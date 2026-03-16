using TradingPilot.Localization;
using Volo.Abp.AspNetCore.Mvc;

namespace TradingPilot.Controllers;

/* Inherit your controllers from this class.
 */
public abstract class TradingPilotController : AbpControllerBase
{
    protected TradingPilotController()
    {
        LocalizationResource = typeof(TradingPilotResource);
    }
}
