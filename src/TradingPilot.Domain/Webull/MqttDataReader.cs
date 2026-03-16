using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;

namespace TradingPilot.Webull;

/// <summary>
/// Reads intercepted MQTT messages from the hook's TCP server (localhost:19880).
/// Protocol: [1 byte type][4 bytes name length][name UTF-8][4 bytes payload length][payload bytes]
/// Type: 0x00 = mqtt_message, 0x01 = hook_event
/// </summary>
public sealed class MqttDataReader : ITransientDependency, IDisposable
{
    private const int TcpPort = 19880;
    private TcpClient? _tcp;
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
                _tcp?.Dispose();
                _tcp = new TcpClient();

                _logger.LogInformation("Connecting to hook TCP server (localhost:{Port})...", TcpPort);
                await _tcp.ConnectAsync("127.0.0.1", TcpPort, _cts.Token);
                _logger.LogInformation("Connected to hook TCP server. Receiving messages...");

                await ReadLoop(_tcp.GetStream(), _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("TCP error: {Message}. Reconnecting in 2s...", ex.Message);
                try { await Task.Delay(2000, _cts.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ReadLoop(NetworkStream stream, CancellationToken ct)
    {
        byte[] typeBuf = new byte[1];
        byte[] lenBuf = new byte[4];

        while (!ct.IsCancellationRequested)
        {
            // Read message type
            if (!await ReadExactAsync(stream, typeBuf, ct)) break;
            byte msgType = typeBuf[0];

            // Read name (topic or eventType)
            if (!await ReadExactAsync(stream, lenBuf, ct)) break;
            int nameLen = BitConverter.ToInt32(lenBuf);
            if (nameLen < 0 || nameLen > 1024 * 1024) break;

            string name = string.Empty;
            if (nameLen > 0)
            {
                byte[] nameBuf = new byte[nameLen];
                if (!await ReadExactAsync(stream, nameBuf, ct)) break;
                name = Encoding.UTF8.GetString(nameBuf);
            }

            // Read payload
            if (!await ReadExactAsync(stream, lenBuf, ct)) break;
            int payloadLen = BitConverter.ToInt32(lenBuf);
            if (payloadLen < 0 || payloadLen > 10 * 1024 * 1024) break;

            byte[] payload = new byte[payloadLen];
            if (payloadLen > 0 && !await ReadExactAsync(stream, payload, ct)) break;

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
            if (read == 0) return false; // connection closed
            offset += read;
        }
        return true;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _tcp?.Dispose();
    }
}
