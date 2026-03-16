namespace TradingPilot.Webull;

public class MqttMessageDto
{
    public DateTime Timestamp { get; set; }
    public string Topic { get; set; } = string.Empty;
    public int PayloadSize { get; set; }
    public string? PayloadText { get; set; }
    public string? PayloadHex { get; set; }
}
