namespace TradingPilot;

public static class MarketHoursHelper
{
    private static readonly TimeZoneInfo Eastern =
        TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    private static readonly TimeOnly MarketOpen = new(7, 30);
    private static readonly TimeOnly MarketClose = new(18, 0);

    public static bool IsMarketOpen(DateTime utcNow)
    {
        var eastern = TimeZoneInfo.ConvertTimeFromUtc(utcNow, Eastern);
        if (eastern.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;

        var time = TimeOnly.FromDateTime(eastern);
        return time >= MarketOpen && time < MarketClose;
    }

    public static DateTime GetMarketOpenUtc(DateTime utcDate)
    {
        var eastern = TimeZoneInfo.ConvertTimeFromUtc(utcDate, Eastern);
        var openLocal = eastern.Date.Add(MarketOpen.ToTimeSpan());
        return TimeZoneInfo.ConvertTimeToUtc(openLocal, Eastern);
    }

    public static DateTime GetMarketCloseUtc(DateTime utcDate)
    {
        var eastern = TimeZoneInfo.ConvertTimeFromUtc(utcDate, Eastern);
        var closeLocal = eastern.Date.Add(MarketClose.ToTimeSpan());
        return TimeZoneInfo.ConvertTimeToUtc(closeLocal, Eastern);
    }
}
