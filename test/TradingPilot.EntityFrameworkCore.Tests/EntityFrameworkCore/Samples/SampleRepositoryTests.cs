using System.Threading.Tasks;
using Xunit;

namespace TradingPilot.EntityFrameworkCore.Samples;

[Collection(TradingPilotTestConsts.CollectionDefinitionName)]
public class SampleRepositoryTests : TradingPilotEntityFrameworkCoreTestBase
{
    [Fact]
    public Task Sample_Test()
    {
        // Add your custom repository tests here
        return Task.CompletedTask;
    }
}
