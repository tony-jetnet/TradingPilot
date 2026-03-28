namespace TradingPilot.Trading;

/// <summary>
/// Weights for the three-layer composite scoring system.
/// Loaded from model_config.json (nightly trained) or defaults from DayTradeConfig.
/// SetupWeight + TimingWeight + ContextWeight must sum to 1.0.
/// </summary>
public class ScoringWeights
{
    /// <summary>Weight for bar-based setup strength in composite score. Default 0.50.</summary>
    public decimal SetupWeight { get; set; } = DayTradeConfig.DefaultSetupWeight;

    /// <summary>Weight for L2 timing score in composite score. Default 0.30.</summary>
    public decimal TimingWeight { get; set; } = DayTradeConfig.DefaultTimingWeight;

    /// <summary>Weight for news/fundamental context score in composite score. Default 0.20.</summary>
    public decimal ContextWeight { get; set; } = DayTradeConfig.DefaultContextWeight;

    /// <summary>
    /// Validate and normalize weights to sum to 1.0.
    /// If any weight is negative, reset to defaults.
    /// </summary>
    public void Normalize()
    {
        if (SetupWeight < 0 || TimingWeight < 0 || ContextWeight < 0)
        {
            SetupWeight = DayTradeConfig.DefaultSetupWeight;
            TimingWeight = DayTradeConfig.DefaultTimingWeight;
            ContextWeight = DayTradeConfig.DefaultContextWeight;
            return;
        }

        decimal sum = SetupWeight + TimingWeight + ContextWeight;
        if (sum <= 0)
        {
            SetupWeight = DayTradeConfig.DefaultSetupWeight;
            TimingWeight = DayTradeConfig.DefaultTimingWeight;
            ContextWeight = DayTradeConfig.DefaultContextWeight;
            return;
        }

        SetupWeight /= sum;
        TimingWeight /= sum;
        ContextWeight /= sum;
    }

    /// <summary>Create default weights from DayTradeConfig constants.</summary>
    public static ScoringWeights Default() => new()
    {
        SetupWeight = DayTradeConfig.DefaultSetupWeight,
        TimingWeight = DayTradeConfig.DefaultTimingWeight,
        ContextWeight = DayTradeConfig.DefaultContextWeight,
    };
}
