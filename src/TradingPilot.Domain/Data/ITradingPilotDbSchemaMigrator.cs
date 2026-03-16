using System.Threading.Tasks;

namespace TradingPilot.Data;

public interface ITradingPilotDbSchemaMigrator
{
    Task MigrateAsync();
}
