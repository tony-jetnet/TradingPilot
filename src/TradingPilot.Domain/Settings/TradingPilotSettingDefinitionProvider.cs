using Volo.Abp.Settings;

namespace TradingPilot.Settings;

public class TradingPilotSettingDefinitionProvider : SettingDefinitionProvider
{
    public override void Define(ISettingDefinitionContext context)
    {
        //Define your own settings here. Example:
        //context.Add(new SettingDefinition(TradingPilotSettings.MySetting1));
    }
}
