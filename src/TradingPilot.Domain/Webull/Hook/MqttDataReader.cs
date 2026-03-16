using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;

namespace TradingPilot.Webull.Hook;

/// <summary>
/// Reads intercepted MQTT messages and hook events from the named pipe exposed by the injected hook DLL.
/// Protocol: [1 byte type][4 bytes name length][name UTF-8][4 bytes payload length][payload bytes]
/// Type: 0x00 = mqtt_message, 0x01 = hook_event
/// </summary>
public sealed class MqttDataReader : ITransientDependency, IDisposable
{
    private const string PipeName = "WebullMqttHook";
    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _cts;
    private readonly ILogger<MqttDataReader> _logger;

    /// <summary>Fired for MQTT messages (topic, payload).</summary>
    public event Action<string, byte[]>? MessageReceived;

    /// <summary>Fired for hook events (eventType, data).</summary>
    public event Action<string, byte[]>? EventReceived;

    public MqttDataReader(ILogger<MqttDataReader> logger)
    {
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                _pipe?.Dispose();
                _pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.In);

                _logger.LogInformation("Connecting to hook pipe...");
                await _pipe.ConnectAsync(5000, _cts.Token);
                _logger.LogInformation("Connected to hook pipe. Receiving messages...");

                await ReadLoop(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Pipe error: {Message}. Reconnecting in 2s...", ex.Message);
                await Task.Delay(2000, _cts.Token);
            }
        }
    }

    private async Task ReadLoop(CancellationToken ct)
    {
        byte[] typeBuf = new byte[1];
        byte[] lenBuf = new byte[4];

        while (!ct.IsCancellationRequested && _pipe!.IsConnected)
        {
            // Read message type
            if (!await ReadExactAsync(_pipe, typeBuf, ct)) break;
            byte msgType = typeBuf[0];

            // Read name (topic or eventType)
            if (!await ReadExactAsync(_pipe, lenBuf, ct)) break;
            int nameLen = BitConverter.ToInt32(lenBuf);
            if (nameLen <= 0 || nameLen > 1024 * 1024) break;

            byte[] nameBuf = new byte[nameLen];
            if (!await ReadExactAsync(_pipe, nameBuf, ct)) break;
            string name = Encoding.UTF8.GetString(nameBuf);

            // Read payload
            if (!await ReadExactAsync(_pipe, lenBuf, ct)) break;
            int payloadLen = BitConverter.ToInt32(lenBuf);
            if (payloadLen < 0 || payloadLen > 10 * 1024 * 1024) break;

            byte[] payload = new byte[payloadLen];
            if (payloadLen > 0 && !await ReadExactAsync(_pipe, payload, ct)) break;

            if (msgType == 0x01)
                EventReceived?.Invoke(name, payload);
            else
                MessageReceived?.Invoke(name, payload);
        }
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _pipe?.Dispose();
    }
}
