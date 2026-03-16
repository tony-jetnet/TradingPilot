using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace TradingPilot.Webull.Hook;

/// <summary>
/// Named pipe server for receiving commands from the app (subscribe, ping, status).
/// Pipe name: WebullMqttHookCmd
/// Protocol: newline-delimited JSON, one command per line, response on same line.
/// </summary>
internal static unsafe class CommandReceiver
{
    private const string PipeName = "WebullMqttHookCmd";
    private static volatile bool _running;

    public static void Start()
    {
        _running = true;
        var thread = new Thread(RunServer)
        {
            IsBackground = true,
            Name = "WebullHook-CmdReceiver"
        };
        thread.Start();
    }

    private static void RunServer()
    {
        while (_running)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                HookLog.Write("CmdPipe: waiting for client...");
                pipe.WaitForConnection();
                HookLog.Write("CmdPipe: client connected.");

                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

                while (pipe.IsConnected)
                {
                    string? line = reader.ReadLine();
                    if (line == null) break;

                    string response = HandleCommand(line);
                    writer.WriteLine(response);
                }

                HookLog.Write("CmdPipe: client disconnected.");
            }
            catch (Exception ex)
            {
                HookLog.Write($"CmdPipe error: {ex.Message}");
                Thread.Sleep(1000);
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private static string HandleCommand(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string cmd = root.GetProperty("cmd").GetString() ?? "";

            return cmd switch
            {
                "ping" => HandlePing(),
                "status" => HandleStatus(),
                "subscribe" => HandleSubscribe(root),
                _ => $$$"""{"ok":false,"error":"unknown command: {{{cmd}}}"}"""
            };
        }
        catch (Exception ex)
        {
            // Escape any quotes in the error message
            string escaped = ex.Message.Replace("\\", "\\\\").Replace("\"", "\\\"");
            return $$$"""{"ok":false,"error":"{{{escaped}}}"}""";
        }
    }

    private static string HandlePing()
    {
        return """{"ok":true,"msg":"pong"}""";
    }

    private static string HandleStatus()
    {
        return $$$"""{"ok":true,"mqttClient":"0x{{{HookEntry.MqttClient:X}}}","hasClient":{{{(HookEntry.MqttClient != 0 ? "true" : "false")}}},"hasInvokeSubscribe":{{{(HookEntry.OrigInvokeSubscribeResult != 0 ? "true" : "false")}}},"messageCount":{{{HookEntry.MessageCount}}},"subscribeCount":{{{HookEntry.SubscribeCount}}}}""";
    }

    private static string HandleSubscribe(JsonElement root)
    {
        string subscriptionJson = root.GetProperty("json").GetString() ?? "";
        if (string.IsNullOrEmpty(subscriptionJson))
            return """{"ok":false,"error":"missing 'json' field"}""";

        nint client = HookEntry.MqttClient;
        nint origFn = HookEntry.OrigInvokeSubscribeResult;

        if (client == 0)
            return """{"ok":false,"error":"no QMqttClient captured yet"}""";
        if (origFn == 0)
            return """{"ok":false,"error":"invokeSubscribeResult not hooked"}""";

        HookLog.Write($"CmdPipe: subscribe json={subscriptionJson[..Math.Min(subscriptionJson.Length, 100)]}...");

        // Build a Qt QString and call invokeSubscribeResult
        using var qstr = QtString.Create(subscriptionJson);

        var invokeSubscribe = (delegate* unmanaged<nint, nint, nint>)origFn;
        nint result = invokeSubscribe(client, qstr.Ptr);

        HookLog.Write($"CmdPipe: invokeSubscribeResult returned {result}");
        return $$$"""{"ok":true,"reasonCode":{{{(long)result}}}}""";
    }
}
