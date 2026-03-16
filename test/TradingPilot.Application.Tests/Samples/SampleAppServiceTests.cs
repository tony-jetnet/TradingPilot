using System.Threading.Tasks;
using Volo.Abp.Modularity;
using Xunit;

namespace TradingPilot.Samples;

public abstract class SampleAppServiceTests<TStartupModule> : TradingPilotApplicationTestBase<TStartupModule>
    where TStartupModule : IAbpModule
{
    [Fact]
    public Task Sample_Test()
    {
        // Add your application service tests here
        return Task.CompletedTask;
    }
}
