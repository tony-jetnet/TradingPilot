using System.IO;

namespace TradingPilot.Webull.Hook;

/// <summary>
/// Simple file logger for debugging the injected DLL.
/// </summary>
internal static class HookLog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WebullHook",
        "hook.log");

    private static readonly object _lock = new();

    static HookLog()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
    }

    public static void Write(string message)
    {
        lock (_lock)
        {
            try
            {
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
            }
            catch { }
        }
    }
}
