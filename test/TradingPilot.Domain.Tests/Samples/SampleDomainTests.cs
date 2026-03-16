using System.Threading.Tasks;
using Volo.Abp.Modularity;
using Xunit;

namespace TradingPilot.Samples;

public abstract class SampleDomainTests<TStartupModule> : TradingPilotDomainTestBase<TStartupModule>
    where TStartupModule : IAbpModule
{
    [Fact]
    public Task Sample_Test()
    {
        // Add your domain tests here
        return Task.CompletedTask;
    }
}
