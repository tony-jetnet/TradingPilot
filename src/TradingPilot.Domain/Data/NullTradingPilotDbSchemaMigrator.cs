using System.Threading.Tasks;
using Volo.Abp.DependencyInjection;

namespace TradingPilot.Data;

/* This is used if database provider does't define
 * ITradingPilotDbSchemaMigrator implementation.
 */
public class NullTradingPilotDbSchemaMigrator : ITradingPilotDbSchemaMigrator, ITransientDependency
{
    public Task MigrateAsync()
    {
        return Task.CompletedTask;
    }
}
