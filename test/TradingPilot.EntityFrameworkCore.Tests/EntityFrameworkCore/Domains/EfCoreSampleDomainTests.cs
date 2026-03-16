using TradingPilot.Samples;
using Xunit;

namespace TradingPilot.EntityFrameworkCore.Domains;

[Collection(TradingPilotTestConsts.CollectionDefinitionName)]
public class EfCoreSampleDomainTests : SampleDomainTests<TradingPilotEntityFrameworkCoreTestModule>
{

}
