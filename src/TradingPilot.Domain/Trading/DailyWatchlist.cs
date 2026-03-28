using Volo.Abp.Domain.Entities;

namespace TradingPilot.Trading;

/// <summary>
/// Pre-market scanner output: top 10 symbols selected for active trading each day.
/// One row per day. Selections stored as JSONB array with per-symbol scores.
/// Retained forever for scanner accuracy evaluation.
/// </summary>
public class DailyWatchlist : Entity<Guid>
{
    public DailyWatchlist() : base(Guid.NewGuid()) { }

    /// <summary>Trading day (ET date).</summary>
    public DateOnly Date { get; set; }

    /// <summary>
    /// JSONB array of selected symbols with scores.
    /// Schema: [{"symbol":"NVDA","tickerId":913256789,"rankScore":0.82,"gapPct":0.035,
    ///           "premarketVolRatio":2.1,"catalystType":"EARNINGS","setupQuality":0.65,"atrPct":0.012}]
    /// </summary>
    public string Selections { get; set; } = "[]";

    public DateTime CreatedAt { get; set; }
}
