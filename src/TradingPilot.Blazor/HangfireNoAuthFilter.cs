using Hangfire.Dashboard;

namespace TradingPilot.Blazor;

/// <summary>Allow anonymous access to Hangfire dashboard (dev/local only)</summary>
public class HangfireNoAuthFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context) => true;
}
