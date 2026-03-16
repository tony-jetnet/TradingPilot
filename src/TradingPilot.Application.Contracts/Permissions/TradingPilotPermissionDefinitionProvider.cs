using TradingPilot.Localization;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Localization;
using Volo.Abp.MultiTenancy;

namespace TradingPilot.Permissions;

public class TradingPilotPermissionDefinitionProvider : PermissionDefinitionProvider
{
    public override void Define(IPermissionDefinitionContext context)
    {
        var myGroup = context.AddGroup(TradingPilotPermissions.GroupName);

        //Define your own permissions here. Example:
        //myGroup.AddPermission(TradingPilotPermissions.MyPermission1, L("Permission:MyPermission1"));
    }

    private static LocalizableString L(string name)
    {
        return LocalizableString.Create<TradingPilotResource>(name);
    }
}
