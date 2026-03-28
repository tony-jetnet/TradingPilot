namespace TradingPilot.Trading;

/// <summary>
/// Identifies what generated a trading signal.
/// L2Micro = legacy L2 microstructure timing (scalping origin).
/// BarSetup = bar-based setup detection (day trading).
/// AiRule = Bedrock AI-generated conditional rule.
/// Composite = full pipeline: setup + L2 timing + context scoring.
/// </summary>
public enum SignalSource : byte
{
    L2Micro = 0,
    BarSetup = 1,
    AiRule = 2,
    Composite = 3,
}
