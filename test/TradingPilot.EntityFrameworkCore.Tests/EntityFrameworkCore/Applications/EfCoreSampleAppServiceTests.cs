using TradingPilot.Samples;
using Xunit;

namespace TradingPilot.EntityFrameworkCore.Applications;

[Collection(TradingPilotTestConsts.CollectionDefinitionName)]
public class EfCoreSampleAppServiceTests : SampleAppServiceTests<TradingPilotEntityFrameworkCoreTestModule>
{

}
