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
    private readonly IServiceScopeFactory _scopeFactory;
    private bool _weStartedWebull;
    private int? _webullPid;

    public WebullHookHostedService(
        ILogger<WebullHookHostedService> logger,
        MqttDataReader mqttDataReader,
        MqttMessageProcessor mqttProcessor,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _mqttDataReader = mqttDataReader;
        _mqttProcessor = mqttProcessor;
        _scopeFactory = scopeFactory;
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

            // Update shared state so dashboard reflects actual status
            WebullHookAppService.IsInjected = true;

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

            // 6. Force MQTT reconnect via firewall reset to capture auth headers
            // The hooks are installed but MQTT connected before hooks were in place,
            // so we temporarily block MQTT ports to force a reconnect through the hooks.
            _ = Task.Run(async () =>
            {
                const string ruleName = "TradingPilot_MQTT_Reset";
                try
                {
                    // Wait for hooks to be fully installed
                    _logger.LogInformation("Firewall reset: waiting 5s for hooks to settle...");
                    await Task.Delay(5000, stoppingToken);

                    // Find active MQTT connections (ports 1883 or 8883)
                    _logger.LogInformation("Firewall reset: scanning for active MQTT connections...");
                    var remoteIps = new HashSet<string>();
                    var netstatInfo = new ProcessStartInfo("netstat", "-an")
                    {
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var netstat = Process.Start(netstatInfo)!)
                    {
                        string output = await netstat.StandardOutput.ReadToEndAsync(stoppingToken);
                        await netstat.WaitForExitAsync(stoppingToken);
                        foreach (string line in output.Split('\n'))
                        {
                            string trimmed = line.Trim();
                            if (!trimmed.StartsWith("TCP", StringComparison.OrdinalIgnoreCase)) continue;
                            if (!trimmed.Contains("ESTABLISHED", StringComparison.OrdinalIgnoreCase)) continue;
                            // Parse: TCP    local_addr:port    remote_addr:port    ESTABLISHED
                            var parts = trimmed.Split([' '], StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length < 3) continue;
                            string remoteEndpoint = parts[2];
                            int lastColon = remoteEndpoint.LastIndexOf(':');
                            if (lastColon < 0) continue;
                            string portStr = remoteEndpoint[(lastColon + 1)..];
                            if (portStr is "1883" or "8883")
                            {
                                string ip = remoteEndpoint[..lastColon];
                                remoteIps.Add(ip);
                                _logger.LogInformation("Firewall reset: found MQTT connection to {IP}:{Port}", ip, portStr);
                            }
                        }
                    }

                    if (remoteIps.Count == 0)
                    {
                        _logger.LogWarning("Firewall reset: no active MQTT connections found. Auth capture may need manual trigger.");
                        return;
                    }

                    string ipList = string.Join(',', remoteIps);
                    _logger.LogInformation("Firewall reset: blocking MQTT to IPs: {IPs}", ipList);

                    // Create temporary firewall rule to block MQTT
                    try
                    {
                        var addRule = new ProcessStartInfo("netsh",
                            $"advfirewall firewall add rule name=\"{ruleName}\" dir=out action=block protocol=TCP remoteip={ipList} remoteport=1883,8883")
                        {
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using (var proc = Process.Start(addRule)!)
                        {
                            await proc.WaitForExitAsync(stoppingToken);
                            string stdout = await proc.StandardOutput.ReadToEndAsync(stoppingToken);
                            _logger.LogInformation("Firewall reset: rule added (exit={Exit}): {Output}",
                                proc.ExitCode, stdout.Trim());
                        }

                        // Wait for connections to drop
                        _logger.LogInformation("Firewall reset: waiting 3s for connections to drop...");
                        await Task.Delay(3000, stoppingToken);
                    }
                    finally
                    {
                        // Always remove the firewall rule
                        var delRule = new ProcessStartInfo("netsh",
                            $"advfirewall firewall delete rule name=\"{ruleName}\"")
                        {
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var delProc = Process.Start(delRule)!;
                        await delProc.WaitForExitAsync(CancellationToken.None);
                        string delOut = await delProc.StandardOutput.ReadToEndAsync(CancellationToken.None);
                        _logger.LogInformation("Firewall reset: rule removed (exit={Exit}): {Output}",
                            delProc.ExitCode, delOut.Trim());
                    }

                    // Wait for Webull to reconnect and re-subscribe through the hooks
                    _logger.LogInformation("Firewall reset: waiting 10s for Webull to reconnect...");
                    await Task.Delay(10000, stoppingToken);

                    // Read auth header from file (written by hook's invokeSubscribeResult)
                    string authFilePath = Path.Combine(@"D:\Third-Parties\WebullHook", "auth_header.json");
                    if (capturedHeader == null && File.Exists(authFilePath))
                    {
                        capturedHeader = File.ReadAllText(authFilePath).Trim();
                        if (!string.IsNullOrEmpty(capturedHeader))
                            _logger.LogInformation("Firewall reset: loaded auth header from file");
                        else
                            capturedHeader = null;
                    }

                    if (capturedHeader != null)
                    {
                        _logger.LogInformation("Firewall reset: auth header available, auto-subscribing...");
                        await AutoSubscribeAsync(capturedHeader, stoppingToken);
                    }
                    else
                    {
                        _logger.LogWarning("Firewall reset: no auth header available. Open a stock in Webull to trigger subscription.");
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Firewall reset failed");
                    // Best-effort cleanup of firewall rule in case of unexpected error
                    try
                    {
                        var cleanup = new ProcessStartInfo("netsh",
                            $"advfirewall firewall delete rule name=\"{ruleName}\"")
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var proc = Process.Start(cleanup);
                        proc?.WaitForExit(5000);
                    }
                    catch { }
                }
            }, stoppingToken);

            // 7. Start reading from hook pipe (C# PipeServer receives data via native callback)
            WebullHookAppService.IsPipeConnected = true;
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
            // Load all watched symbols from DB
            var watchedSymbols = await GetWatchedSymbolsAsync();
            if (watchedSymbols.Count == 0)
            {
                _logger.LogWarning("Auto-subscribe: no watched symbols found in DB");
                return;
            }

            _logger.LogInformation("Auto-subscribe: connecting to command pipe for {Count} symbols...", watchedSymbols.Count);
            using var cmdPipe = new NamedPipeClientStream(".", "WebullMqttHookCmd", PipeDirection.InOut);
            await cmdPipe.ConnectAsync(5000, ct);
            _logger.LogInformation("Auto-subscribe: command pipe connected.");

            using var reader = new StreamReader(cmdPipe, Utf8NoBom, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            using var writer = new StreamWriter(cmdPipe, Utf8NoBom, leaveOpen: true) { AutoFlush = true };

            // Ping first
            await writer.WriteLineAsync("""{"cmd":"ping"}""".AsMemory(), ct);
            string? pong = await reader.ReadLineAsync(ct);
            _logger.LogInformation("Auto-subscribe: ping: {Response}", pong);

            // Subscribe types: 91=L2depth, 92=quote, 100=tick, 102=trade, 104=order, 105=L2depth2
            int[] types = [91, 92, 100, 102, 104, 105];

            foreach (var (ticker, tickerId) in watchedSymbols)
            {
                foreach (int type in types)
                {
                    string flag = type is 91 or 105 ? "1,50,1" : "1";
                    string subJson = $$"""{"flag":"{{flag}}","header":{{headerJson}},"module":"[\"OtherStocks\"]","tickerIds":[{{tickerId}}],"type":"{{type}}"}""";
                    string cmd = JsonSerializer.Serialize(new { cmd = "subscribe", json = subJson });
                    await writer.WriteLineAsync(cmd.AsMemory(), ct);
                    string? resp = await reader.ReadLineAsync(ct);
                }
                _logger.LogInformation("Auto-subscribe: {Ticker} (tickerId={TickerId}) — all 6 types", ticker, tickerId);
            }

            _logger.LogInformation("Auto-subscribe complete for {Count} symbols ({Names}).",
                watchedSymbols.Count, string.Join(", ", watchedSymbols.Select(s => s.Ticker)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-subscribe failed");
        }
    }

    private async Task<List<(string Ticker, long TickerId)>> GetWatchedSymbolsAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TradingPilot.EntityFrameworkCore.TradingPilotDbContext>();
            var symbols = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
                dbContext.Symbols.Where(s => s.IsWatched).OrderBy(s => s.Id));
            return symbols.Select(s => (s.Id, s.WebullTickerId)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load watched symbols for auto-subscribe");
            return [];
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
                // Inject immediately — wbmqtt.dll must be loaded for hooks to install.
                // If it's not loaded yet, the hook will retry internally.
                _logger.LogInformation("Webull Desktop started (PID {Pid}). Injecting immediately...", proc.Id);
                await Task.Delay(1000, ct);
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
