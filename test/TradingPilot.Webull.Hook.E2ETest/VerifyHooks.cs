using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TradingPilot.Webull.Hook.E2ETest;

public static class VerifyHooks
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(nint hProcess, nint lpBaseAddress, byte[] lpBuffer, nuint nSize, out nuint lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern nint OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(nint hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern nint GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    static extern nint GetProcAddress(nint hModule, string lpProcName);

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Hook Verification Tool ===\n");

        var webull = Process.GetProcessesByName("Webull Desktop")
            .OrderByDescending(p => p.WorkingSet64).FirstOrDefault();
        if (webull == null) { Console.WriteLine("Webull not running."); return 1; }

        Console.WriteLine($"Webull PID: {webull.Id}");

        // Find wbmqtt.dll in remote process
        nint remoteBase = 0;
        foreach (ProcessModule mod in webull.Modules)
        {
            if (mod.ModuleName?.Equals("wbmqtt.dll", StringComparison.OrdinalIgnoreCase) == true)
            {
                remoteBase = mod.BaseAddress;
                break;
            }
        }
        if (remoteBase == 0) { Console.WriteLine("wbmqtt.dll not found!"); return 1; }
        Console.WriteLine($"wbmqtt.dll remote base: 0x{remoteBase:X}\n");

        // Load wbmqtt.dll locally to get function offsets
        nint localBase = LoadLibraryEx(@"C:\Program Files (x86)\Webull Desktop\wbmqtt.dll", 0, 0x01);
        if (localBase == 0) { Console.WriteLine("Can't load wbmqtt.dll locally"); return 1; }

        string[] functions = [
            "?messageReceived@QMqttClient@@QEAAXAEBVQByteArray@@AEBVQMqttTopicName@@@Z",
            "?subscribe@QMqttClient@@QEAAPEAVQMqttSubscription@@AEBVQMqttTopicFilter@@E@Z",
            "?connectToHost@QMqttClient@@QEAAXXZ",
            "?connected@QMqttClient@@QEAAXXZ",
            "?invokeSubscribe@QMqttClient@@QEAA?AW4ReasonCode@QMqtt@@AEBVQString@@@Z",
            "?invokeSubscribeResult@QMqttClient@@QEAA?AW4ReasonCode@QMqtt@@AEBVQString@@@Z",
        ];

        nint hProcess = OpenProcess(0x0010, false, webull.Id); // PROCESS_VM_READ
        if (hProcess == 0) { Console.WriteLine("Can't open process."); return 1; }

        try
        {
            foreach (var fn in functions)
            {
                nint localAddr = GetProcAddress(localBase, fn);
                if (localAddr == 0) { Console.WriteLine($"  {Demangle(fn)}: NOT FOUND"); continue; }

                long offset = localAddr - localBase;
                nint remoteAddr = remoteBase + (nint)offset;

                // Read first 32 bytes from the remote process
                byte[] bytes = new byte[32];
                ReadProcessMemory(hProcess, remoteAddr, bytes, 32, out _);

                string hex = BitConverter.ToString(bytes).Replace("-", " ");
                bool isHooked = bytes[0] == 0x48 && bytes[1] == 0xB8 && bytes[10] == 0xFF && bytes[11] == 0xE0;

                Console.ForegroundColor = isHooked ? ConsoleColor.Green : ConsoleColor.Yellow;
                Console.Write(isHooked ? "[HOOKED] " : "[NATIVE] ");
                Console.ResetColor();
                Console.WriteLine($"{Demangle(fn)}");
                Console.WriteLine($"  addr: 0x{remoteAddr:X}  offset: 0x{offset:X}");
                Console.Write("  bytes: ");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine(hex);
                Console.ResetColor();

                if (isHooked)
                {
                    long detourAddr = BitConverter.ToInt64(bytes, 2);
                    Console.WriteLine($"  detour -> 0x{detourAddr:X}");
                }
                Console.WriteLine();
            }
        }
        finally
        {
            CloseHandle(hProcess);
            FreeLibrary(localBase);
        }

        return 0;
    }

    static string Demangle(string name)
    {
        // Simple demangling: extract the function name
        int at1 = name.IndexOf('@');
        int q = name.IndexOf('?', 1);
        if (q > 0 && at1 > q) at1 = q;
        string fn = name[1..at1];

        int at2 = name.IndexOf("@@", at1);
        string cls = at2 > at1 ? name[(at1 + 1)..at2] : "";

        return cls.Length > 0 ? $"{cls}::{fn}" : fn;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern nint LoadLibraryEx(string lpLibFileName, nint hFile, uint dwFlags);

    [DllImport("kernel32.dll")]
    static extern bool FreeLibrary(nint hLibModule);
}
