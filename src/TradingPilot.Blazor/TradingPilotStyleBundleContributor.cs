using Volo.Abp.AspNetCore.Mvc.UI.Bundling;

namespace TradingPilot.Blazor;

public class TradingPilotStyleBundleContributor : BundleContributor
{
    public override void ConfigureBundle(BundleConfigurationContext context)
    {
        context.Files.Add(new BundleFile("main.css", true));
    }
}
