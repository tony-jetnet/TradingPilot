using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingPilot.EntityFrameworkCore;

namespace TradingPilot.Trading;

/// <summary>
/// Fills in PriceAfter1Min/5Min for unverified TradingSignals by looking up
/// the nearest SymbolBookSnapshot at the target time offset.
/// Runs every 5 minutes. Without this, ~80% of signals lack outcome data
/// and are invisible to the nightly AI optimizer.
/// </summary>
[DisableConcurrentExecution(300)]
[AutomaticRetry(Attempts = 1)]
public class SignalVerificationJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SignalVerificationJob> _logger;

    public SignalVerificationJob(
        IServiceScopeFactory scopeFactory,
        ILogger<SignalVerificationJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task VerifyRecentSignalsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TradingPilotDbContext>();

        // Only verify signals older than 6 minutes (so 5-min outcome is available)
        // and younger than 3 days (SymbolBookSnapshots retention)
        int updated = await dbContext.Database.ExecuteSqlRawAsync(@"
            UPDATE ""TradingSignals"" ts
            SET
                ""PriceAfter1Min"" = COALESCE(ts.""PriceAfter1Min"", (
                    SELECT bs.""MidPrice"" FROM ""SymbolBookSnapshots"" bs
                    WHERE bs.""SymbolId"" = ts.""SymbolId""
                      AND bs.""Timestamp"" BETWEEN ts.""Timestamp"" + INTERVAL '55 seconds'
                                                AND ts.""Timestamp"" + INTERVAL '65 seconds'
                    ORDER BY ABS(EXTRACT(EPOCH FROM bs.""Timestamp"" - (ts.""Timestamp"" + INTERVAL '60 seconds')))
                    LIMIT 1
                )),
                ""PriceAfter5Min"" = COALESCE(ts.""PriceAfter5Min"", (
                    SELECT bs.""MidPrice"" FROM ""SymbolBookSnapshots"" bs
                    WHERE bs.""SymbolId"" = ts.""SymbolId""
                      AND bs.""Timestamp"" BETWEEN ts.""Timestamp"" + INTERVAL '295 seconds'
                                                AND ts.""Timestamp"" + INTERVAL '305 seconds'
                    ORDER BY ABS(EXTRACT(EPOCH FROM bs.""Timestamp"" - (ts.""Timestamp"" + INTERVAL '300 seconds')))
                    LIMIT 1
                )),
                ""VerifiedAt"" = NOW()
            WHERE ts.""VerifiedAt"" IS NULL
              AND ts.""Timestamp"" < NOW() - INTERVAL '6 minutes'
              AND ts.""Timestamp"" > NOW() - INTERVAL '3 days'");

        // Also compute WasCorrect1Min for newly verified signals
        if (updated > 0)
        {
            await dbContext.Database.ExecuteSqlRawAsync(@"
                UPDATE ""TradingSignals""
                SET ""WasCorrect1Min"" = CASE
                    WHEN ""PriceAfter1Min"" IS NOT NULL AND ""Type"" = 1 THEN ""PriceAfter1Min"" > ""Price""
                    WHEN ""PriceAfter1Min"" IS NOT NULL AND ""Type"" = 2 THEN ""PriceAfter1Min"" < ""Price""
                    ELSE NULL END,
                    ""WasCorrect5Min"" = CASE
                    WHEN ""PriceAfter5Min"" IS NOT NULL AND ""Type"" = 1 THEN ""PriceAfter5Min"" > ""Price""
                    WHEN ""PriceAfter5Min"" IS NOT NULL AND ""Type"" = 2 THEN ""PriceAfter5Min"" < ""Price""
                    ELSE NULL END
                WHERE ""VerifiedAt"" IS NOT NULL
                  AND ""WasCorrect1Min"" IS NULL
                  AND ""PriceAfter1Min"" IS NOT NULL");

            _logger.LogInformation("Signal verification: updated {Count} signals with price outcomes", updated);
        }
    }
}
