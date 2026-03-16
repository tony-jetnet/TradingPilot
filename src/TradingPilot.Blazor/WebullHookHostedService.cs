using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingPilot.Webull;

namespace TradingPilot.Blazor;

/// <summary>
/// Background service that auto-launches Webull Desktop and injects the hook on app startup.
/// </summary>
public partial class WebullHookHostedService : BackgroundService
{
    private readonly ILogger<WebullHookHostedService> _logger;
    private readonly MqttDataReader _mqttDataReader;
    private readonly MqttMessageProcessor _mqttProcessor;
    private bool _weStartedWebull;
    private int? _webullPid;

    public WebullHookHostedService(
        ILogger<WebullHookHostedService> logger,
        MqttDataReader mqttDataReader,
        MqttMessageProcessor mqttProcessor)
    {
        _logger = logger;
        _mqttDataReader = mqttDataReader;
        _mqttProcessor = mqttProcessor;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the web server a moment to start
        await Task.Delay(1000, stoppingToken);

        try
        {
            // 1. Find or launch Webull
            var webull = FindWebull();
            if (webull == null)
            {
                _logger.LogInformation("Webull Desktop not running. Launching...");
                webull = await LaunchWebullAsync(stoppingToken);
                _weStartedWebull = webull != null;
            }

            if (webull == null)
            {
                _logger.LogWarning("Could not find or launch Webull Desktop. Hook not injected.");
                return;
            }

            _webullPid = webull.Id;
            _logger.LogInformation("Webull Desktop found: PID {Pid}", webull.Id);

            // 2. Check if already injected
            if (IsAlreadyInjected(webull))
            {
                _logger.LogInformation("Hook DLL already loaded in Webull. Skipping injection.");
            }
            else
            {
                // 3. Find hook DLL
                string? dllPath = FindHookDll();
                if (dllPath == null)
                {
                    _logger.LogError("Hook DLL not found. Run: dotnet publish src/TradingPilot.Webull.Hook -c Release");
                    return;
                }

                // 4. Inject + Initialize
                _logger.LogInformation("Injecting hook DLL: {Path}", dllPath);
                InjectAndInitialize(webull.Id, dllPath);
                _logger.LogInformation("Hook injected and initialized.");

                // Wait for hook to start pipe servers
                await Task.Delay(3000, stoppingToken);
            }

            // 5. Start reading data pipe + auto-subscribe
            _logger.LogInformation("Connecting to hook data pipe...");
            string? capturedHeader = null;
            int msgCount = 0;

            _mqttDataReader.MessageReceived += (topic, payload) =>
            {
                msgCount++;
                if (msgCount <= 5 || msgCount % 100 == 0)
                    _logger.LogInformation("MQTT #{Count}: topic={Topic} ({Bytes} bytes)", msgCount, topic, payload.Length);

                // Process message asynchronously (fire-and-forget with error logging)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _mqttProcessor.ProcessMessageAsync(topic, payload);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "MQTT message processing failed for topic {Topic}", topic);
                    }
                });
            };

            _mqttDataReader.EventReceived += (eventType, data) =>
            {
                string text = Encoding.UTF8.GetString(data);
                _logger.LogInformation("Hook event: {EventType} {Data}",
                    eventType, text.Length > 120 ? text[..120] + "..." : text);

                // Capture auth header from first subscription event, then auto-subscribe
                if (eventType == "subscribe" && capturedHeader == null)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(text);
                        if (doc.RootElement.TryGetProperty("header", out var header))
                        {
                            capturedHeader = header.ToString();
                            _logger.LogInformation("Auth header captured! Waiting 3s for QMqttClient to be captured, then auto-subscribing...");
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(3000, stoppingToken);
                                await AutoSubscribeAsync(capturedHeader, stoppingToken);
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Failed to parse subscription event: {Message}", ex.Message);
                    }
                }
            };

            // 6. Trigger MQTT reconnect to force Webull to re-subscribe (captures auth headers)
            // Polls until MQTT client is captured, then sends reconnect
            _ = Task.Run(async () =>
            {
                try
                {
                    // Wait for Webull to establish MQTT connection (hook captures client pointer)
                    for (int i = 0; i < 60; i++)
                    {
                        await Task.Delay(5000, stoppingToken);
                        bool hasClient;
                        using (var statusCmd = new MqttCommandWriter())
                        {
                            await statusCmd.ConnectAsync(5000, stoppingToken);
                            using var status = await statusCmd.GetStatusAsync(stoppingToken);
                            hasClient = status.RootElement.TryGetProperty("hasClient", out var hc) && hc.ValueKind == System.Text.Json.JsonValueKind.True;
                        }
                        if (hasClient)
                        {
                            _logger.LogInformation("MQTT client captured. Sending reconnect to force auth capture...");
                            using var cmd = new MqttCommandWriter();
                            await cmd.ConnectAsync(5000, stoppingToken);
                            using var result = await cmd.ReconnectAsync(stoppingToken);
                            _logger.LogInformation("Reconnect response: {Response}", result.RootElement.ToString());
                            return;
                        }
                        if (i % 6 == 0)
                            _logger.LogDebug("Waiting for MQTT client to be captured ({Attempt}/60)...", i + 1);
                    }
                    _logger.LogWarning("MQTT client was never captured after 5 minutes. Auth may need manual trigger.");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogWarning("Reconnect command failed: {Message}", ex.Message);
                }
            }, stoppingToken);

            await _mqttDataReader.StartAsync(stoppingToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WebullHookHostedService failed");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        if (_weStartedWebull && _webullPid is { } pid)
        {
            try
            {
                var proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    _logger.LogInformation("Closing Webull Desktop (PID {Pid})...", pid);
                    proc.CloseMainWindow();

                    if (!proc.WaitForExit(5000))
                    {
                        _logger.LogWarning("Webull did not close gracefully, killing...");
                        proc.Kill();
                    }

                    _logger.LogInformation("Webull Desktop closed.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to close Webull: {Message}", ex.Message);
            }
        }
    }

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private async Task AutoSubscribeAsync(string headerJson, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Auto-subscribe: connecting to command pipe...");
            using var cmdPipe = new NamedPipeClientStream(".", "WebullMqttHookCmd", PipeDirection.InOut);
            await cmdPipe.ConnectAsync(5000, ct);
            _logger.LogInformation("Auto-subscribe: command pipe connected.");

            using var reader = new StreamReader(cmdPipe, Utf8NoBom, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            using var writer = new StreamWriter(cmdPipe, Utf8NoBom, leaveOpen: true) { AutoFlush = true };

            // Ping first to verify pipe works
            _logger.LogInformation("Auto-subscribe: sending ping...");
            await writer.WriteLineAsync("""{"cmd":"ping"}""".AsMemory(), ct);
            string? pong = await reader.ReadLineAsync(ct);
            _logger.LogInformation("Auto-subscribe: ping response: {Response}", pong);

            // Ticker IDs: AMD = 913254235, RKLB = 950178054
            long[] tickers = [913254235, 950178054];
            string[] tickerNames = ["AMD", "RKLB"];
            int[] types = [91, 92, 100, 102, 104, 105];

            for (int t = 0; t < tickers.Length; t++)
            {
                foreach (int type in types)
                {
                    string flag = type is 91 or 105 ? "1,50,1" : "1";
                    string subJson = $$"""{"flag":"{{flag}}","header":{{headerJson}},"module":"[\"OtherStocks\"]","tickerIds":[{{tickers[t]}}],"type":"{{type}}"}""";
                    string cmd = JsonSerializer.Serialize(new { cmd = "subscribe", json = subJson });
                    _logger.LogInformation("Auto-subscribe: sending {Ticker} type={Type}...", tickerNames[t], type);
                    await writer.WriteLineAsync(cmd.AsMemory(), ct);
                    string? resp = await reader.ReadLineAsync(ct);
                    _logger.LogInformation("Subscribe {Ticker} type={Type}: {Response}", tickerNames[t], type, resp);
                }
            }

            _logger.LogInformation("Auto-subscribe complete for AMD + RKLB (all 6 types each).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-subscribe failed");
        }
    }

    private static Process? FindWebull()
    {
        return Process.GetProcessesByName("Webull Desktop")
            .OrderByDescending(p => p.WorkingSet64)
            .FirstOrDefault();
    }

    private async Task<Process?> LaunchWebullAsync(CancellationToken ct)
    {
        string[] paths =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Webull Desktop", "Webull Desktop.exe"),
            @"C:\Program Files\Webull Desktop\Webull Desktop.exe",
            @"C:\Program Files (x86)\Webull Desktop\Webull Desktop.exe",
        ];

        string? exe = paths.FirstOrDefault(File.Exists);
        if (exe == null)
        {
            _logger.LogWarning("Webull Desktop executable not found in common locations.");
            return null;
        }

        _logger.LogInformation("Starting Webull Desktop from: {Path}", exe);
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });

        // Wait for process to appear
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(1000, ct);
            var proc = FindWebull();
            if (proc != null)
            {
                // Extra time for full load
                _logger.LogInformation("Webull Desktop started (PID {Pid}). Waiting for full load...", proc.Id);
                await Task.Delay(10000, ct);
                return proc;
            }
        }

        return null;
    }

    private static bool IsAlreadyInjected(Process webull)
    {
        try
        {
            foreach (ProcessModule mod in webull.Modules)
            {
                if (mod.ModuleName?.Contains("TradingPilot.Webull.Hook", StringComparison.OrdinalIgnoreCase) == true)
                    return true;
            }
        }
        catch { }
        return false;
    }

    private static string? FindHookDll()
    {
        string[] paths =
        [
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TradingPilot.Webull.Hook",
                "bin", "Release", "net10.0", "win-x64", "publish", "TradingPilot.Webull.Hook.dll")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src",
                "TradingPilot.Webull.Hook", "bin", "Release", "net10.0", "win-x64", "publish",
                "TradingPilot.Webull.Hook.dll")),
            @"D:\Third-Parties\TradingPilot\src\TradingPilot.Webull.Hook\bin\Release\net10.0\win-x64\publish\TradingPilot.Webull.Hook.dll",
        ];
        return paths.FirstOrDefault(File.Exists);
    }

    // ═══════════════════════════════════════════════════════════════════
    // DLL Injection + Initialize call
    // ═══════════════════════════════════════════════════════════════════

    private void InjectAndInitialize(int processId, string dllPath)
    {
        string fullPath = Path.GetFullPath(dllPath);

        nint kernel32 = GetModuleHandle("kernel32.dll");
        nint loadLibraryAddr = GetProcAddress(kernel32, "LoadLibraryW");
        if (loadLibraryAddr == 0)
            throw new InvalidOperationException("Could not find LoadLibraryW.");

        nint hProcess = OpenProcess(0x1F0FFF, false, processId);
        if (hProcess == 0)
            throw new InvalidOperationException($"OpenProcess failed ({Marshal.GetLastPInvokeError()}). Run as Administrator.");

        try
        {
            // Step 1: LoadLibraryW
            byte[] pathBytes = Encoding.Unicode.GetBytes(fullPath + '\0');
            nint remoteMem = VirtualAllocEx(hProcess, 0, (nuint)pathBytes.Length, 0x3000, 0x04);
            if (remoteMem == 0)
                throw new InvalidOperationException($"VirtualAllocEx failed: {Marshal.GetLastPInvokeError()}");

            try
            {
                if (!WriteProcessMemory(hProcess, remoteMem, pathBytes, (nuint)pathBytes.Length, out _))
                    throw new InvalidOperationException($"WriteProcessMemory failed: {Marshal.GetLastPInvokeError()}");

                nint hThread = CreateRemoteThread(hProcess, 0, 0, loadLibraryAddr, remoteMem, 0, out _);
                if (hThread == 0)
                    throw new InvalidOperationException($"CreateRemoteThread failed: {Marshal.GetLastPInvokeError()}");

                WaitForSingleObject(hThread, 10000);
                GetExitCodeThread(hThread, out uint exitCode);
                CloseHandle(hThread);

                if (exitCode == 0)
                    throw new InvalidOperationException("LoadLibraryW returned NULL. Check hook.log.");

                _logger.LogInformation("DLL loaded (handle low32: 0x{Handle:X})", exitCode);
            }
            finally
            {
                VirtualFreeEx(hProcess, remoteMem, 0, 0x8000);
            }

            // Step 2: Call Initialize export
            nint localModule = LoadLibraryEx(fullPath, 0, 0x01); // DONT_RESOLVE_DLL_REFERENCES
            if (localModule == 0)
                throw new InvalidOperationException($"Local LoadLibraryEx failed: {Marshal.GetLastPInvokeError()}");

            try
            {
                nint localInitAddr = GetProcAddress(localModule, "Initialize");
                if (localInitAddr == 0)
                    throw new InvalidOperationException("'Initialize' export not found in DLL.");

                long offset = localInitAddr - localModule;

                nint remoteBase = FindRemoteModuleBase(processId);
                if (remoteBase == 0)
                    throw new InvalidOperationException("Could not find injected DLL in remote process.");

                nint remoteInitAddr = remoteBase + (nint)offset;
                _logger.LogInformation("Calling Initialize at remote 0x{Addr:X} (offset 0x{Offset:X})", remoteInitAddr, offset);

                nint hThread2 = CreateRemoteThread(hProcess, 0, 0, remoteInitAddr, 0, 0, out _);
                if (hThread2 == 0)
                    throw new InvalidOperationException($"CreateRemoteThread for Initialize failed: {Marshal.GetLastPInvokeError()}");

                WaitForSingleObject(hThread2, 10000);
                GetExitCodeThread(hThread2, out uint initResult);
                CloseHandle(hThread2);
                _logger.LogInformation("Initialize returned {Result}", initResult);
            }
            finally
            {
                FreeLibrary(localModule);
            }
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    private static nint FindRemoteModuleBase(int processId)
    {
        try
        {
            var proc = Process.GetProcessById(processId);
            foreach (ProcessModule mod in proc.Modules)
            {
                if (mod.ModuleName?.Equals("TradingPilot.Webull.Hook.dll", StringComparison.OrdinalIgnoreCase) == true)
                    return mod.BaseAddress;
            }
        }
        catch { }
        return 0;
    }

    // P/Invoke
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint VirtualAllocEx(nint hProcess, nint lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteProcessMemory(nint hProcess, nint lpBaseAddress, byte[] lpBuffer, nuint nSize, out nuint lpNumberOfBytesWritten);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateRemoteThread(nint hProcess, nint lpThreadAttributes, nuint dwStackSize, nint lpStartAddress, nint lpParameter, uint dwCreationFlags, out uint lpThreadId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeThread(nint hThread, out uint lpExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualFreeEx(nint hProcess, nint lpAddress, nuint dwSize, uint dwFreeType);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandle(string lpModuleName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcAddress", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetProcAddress(nint hModule, string lpProcName);

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadLibraryEx(string lpLibFileName, nint hFile, uint dwFlags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeLibrary(nint hLibModule);
}
