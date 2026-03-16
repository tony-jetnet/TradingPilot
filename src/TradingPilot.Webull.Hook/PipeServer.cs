using System.IO.Pipes;
using System.Text;

namespace TradingPilot.Webull.Hook;

/// <summary>
/// Named pipe server running inside the Webull process.
/// Sends intercepted MQTT messages and hook events to the external app.
/// Protocol: [1 byte type][4 bytes topic/eventType length][topic/eventType UTF-8][4 bytes payload length][payload bytes]
/// Type: 0x00 = mqtt_message, 0x01 = hook_event
/// </summary>
internal static class PipeServer
{
    private const string PipeName = "WebullMqttHook";
    private const byte TypeMqttMessage = 0x00;
    private const byte TypeHookEvent = 0x01;

    private static NamedPipeServerStream? _pipe;
    private static readonly object _lock = new();
    private static volatile bool _connected;

    public static void Start()
    {
        var thread = new Thread(RunServer)
        {
            IsBackground = true,
            Name = "WebullHook-PipeServer"
        };
        thread.Start();
    }

    private static void RunServer()
    {
        while (true)
        {
            try
            {
                _pipe?.Dispose();
                _pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.Out,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                HookLog.Write("Pipe: waiting for client...");
                _pipe.WaitForConnection();
                _connected = true;
                HookLog.Write("Pipe: client connected.");

                // Keep alive until disconnected
                while (_pipe.IsConnected)
                {
                    Thread.Sleep(100);
                }

                _connected = false;
                HookLog.Write("Pipe: client disconnected.");
            }
            catch (Exception ex)
            {
                _connected = false;
                HookLog.Write($"Pipe error: {ex.Message}");
                Thread.Sleep(1000);
            }
        }
    }

    /// <summary>Send an intercepted MQTT message (type 0x00).</summary>
    public static void SendMessage(string topic, ReadOnlySpan<byte> payload)
    {
        SendRaw(TypeMqttMessage, topic, payload);
    }

    /// <summary>Send a hook event (type 0x01) - connection changes, subscription events, etc.</summary>
    public static void SendEvent(string eventType, string data)
    {
        byte[] dataBytes = Encoding.UTF8.GetBytes(data);
        SendRaw(TypeHookEvent, eventType, dataBytes);
    }

    private static void SendRaw(byte type, string name, ReadOnlySpan<byte> payload)
    {
        if (!_connected || _pipe == null) return;

        lock (_lock)
        {
            try
            {
                if (!_pipe.IsConnected) { _connected = false; return; }

                byte[] nameBytes = Encoding.UTF8.GetBytes(name);
                Span<byte> lenBuf = stackalloc byte[4];

                // Write message type byte
                _pipe.WriteByte(type);

                // Write name length + name
                BitConverter.TryWriteBytes(lenBuf, nameBytes.Length);
                _pipe.Write(lenBuf);
                _pipe.Write(nameBytes);

                // Write payload length + payload
                BitConverter.TryWriteBytes(lenBuf, payload.Length);
                _pipe.Write(lenBuf);
                _pipe.Write(payload);

                _pipe.Flush();
            }
            catch
            {
                _connected = false;
            }
        }
    }
}
