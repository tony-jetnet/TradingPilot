namespace TradingPilot.Webull.Hook;

public class HookStatusDto
{
    public bool IsRunning { get; set; }
    public bool IsInjected { get; set; }
    public bool IsPipeConnected { get; set; }
    public int? WebullProcessId { get; set; }
    public int MessageCount { get; set; }
    public string? Error { get; set; }
}
