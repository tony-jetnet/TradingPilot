using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TradingPilot.Webull.Hook.E2ETest;

// Check for mode flags
if (args.Contains("--mqtt"))
    return await MqttDirectTest.RunAsync();
if (args.Contains("--verify"))
    return await VerifyHooks.RunAsync();
if (args.Contains("--live"))
    return await RunLiveMode(args);

Console.WriteLine("=== WebullHook End-to-End Test ===");
Console.WriteLine();

// ── Step 1: Find Webull Desktop ──────────────────────────────────────
Console.Write("Finding Webull Desktop... ");
var webull = Process.GetProcessesByName("Webull Desktop")
    .OrderByDescending(p => p.WorkingSet64)
    .FirstOrDefault();

if (webull == null)
{
    Console.WriteLine("FAILED - Webull Desktop is not running.");
    return 1;
}
Console.WriteLine($"OK (PID: {webull.Id}, Memory: {webull.WorkingSet64 / 1024 / 1024} MB)");

// ── Step 2: Locate NativeAOT DLL ────────────────────────────────────
Console.Write("Locating hook DLL... ");

// Try multiple search paths
string[] searchPaths =
[
    // From solution root (when running via dotnet run from repo root)
    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "src",
        "TradingPilot.Webull.Hook", "bin", "Release", "net10.0", "win-x64", "publish",
        "TradingPilot.Webull.Hook.dll")),
    // Absolute fallback
    @"D:\Third-Parties\TradingPilot\src\TradingPilot.Webull.Hook\bin\Release\net10.0\win-x64\publish\TradingPilot.Webull.Hook.dll",
];
string dllPath = searchPaths.FirstOrDefault(File.Exists) ?? searchPaths[^1];

if (!File.Exists(dllPath))
{
    Console.WriteLine($"FAILED - Not found at: {dllPath}");
    return 1;
}
Console.WriteLine($"OK ({new FileInfo(dllPath).Length / 1024} KB)");
Console.WriteLine($"  Path: {dllPath}");

// ── Step 3: Check if already injected ────────────────────────────────
Console.Write("Checking if already injected... ");
bool alreadyInjected = false;
try
{
    foreach (ProcessModule mod in webull.Modules)
    {
        if (mod.ModuleName?.Contains("TradingPilot.Webull.Hook", StringComparison.OrdinalIgnoreCase) == true)
        {
            alreadyInjected = true;
            break;
        }
    }
}
catch { /* access denied - assume not injected */ }

if (alreadyInjected)
{
    Console.WriteLine("YES (already loaded, skipping injection)");
}
else
{
    Console.WriteLine("NO");

    // ── Step 4: Inject DLL ───────────────────────────────────────────
    Console.Write("Injecting DLL into Webull... ");
    try
    {
        InjectDll(webull.Id, dllPath);
        Console.WriteLine("OK");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAILED - {ex.Message}");
        return 1;
    }

    // Give the hook time to initialize and start the pipe server
    Console.Write("Waiting for hook to initialize (2s)... ");
    Thread.Sleep(2000);
    Console.WriteLine("OK");
}

// ── Step 5: Check hook log ───────────────────────────────────────────
string logPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "WebullHook", "hook.log");
Console.Write("Checking hook log... ");
if (File.Exists(logPath))
{
    string[] lines = File.ReadAllLines(logPath);
    Console.WriteLine($"OK ({lines.Length} lines)");
    // Show last 10 lines
    foreach (var line in lines.TakeLast(10))
        Console.WriteLine($"  {line}");
}
else
{
    Console.WriteLine("NOT FOUND (hook may not have started yet)");
}
Console.WriteLine();

// ── Step 6: Connect to named pipe and read messages ──────────────────
Console.WriteLine("Connecting to named pipe 'WebullMqttHook'...");
Console.WriteLine("Press Ctrl+C to stop.");
Console.WriteLine(new string('─', 80));

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

int messageCount = 0;
var startTime = DateTime.UtcNow;

while (!cts.Token.IsCancellationRequested)
{
    try
    {
        using var pipe = new NamedPipeClientStream(".", "WebullMqttHook", PipeDirection.In);
        Console.Write("  Connecting... ");
        await pipe.ConnectAsync(5000, cts.Token);
        Console.WriteLine("CONNECTED!");
        Console.WriteLine();

        byte[] typeBuf = new byte[1];
        byte[] lenBuf = new byte[4];

        while (!cts.Token.IsCancellationRequested && pipe.IsConnected)
        {
            // Read message type
            if (!await ReadExactAsync(pipe, typeBuf, cts.Token)) break;
            byte msgType = typeBuf[0];

            // Read name (topic or event type)
            if (!await ReadExactAsync(pipe, lenBuf, cts.Token)) break;
            int nameLen = BitConverter.ToInt32(lenBuf);
            if (nameLen <= 0 || nameLen > 1024 * 1024) { Console.WriteLine("  [BAD NAME LENGTH]"); break; }

            byte[] nameBuf = new byte[nameLen];
            if (!await ReadExactAsync(pipe, nameBuf, cts.Token)) break;
            string name = Encoding.UTF8.GetString(nameBuf);

            // Read payload
            if (!await ReadExactAsync(pipe, lenBuf, cts.Token)) break;
            int payloadLen = BitConverter.ToInt32(lenBuf);
            if (payloadLen < 0 || payloadLen > 10 * 1024 * 1024) { Console.WriteLine("  [BAD PAYLOAD LENGTH]"); break; }

            byte[] payload = new byte[payloadLen];
            if (payloadLen > 0 && !await ReadExactAsync(pipe, payload, cts.Token)) break;

            messageCount++;
            string? text = TryDecodeUtf8(payload);

            if (msgType == 0x01)
            {
                // Hook event
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.Write($"[EVENT] ");
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write($"{name} ");
                Console.ResetColor();
                if (text != null)
                    Console.Write(text.Length > 120 ? text[..120] + "..." : text);
                Console.WriteLine();
            }
            else
            {
                // MQTT message
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write($"[{messageCount:D4}] ");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write($"{name} ");
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.Write($"({payloadLen} bytes) ");
                Console.ResetColor();
                Console.WriteLine();

                if (text != null)
                {
                    string display = text.Length > 200 ? text[..200] + "..." : text;
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"       {display}");
                    Console.ResetColor();
                }
                else if (payloadLen > 0)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"       [hex] {Convert.ToHexString(payload.AsSpan(0, Math.Min(payloadLen, 100)))}");
                    Console.ResetColor();
                }
            }
        }

        Console.WriteLine("  Pipe disconnected. Reconnecting...");
    }
    catch (OperationCanceledException)
    {
        break;
    }
    catch (TimeoutException)
    {
        Console.WriteLine("  Timeout. Retrying...");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Error: {ex.Message}. Retrying in 2s...");
        await Task.Delay(2000, cts.Token);
    }
}

Console.WriteLine();
Console.WriteLine(new string('─', 80));
Console.WriteLine($"Total messages received: {messageCount}");
Console.WriteLine($"Duration: {DateTime.UtcNow - startTime:mm\\:ss}");
return 0;

// ═══════════════════════════════════════════════════════════════════════
// --live mode: inject, subscribe, capture all data types
// ═══════════════════════════════════════════════════════════════════════

static async Task<int> RunLiveMode(string[] args)
{
    Console.WriteLine("=== WebullHook LIVE Mode ===");
    Console.WriteLine("Inject → Subscribe → Capture all data types");
    Console.WriteLine();

    // ── 1. Find or launch Webull ──────────────────────────────────────
    Console.Write("Finding Webull Desktop... ");
    var webull = Process.GetProcessesByName("Webull Desktop")
        .OrderByDescending(p => p.WorkingSet64)
        .FirstOrDefault();

    if (webull == null)
    {
        Console.WriteLine("NOT RUNNING");
        Console.Write("Launching Webull Desktop... ");
        try
        {
            // Try common install locations
            string[] webullPaths =
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Webull Desktop", "Webull Desktop.exe"),
                @"C:\Program Files\Webull Desktop\Webull Desktop.exe",
                @"C:\Program Files (x86)\Webull Desktop\Webull Desktop.exe",
            ];
            string? webullExe = webullPaths.FirstOrDefault(File.Exists);
            if (webullExe == null)
            {
                Console.WriteLine("FAILED - Webull Desktop not found. Install it or start it manually.");
                return 1;
            }
            Process.Start(new ProcessStartInfo(webullExe) { UseShellExecute = true });
            Console.WriteLine("OK - waiting for process...");

            // Wait for it to start
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(1000);
                webull = Process.GetProcessesByName("Webull Desktop")
                    .OrderByDescending(p => p.WorkingSet64)
                    .FirstOrDefault();
                if (webull != null) break;
            }
            if (webull == null)
            {
                Console.WriteLine("FAILED - Webull did not start within 30 seconds.");
                return 1;
            }
            // Give it extra time to fully load
            Console.Write("Waiting for Webull to fully load (10s)... ");
            await Task.Delay(10000);
            Console.WriteLine("OK");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED - {ex.Message}");
            return 1;
        }
    }
    Console.WriteLine($"PID: {webull!.Id}, Memory: {webull.WorkingSet64 / 1024 / 1024} MB");

    // ── 2. Inject if needed ───────────────────────────────────────────
    string[] searchPaths =
    [
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "src",
            "TradingPilot.Webull.Hook", "bin", "Release", "net10.0", "win-x64", "publish",
            "TradingPilot.Webull.Hook.dll")),
        @"D:\Third-Parties\TradingPilot\src\TradingPilot.Webull.Hook\bin\Release\net10.0\win-x64\publish\TradingPilot.Webull.Hook.dll",
    ];
    string dllPath = searchPaths.FirstOrDefault(File.Exists) ?? searchPaths[^1];
    if (!File.Exists(dllPath))
    {
        Console.WriteLine($"Hook DLL not found at: {dllPath}");
        return 1;
    }

    bool alreadyInjected = false;
    try
    {
        foreach (ProcessModule mod in webull.Modules)
        {
            if (mod.ModuleName?.Contains("TradingPilot.Webull.Hook", StringComparison.OrdinalIgnoreCase) == true)
            { alreadyInjected = true; break; }
        }
    }
    catch { }

    if (!alreadyInjected)
    {
        Console.Write("Injecting... ");
        InjectDll(webull.Id, dllPath);
        Console.WriteLine("OK");
        Console.Write("Waiting for hook init (3s)... ");
        await Task.Delay(3000);
        Console.WriteLine("OK");
    }
    else
    {
        Console.WriteLine("Already injected.");
    }

    // ── 3. Setup logging ──────────────────────────────────────────────
    string logDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WebullHook", "captures");
    Directory.CreateDirectory(logDir);
    string logFile = Path.Combine(logDir, $"live_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
    Console.WriteLine($"Logging raw payloads to: {logFile}");
    Console.WriteLine();

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    // ── 4. Connect command pipe → ping → get status ───────────────────
    Console.Write("Connecting command pipe... ");
    NamedPipeClientStream? cmdPipe = null;
    StreamReader? cmdReader = null;
    StreamWriter? cmdWriter = null;
    try
    {
        cmdPipe = new NamedPipeClientStream(".", "WebullMqttHookCmd", PipeDirection.InOut);
        await cmdPipe.ConnectAsync(5000, cts.Token);
        cmdReader = new StreamReader(cmdPipe, Encoding.UTF8, leaveOpen: true);
        cmdWriter = new StreamWriter(cmdPipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
        Console.WriteLine("OK");

        // Ping
        await cmdWriter.WriteLineAsync("""{"cmd":"ping"}""");
        string? pong = await cmdReader.ReadLineAsync(cts.Token);
        Console.WriteLine($"  Ping: {pong}");

        // Status
        await cmdWriter.WriteLineAsync("""{"cmd":"status"}""");
        string? status = await cmdReader.ReadLineAsync(cts.Token);
        Console.WriteLine($"  Status: {status}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAILED - {ex.Message}");
        Console.WriteLine("  Command pipe not available. Will still capture data.");
    }

    // ── 5. Wait for auth header capture ───────────────────────────────
    string? capturedHeader = null;
    int msgCount = 0;
    int eventCount = 0;
    var startTime = DateTime.UtcNow;
    using var logStream = new StreamWriter(logFile, append: true, Encoding.UTF8) { AutoFlush = true };

    Console.WriteLine();
    Console.WriteLine("Connecting data pipe... subscribing after auth header captured.");
    Console.WriteLine("(Open/click any stock in Webull to generate a subscription event)");
    Console.WriteLine(new string('─', 80));

    while (!cts.Token.IsCancellationRequested)
    {
        try
        {
            using var dataPipe = new NamedPipeClientStream(".", "WebullMqttHook", PipeDirection.In);
            await dataPipe.ConnectAsync(5000, cts.Token);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("DATA PIPE CONNECTED");
            Console.ResetColor();

            byte[] typeBuf = new byte[1];
            byte[] lenBuf = new byte[4];

            while (!cts.Token.IsCancellationRequested && dataPipe.IsConnected)
            {
                if (!await ReadExactAsync(dataPipe, typeBuf, cts.Token)) break;
                byte msgType = typeBuf[0];

                if (!await ReadExactAsync(dataPipe, lenBuf, cts.Token)) break;
                int nameLen = BitConverter.ToInt32(lenBuf);
                if (nameLen <= 0 || nameLen > 1024 * 1024) break;

                byte[] nameBuf = new byte[nameLen];
                if (!await ReadExactAsync(dataPipe, nameBuf, cts.Token)) break;
                string name = Encoding.UTF8.GetString(nameBuf);

                if (!await ReadExactAsync(dataPipe, lenBuf, cts.Token)) break;
                int payloadLen = BitConverter.ToInt32(lenBuf);
                if (payloadLen < 0 || payloadLen > 10 * 1024 * 1024) break;

                byte[] payload = new byte[payloadLen];
                if (payloadLen > 0 && !await ReadExactAsync(dataPipe, payload, cts.Token)) break;

                string? text = TryDecodeUtf8(payload);
                string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");

                // Log raw to file
                logStream.WriteLine(JsonSerializer.Serialize(new
                {
                    ts = DateTime.UtcNow,
                    type = msgType == 0x01 ? "event" : "mqtt",
                    name,
                    payloadLen,
                    text,
                    hex = text == null && payloadLen > 0 ? Convert.ToHexString(payload) : null
                }));

                if (msgType == 0x01)
                {
                    // Hook event
                    eventCount++;
                    Console.ForegroundColor = ConsoleColor.Magenta;
                    Console.Write($"[{timestamp}] EVENT ");
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.Write($"{name} ");
                    Console.ResetColor();

                    if (name == "subscribe" && text != null)
                    {
                        Console.WriteLine(text.Length > 100 ? text[..100] + "..." : text);

                        // Try to capture auth header
                        if (capturedHeader == null)
                        {
                            capturedHeader = WebullProtocol.TryExtractHeader(text);
                            if (capturedHeader != null)
                            {
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine("  >>> AUTH HEADER CAPTURED! Sending subscriptions...");
                                Console.ResetColor();

                                // Send subscription commands for target tickers
                                if (cmdWriter != null && cmdPipe?.IsConnected == true)
                                {
                                    long[] tickers = [WebullProtocol.Tickers.RKLB, WebullProtocol.Tickers.AMD];
                                    foreach (long tickerId in tickers)
                                    {
                                        foreach (int subType in WebullProtocol.AllTypes)
                                        {
                                            string subJson = WebullProtocol.BuildSubscription(capturedHeader, tickerId, subType);
                                            string cmd = JsonSerializer.Serialize(new { cmd = "subscribe", json = subJson });
                                            await cmdWriter.WriteLineAsync(cmd.AsMemory(), cts.Token);
                                            string? resp = await cmdReader!.ReadLineAsync(cts.Token);
                                            Console.ForegroundColor = WebullProtocol.GetTypeColor(subType);
                                            Console.WriteLine($"  Subscribe ticker={tickerId} type={subType}: {resp}");
                                            Console.ResetColor();
                                        }
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine(text ?? $"({payloadLen} bytes)");
                    }
                }
                else
                {
                    // MQTT message
                    msgCount++;
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.Write($"[{timestamp}] ");
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Write($"#{msgCount:D4} {name} ");
                    Console.ForegroundColor = ConsoleColor.Gray;
                    Console.Write($"({payloadLen}b) ");
                    Console.ResetColor();

                    if (text != null)
                    {
                        string display = text.Length > 150 ? text[..150] + "..." : text;
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine(display);
                        Console.ResetColor();
                    }
                    else if (payloadLen > 0)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine($"[hex] {Convert.ToHexString(payload.AsSpan(0, Math.Min(payloadLen, 80)))}");
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.WriteLine();
                    }
                }
            }
            Console.WriteLine("  Data pipe disconnected. Reconnecting...");
        }
        catch (OperationCanceledException) { break; }
        catch (TimeoutException) { Console.WriteLine("  Timeout. Retrying..."); }
        catch (Exception ex)
        {
            Console.WriteLine($"  Error: {ex.Message}. Retrying in 2s...");
            await Task.Delay(2000, cts.Token);
        }
    }

    // Cleanup
    cmdReader?.Dispose();
    cmdWriter?.Dispose();
    cmdPipe?.Dispose();

    Console.WriteLine();
    Console.WriteLine(new string('─', 80));
    Console.WriteLine($"MQTT messages: {msgCount}, Events: {eventCount}");
    Console.WriteLine($"Duration: {DateTime.UtcNow - startTime:mm\\:ss}");
    Console.WriteLine($"Raw log: {logFile}");
    return 0;
}

// ═══════════════════════════════════════════════════════════════════════
// Helper methods
// ═══════════════════════════════════════════════════════════════════════

static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
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

static string? TryDecodeUtf8(byte[] data)
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
    catch { return null; }
}

// ═══════════════════════════════════════════════════════════════════════
// DLL Injection via CreateRemoteThread + LoadLibraryW
// ═══════════════════════════════════════════════════════════════════════

static void InjectDll(int processId, string dllPath)
{
    string fullPath = Path.GetFullPath(dllPath);

    nint kernel32 = GetModuleHandle("kernel32.dll");
    nint loadLibraryAddr = GetProcAddress(kernel32, "LoadLibraryW");
    if (loadLibraryAddr == 0)
        throw new InvalidOperationException("Could not find LoadLibraryW.");

    nint hProcess = OpenProcess(0x1F0FFF, false, processId);
    if (hProcess == 0)
        throw new InvalidOperationException($"OpenProcess failed (error {Marshal.GetLastPInvokeError()}). Run as Administrator.");

    try
    {
        // Step 1: Inject the DLL via LoadLibraryW
        byte[] pathBytes = Encoding.Unicode.GetBytes(fullPath + '\0');
        nint remoteMem = VirtualAllocEx(hProcess, 0, (nuint)pathBytes.Length, 0x3000, 0x04);
        if (remoteMem == 0)
            throw new InvalidOperationException($"VirtualAllocEx failed: {Marshal.GetLastPInvokeError()}");

        nint moduleHandle;
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
                throw new InvalidOperationException("LoadLibraryW returned NULL — DLL failed to load. Check hook.log.");

            // exitCode is the low 32 bits of the module handle
            moduleHandle = (nint)exitCode;
        }
        finally
        {
            VirtualFreeEx(hProcess, remoteMem, 0, 0x8000);
        }

        // Step 2: Call the exported Initialize function via GetProcAddress + CreateRemoteThread
        Console.Write("\n  Resolving Initialize export... ");
        nint localModule = LoadLibraryEx(fullPath, 0, 0x01); // DONT_RESOLVE_DLL_REFERENCES
        if (localModule == 0)
            throw new InvalidOperationException($"Local LoadLibraryEx failed: {Marshal.GetLastPInvokeError()}");

        try
        {
            nint localInitAddr = GetProcAddress(localModule, "Initialize");
            if (localInitAddr == 0)
                throw new InvalidOperationException("Could not find 'Initialize' export in DLL.");

            long offset = localInitAddr - localModule;
            nint remoteBase = FindRemoteModuleBase(processId, "TradingPilot.Webull.Hook.dll");
            if (remoteBase == 0)
                throw new InvalidOperationException("Could not find injected DLL in remote process modules.");

            nint remoteInitAddr = remoteBase + (nint)offset;
            Console.WriteLine($"OK (offset: 0x{offset:X}, remote: 0x{remoteInitAddr:X})");

            Console.Write("  Calling Initialize... ");
            nint hThread2 = CreateRemoteThread(hProcess, 0, 0, remoteInitAddr, 0, 0, out _);
            if (hThread2 == 0)
                throw new InvalidOperationException($"CreateRemoteThread for Initialize failed: {Marshal.GetLastPInvokeError()}");

            WaitForSingleObject(hThread2, 10000);
            GetExitCodeThread(hThread2, out uint initResult);
            CloseHandle(hThread2);
            Console.WriteLine($"OK (returned {initResult})");
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

static nint FindRemoteModuleBase(int processId, string moduleName)
{
    try
    {
        var proc = Process.GetProcessById(processId);
        foreach (ProcessModule mod in proc.Modules)
        {
            if (mod.ModuleName?.Equals(moduleName, StringComparison.OrdinalIgnoreCase) == true)
                return mod.BaseAddress;
        }
    }
    catch { }
    return 0;
}

// P/Invoke declarations
[DllImport("kernel32.dll", SetLastError = true)]
static extern nint OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

[DllImport("kernel32.dll", SetLastError = true)]
static extern nint VirtualAllocEx(nint hProcess, nint lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool WriteProcessMemory(nint hProcess, nint lpBaseAddress, byte[] lpBuffer, nuint nSize, out nuint lpNumberOfBytesWritten);

[DllImport("kernel32.dll", SetLastError = true)]
static extern nint CreateRemoteThread(nint hProcess, nint lpThreadAttributes, nuint dwStackSize, nint lpStartAddress, nint lpParameter, uint dwCreationFlags, out uint lpThreadId);

[DllImport("kernel32.dll", SetLastError = true)]
static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool GetExitCodeThread(nint hThread, out uint lpExitCode);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool CloseHandle(nint hObject);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool VirtualFreeEx(nint hProcess, nint lpAddress, nuint dwSize, uint dwFreeType);

[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
static extern nint GetModuleHandle(string lpModuleName);

[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
static extern nint GetProcAddress(nint hModule, string lpProcName);

[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
static extern nint LoadLibraryEx(string lpLibFileName, nint hFile, uint dwFlags);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool FreeLibrary(nint hLibModule);
