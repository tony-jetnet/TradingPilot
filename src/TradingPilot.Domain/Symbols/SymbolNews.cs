using Volo.Abp.Domain.Entities;

namespace TradingPilot.Symbols;

public class SymbolNews : Entity<Guid>
{
    public string SymbolId { get; set; } = null!;
    public long WebullNewsId { get; set; }
    public string Title { get; set; } = null!;
    public string? Summary { get; set; }
    public string? SourceName { get; set; }
    public string? Url { get; set; }
    public DateTime PublishedAt { get; set; }
    public DateTime CollectedAt { get; set; }

    // ── News scoring (filled by nightly Bedrock batch or live keyword matching) ──
    /// <summary>Sentiment score [-1, +1]. Null until scored.</summary>
    public decimal? SentimentScore { get; set; }
    /// <summary>"KEYWORD" (live, free) or "BEDROCK" (nightly, higher quality).</summary>
    public string? SentimentMethod { get; set; }
    /// <summary>EARNINGS, ANALYST, REGULATORY, SECTOR, CORPORATE, or null if not a catalyst.</summary>
    public string? CatalystType { get; set; }
    /// <summary>When sentiment was scored.</summary>
    public DateTime? ScoredAt { get; set; }
}
