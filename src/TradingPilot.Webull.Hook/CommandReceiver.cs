using System.IO.Pipes;
using System.Runtime.InteropServices;
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
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
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

                using var reader = new StreamReader(pipe, Utf8NoBom, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                using var writer = new StreamWriter(pipe, Utf8NoBom, leaveOpen: true) { AutoFlush = true };

                while (pipe.IsConnected)
                {
                    HookLog.Write("CmdPipe: waiting for command...");
                    string? line = reader.ReadLine();
                    if (line == null) { HookLog.Write("CmdPipe: ReadLine returned null"); break; }

                    HookLog.Write($"CmdPipe: received {line[..Math.Min(line.Length, 80)]}");
                    string response = HandleCommand(line);
                    HookLog.Write($"CmdPipe: responding {response[..Math.Min(response.Length, 80)]}");
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
                "reconnect" => HandleReconnect(),
                "dump_mem" => HandleDumpMem(root),
                "call_sign" => HandleCallSign(root),
                _ => $$$"""{"ok":false,"error":"unknown command: {{{cmd}}}"}"""
            };
        }
        catch (Exception ex)
        {
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
        return $$$"""{"ok":true,"mqttClient":"0x{{{HookEntry.MqttClient:X}}}","hasClient":{{{(HookEntry.MqttClient != 0 ? "true" : "false")}}},"hasInvokeSubscribe":{{{(HookEntry.OrigInvokeSubscribeResult != 0 ? "true" : "false")}}},"messageCount":{{{HookEntry.MessageCount}}},"subscribeCount":{{{HookEntry.SubscribeCount}}},"grpcRequestCount":{{{HookEntry.GrpcRequestCount}}}}""";
    }

    private static string HandleReconnect()
    {
        nint client = HookEntry.MqttClient;
        if (client == 0)
            return """{"ok":false,"error":"no mqtt client"}""";

        nint disconnectFn = HookEntry.OrigDisconnectFromHost;
        nint connectFn = HookEntry.OrigConnectToHost;

        if (disconnectFn == 0 || connectFn == 0)
            return """{"ok":false,"error":"disconnect/connect trampolines not available"}""";

        HookLog.Write($"CmdPipe: reconnecting client 0x{client:X}...");

        var disconnect = (delegate* unmanaged<nint, void>)disconnectFn;
        disconnect(client);

        Thread.Sleep(1000);

        var connect = (delegate* unmanaged<nint, void>)connectFn;
        connect(client);

        HookLog.Write("CmdPipe: reconnect initiated.");
        return """{"ok":true,"msg":"reconnecting"}""";
    }

    /// <summary>
    /// Dump raw memory at a DLL + offset. Example: {"cmd":"dump_mem","dll":"wbgrpc.dll","rva":"0x7A79A0","size":64}
    /// Can also read relative to a function: {"cmd":"dump_mem","dll":"wbgrpc.dll","export":"?getHeadMd5Sign...","offset":0,"size":64}
    /// </summary>
    private static string HandleDumpMem(JsonElement root)
    {
        string dllName = root.GetProperty("dll").GetString() ?? "";
        int size = root.TryGetProperty("size", out var sz) ? sz.GetInt32() : 64;
        if (size > 4096) size = 4096;

        nint module = NativeMethods.GetModuleHandleW(dllName);
        if (module == 0)
            return $$$"""{"ok":false,"error":"module not found: {{{dllName}}}"}""";

        nint addr;
        if (root.TryGetProperty("export", out var exportProp))
        {
            string exportName = exportProp.GetString() ?? "";
            addr = NativeMethods.GetProcAddress(module, exportName);
            if (addr == 0)
                return $$$"""{"ok":false,"error":"export not found: {{{exportName}}}"}""";
            int offset = root.TryGetProperty("offset", out var offProp) ? offProp.GetInt32() : 0;
            addr += offset;
        }
        else
        {
            string rvaStr = root.GetProperty("rva").GetString() ?? "0";
            long rva = Convert.ToInt64(rvaStr, 16);
            addr = module + (nint)rva;
        }

        byte[] data = new byte[size];
        Marshal.Copy(addr, data, 0, size);

        string hex = Convert.ToHexString(data);
        // Also try to read as ASCII
        string ascii = "";
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            if (b == 0) { ascii = Encoding.ASCII.GetString(data, 0, i); break; }
            if (b < 32 || b > 126) { ascii = "(binary)"; break; }
        }

        return $$$"""{"ok":true,"addr":"0x{{{addr:X}}}","hex":"{{{hex}}}","ascii":"{{{ascii}}}"}""";
    }

    /// <summary>
    /// Call getHeadMd5Sign with given headers and return the computed sign.
    /// This directly calls the native function inside wbgrpc.dll.
    /// Example: {"cmd":"call_sign","headers":{"appid":"wb_desktop","ver":"8.19.9",...}}
    /// </summary>
    private static unsafe string HandleCallSign(JsonElement root)
    {
        nint grpcModule = NativeMethods.GetModuleHandleW("wbgrpc.dll");
        if (grpcModule == 0)
            return """{"ok":false,"error":"wbgrpc.dll not loaded"}""";

        // Resolve getHeadMd5Sign
        nint signFn = NativeMethods.GetProcAddress(grpcModule, HookEntry.Fn_getHeadMd5Sign_Mangled);
        if (signFn == 0)
            return """{"ok":false,"error":"getHeadMd5Sign not found"}""";

        // We can't easily construct a QMap and this pointer.
        // Instead, dump the runtime secret from the .data section.
        // The secret is initialized at startup and may differ from the embedded string.
        // RVA 0x7A79A0 in wbgrpc.dll holds a pointer to the runtime config.
        nint secretStaticAddr = grpcModule + 0x7A79A0;
        // Read what's at this .data address
        nint val = *(nint*)secretStaticAddr;
        string secretDump = $"0x{val:X}";

        // Also read the embedded secret at the known location
        nint embeddedAddr = grpcModule + 0x59F708;
        byte[] embeddedBytes = new byte[32];
        Marshal.Copy(embeddedAddr, embeddedBytes, 0, 32);
        int end = Array.IndexOf(embeddedBytes, (byte)0);
        string embedded = end > 0 ? Encoding.ASCII.GetString(embeddedBytes, 0, end) : "(binary)";

        // Try to read the .data pointer as a potential QString or char*
        string runtimeSecret = "(unknown)";
        if (val > 0x10000)
        {
            try
            {
                // Try as ASCII string pointer
                byte[] buf = new byte[64];
                Marshal.Copy(val, buf, 0, 64);
                int asciiEnd = Array.IndexOf(buf, (byte)0);
                if (asciiEnd > 0 && asciiEnd < 64)
                {
                    bool allAscii = true;
                    for (int i = 0; i < asciiEnd; i++)
                        if (buf[i] < 32 || buf[i] > 126) { allAscii = false; break; }
                    if (allAscii)
                        runtimeSecret = Encoding.ASCII.GetString(buf, 0, asciiEnd);
                }
                // If not ASCII, try reading it as a Qt object / other structure
                if (runtimeSecret == "(unknown)")
                    runtimeSecret = $"hex={Convert.ToHexString(buf[..16])}";
            }
            catch { runtimeSecret = "(access error)"; }
        }

        return $$$"""{"ok":true,"signFn":"0x{{{signFn:X}}}","dataPtr":"0x{{{secretStaticAddr:X}}}","dataVal":{{{secretDump}}},"embedded":"{{{embedded}}}","runtimeSecret":"{{{runtimeSecret}}}"}""";
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

        HookLog.Write($"CmdPipe: calling invokeSubscribeResult on client 0x{client:X}...");

        // Build a Qt QString and call invokeSubscribeResult
        using var qstr = QtString.Create(subscriptionJson);

        var invokeSubscribe = (delegate* unmanaged<nint, nint, nint>)origFn;
        nint result = invokeSubscribe(client, qstr.Ptr);

        HookLog.Write($"CmdPipe: invokeSubscribeResult returned {result}");
        return $$$"""{"ok":true,"reasonCode":{{{(long)result}}}}""";
    }
}
