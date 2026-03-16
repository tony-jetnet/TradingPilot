using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Volo.Abp.Application.Services;

namespace TradingPilot.Webull;

public class WebullHookAppService : ApplicationService, IWebullHookAppService
{
    private readonly ProcessInjector _processInjector;
    private readonly MqttDataReader _mqttDataReader;

    // Shared state for the running hook session
    private static readonly ConcurrentQueue<MqttMessage> _recentMessages = new();
    private static volatile int _messageCount;
    private static CancellationTokenSource? _cts;
    private static Task? _readerTask;
    private static volatile bool _isInjected;
    private static volatile bool _isPipeConnected;
    private static int? _webullPid;
    private static string? _lastError;
    private static MqttCommandWriter? _commandWriter;
    private static string? _capturedAuthHeader;
    private static string? _lastGrpcSign;
    private static DateTime _lastGrpcSignTime;

    public static string? LastGrpcSign => _lastGrpcSign;

    private static readonly string AuthFilePath = Path.Combine(
        @"D:\Third-Parties\WebullHook", "auth_header.json");

    public static string? CapturedAuthHeader => _capturedAuthHeader;

    private const int MaxRecentMessages = 500;

    static WebullHookAppService()
    {
        // Load persisted auth header on first access
        try
        {
            if (File.Exists(AuthFilePath))
                _capturedAuthHeader = File.ReadAllText(AuthFilePath).Trim();
        }
        catch { }
    }

    public WebullHookAppService(
        ProcessInjector processInjector,
        MqttDataReader mqttDataReader)
    {
        _processInjector = processInjector;
        _mqttDataReader = mqttDataReader;
    }

    public Task<HookStatusDto> GetStatusAsync()
    {
        return Task.FromResult(BuildStatus());
    }

    public Task<HookStatusDto> StartAsync()
    {
        if (_cts != null && !_cts.IsCancellationRequested)
        {
            Logger.LogWarning("Hook is already running.");
            return Task.FromResult(BuildStatus());
        }

        _lastError = null;

        // Find Webull process
        var webull = _processInjector.FindWebullProcess();
        if (webull == null)
        {
            _lastError = "Webull Desktop is not running.";
            return Task.FromResult(BuildStatus());
        }
        _webullPid = webull.Id;

        // Locate hook DLL
        string hookDllPath = ResolveHookDllPath();
        if (!File.Exists(hookDllPath))
        {
            _lastError = $"Hook DLL not found at {hookDllPath}. Run: dotnet publish src/TradingPilot.Webull.Hook -c Release";
            return Task.FromResult(BuildStatus());
        }

        // Inject
        try
        {
            _processInjector.Inject(webull.Id, hookDllPath);
            _isInjected = true;
        }
        catch (Exception ex)
        {
            _lastError = $"Injection failed: {ex.Message}. Try running as Administrator.";
            return Task.FromResult(BuildStatus());
        }

        // Start pipe reader
        _cts = new CancellationTokenSource();
        _mqttDataReader.MessageReceived += OnMessageReceived;
        _mqttDataReader.EventReceived += OnEventReceived;
        _readerTask = Task.Run(async () =>
        {
            try
            {
                _isPipeConnected = true;
                await _mqttDataReader.StartAsync(_cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _lastError = $"Pipe reader error: {ex.Message}";
                Logger.LogError(ex, "Pipe reader error");
            }
            finally
            {
                _isPipeConnected = false;
            }
        });

        return Task.FromResult(BuildStatus());
    }

    public async Task<HookStatusDto> StopAsync()
    {
        if (_cts != null)
        {
            await _cts.CancelAsync();
            if (_readerTask != null)
            {
                try { await _readerTask.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch { /* timeout is fine */ }
            }
            _mqttDataReader.MessageReceived -= OnMessageReceived;
            _mqttDataReader.EventReceived -= OnEventReceived;
            _commandWriter?.Dispose();
            _commandWriter = null;
            _cts.Dispose();
            _cts = null;
            _readerTask = null;
            _isInjected = false;
            _isPipeConnected = false;
            _webullPid = null;
        }

        return BuildStatus();
    }

    public Task<List<MqttMessageDto>> GetRecentMessagesAsync(int count = 50)
    {
        var messages = _recentMessages
            .Reverse()
            .Take(count)
            .Select(m => new MqttMessageDto
            {
                Timestamp = m.Timestamp,
                Topic = m.Topic,
                PayloadSize = m.Payload.Length,
                PayloadText = m.PayloadText,
                PayloadHex = m.PayloadText == null
                    ? Convert.ToHexString(m.Payload.AsSpan(0, Math.Min(m.Payload.Length, 200)))
                    : null
            })
            .ToList();

        return Task.FromResult(messages);
    }

    public async Task<string> PingHookAsync()
    {
        var writer = await GetOrConnectCommandWriterAsync();
        using var result = await writer.PingAsync();
        return result.RootElement.ToString();
    }

    public async Task<string> SubscribeTickerAsync(long tickerId, int[] types)
    {
        var writer = await GetOrConnectCommandWriterAsync();

        // Use captured auth header if available, otherwise use a minimal template
        string? header = _capturedAuthHeader;
        if (header == null)
            return JsonSerializer.Serialize(new { ok = false, error = "No auth header captured yet. Open a stock in Webull first." });

        var results = new List<string>();
        foreach (int type in types)
        {
            string json = BuildSubscriptionJson(header, tickerId, type);
            using var result = await writer.SubscribeAsync(json);
            results.Add($"type={type}: {result.RootElement}");
        }

        return string.Join("\n", results);
    }

    private static async Task<MqttCommandWriter> GetOrConnectCommandWriterAsync()
    {
        if (_commandWriter?.IsConnected == true)
            return _commandWriter;

        _commandWriter?.Dispose();
        _commandWriter = new MqttCommandWriter();
        await _commandWriter.ConnectAsync();
        return _commandWriter;
    }

    private static string BuildSubscriptionJson(string headerJson, long tickerId, int type)
    {
        string flag = type is 91 or 105 ? "1,50,1" : "1";
        return $$"""{"flag":"{{flag}}","header":{{headerJson}},"module":"[\"OtherStocks\"]","tickerIds":[{{tickerId}}],"type":"{{type}}"}""";
    }

    private static void OnMessageReceived(string topic, byte[] payload)
    {
        _messageCount++;

        string? text = TryDecodeUtf8(payload);
        var msg = new MqttMessage
        {
            Timestamp = DateTime.UtcNow,
            Topic = topic,
            Payload = payload,
            PayloadText = text
        };

        _recentMessages.Enqueue(msg);

        // Trim old messages
        while (_recentMessages.Count > MaxRecentMessages)
            _recentMessages.TryDequeue(out _);
    }

    private static void OnEventReceived(string eventType, byte[] data)
    {
        string text = Encoding.UTF8.GetString(data);
        if (eventType == "grpc_sign")
        {
            _lastGrpcSign = text;
            _lastGrpcSignTime = DateTime.UtcNow;
        }
        else if (eventType == "subscribe")
        {
            // Extract and persist the auth header from subscription JSON
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("header", out var header))
                {
                    _capturedAuthHeader = header.ToString();
                    // Persist to file for reuse across restarts
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(AuthFilePath)!);
                        File.WriteAllText(AuthFilePath, _capturedAuthHeader);
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    private static string? TryDecodeUtf8(byte[] data)
    {
        try
        {
            string s = Encoding.UTF8.GetString(data);
            foreach (char c in s)
            {
                if (c != '\n' && c != '\r' && c != '\t' && (c < ' ' || c > '~') && c < 128)
                    return null;
            }
            return s;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveHookDllPath()
    {
        // Check next to the running app first
        string path = Path.Combine(AppContext.BaseDirectory, "TradingPilot.Webull.Hook.dll");
        if (File.Exists(path)) return path;

        // Check the publish output
        path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TradingPilot.Webull.Hook", "bin", "Release", "net10.0", "win-x64", "publish", "TradingPilot.Webull.Hook.dll");
        path = Path.GetFullPath(path);
        if (File.Exists(path)) return path;

        // Check relative from solution root (common dev scenario)
        path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "TradingPilot.Webull.Hook", "bin", "Release", "net10.0", "win-x64", "publish", "TradingPilot.Webull.Hook.dll"));
        return path;
    }

    private static HookStatusDto BuildStatus() => new()
    {
        IsRunning = _cts != null && !_cts.IsCancellationRequested,
        IsInjected = _isInjected,
        IsPipeConnected = _isPipeConnected,
        WebullProcessId = _webullPid,
        MessageCount = _messageCount,
        Error = _lastError
    };
}
