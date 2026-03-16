using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace TradingPilot.Webull;

/// <summary>
/// Connects to the hook's command pipe to send commands (subscribe, ping, status).
/// Pipe name: WebullMqttHookCmd
/// Protocol: newline-delimited JSON request/response.
/// </summary>
public sealed class MqttCommandWriter : IDisposable
{
    private const string PipeName = "WebullMqttHookCmd";
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly object _lock = new();

    public bool IsConnected => _pipe?.IsConnected == true;

    public async Task ConnectAsync(int timeoutMs = 5000, CancellationToken ct = default)
    {
        _pipe?.Dispose();
        _pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
        await _pipe.ConnectAsync(timeoutMs, ct);
        _reader = new StreamReader(_pipe, new UTF8Encoding(false), leaveOpen: true);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    }

    public async Task<JsonDocument> SendCommandAsync(object command, CancellationToken ct = default)
    {
        if (_pipe == null || !_pipe.IsConnected)
            throw new InvalidOperationException("Not connected to command pipe.");

        string json = JsonSerializer.Serialize(command);
        await _writer!.WriteLineAsync(json.AsMemory(), ct);

        string? response = await _reader!.ReadLineAsync(ct);
        if (response == null)
            throw new IOException("Command pipe disconnected.");

        return JsonDocument.Parse(response);
    }

    public async Task<JsonDocument> PingAsync(CancellationToken ct = default)
    {
        return await SendCommandAsync(new { cmd = "ping" }, ct);
    }

    public async Task<JsonDocument> GetStatusAsync(CancellationToken ct = default)
    {
        return await SendCommandAsync(new { cmd = "status" }, ct);
    }

    public async Task<JsonDocument> SubscribeAsync(string subscriptionJson, CancellationToken ct = default)
    {
        return await SendCommandAsync(new { cmd = "subscribe", json = subscriptionJson }, ct);
    }

    public async Task<JsonDocument> ReconnectAsync(CancellationToken ct = default)
    {
        return await SendCommandAsync(new { cmd = "reconnect" }, ct);
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _pipe?.Dispose();
    }
}
