using Xunit;

namespace TradingPilot.EntityFrameworkCore;

[CollectionDefinition(TradingPilotTestConsts.CollectionDefinitionName)]
public class TradingPilotEntityFrameworkCoreCollection : ICollectionFixture<TradingPilotEntityFrameworkCoreFixture>
{

}
