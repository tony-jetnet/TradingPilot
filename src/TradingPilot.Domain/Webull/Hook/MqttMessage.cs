namespace TradingPilot.Webull.Hook;

/// <summary>
/// Represents a captured MQTT message from the Webull hook.
/// </summary>
public class MqttMessage
{
    public DateTime Timestamp { get; init; }
    public string Topic { get; init; } = string.Empty;
    public byte[] Payload { get; init; } = [];
    public string? PayloadText { get; init; }
}
