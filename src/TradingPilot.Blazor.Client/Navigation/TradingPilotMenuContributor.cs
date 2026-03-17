using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using TradingPilot.Localization;
using Volo.Abp.UI.Navigation;

namespace TradingPilot.Blazor.Client.Navigation;

public class TradingPilotMenuContributor : IMenuContributor
{
    private readonly IConfiguration _configuration;

    public TradingPilotMenuContributor(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task ConfigureMenuAsync(MenuConfigurationContext context)
    {
        if (context.Menu.Name == StandardMenus.Main)
        {
            await ConfigureMainMenuAsync(context);
        }
    }

    private static Task ConfigureMainMenuAsync(MenuConfigurationContext context)
    {
        context.Menu.AddItem(new ApplicationMenuItem(
            TradingPilotMenus.Home,
            "Dashboard",
            "/",
            icon: "fas fa-chart-line",
            order: 1
        ));

        return Task.CompletedTask;
    }
}
