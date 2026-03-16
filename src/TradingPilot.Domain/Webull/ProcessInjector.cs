using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;

namespace TradingPilot.Webull;

/// <summary>
/// Injects a native DLL into a target process using CreateRemoteThread + LoadLibraryW.
/// </summary>
public partial class ProcessInjector : ITransientDependency
{
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetModuleHandleW([MarshalAs(UnmanagedType.LPWStr)] string lpModuleName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetProcAddress(nint hModule, [MarshalAs(UnmanagedType.LPStr)] string lpProcName);

    private const uint PROCESS_ALL_ACCESS = 0x1F0FFF;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;

    private readonly ILogger<ProcessInjector> _logger;

    public ProcessInjector(ILogger<ProcessInjector> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Inject a DLL into the target process by path.
    /// </summary>
    public void Inject(int processId, string dllPath)
    {
        string fullPath = Path.GetFullPath(dllPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"DLL not found: {fullPath}");

        _logger.LogInformation("Injecting {DllName} into PID {ProcessId}...", Path.GetFileName(fullPath), processId);

        nint kernel32 = GetModuleHandleW("kernel32.dll");
        nint loadLibraryAddr = GetProcAddress(kernel32, "LoadLibraryW");
        if (loadLibraryAddr == 0)
            throw new InvalidOperationException("Could not find LoadLibraryW.");

        nint hProcess = OpenProcess(PROCESS_ALL_ACCESS, false, processId);
        if (hProcess == 0)
            throw new InvalidOperationException($"OpenProcess failed: {Marshal.GetLastPInvokeError()}");

        try
        {
            byte[] pathBytes = Encoding.Unicode.GetBytes(fullPath + '\0');
            nint remoteMemory = VirtualAllocEx(hProcess, 0, (nuint)pathBytes.Length, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (remoteMemory == 0)
                throw new InvalidOperationException($"VirtualAllocEx failed: {Marshal.GetLastPInvokeError()}");

            try
            {
                if (!WriteProcessMemory(hProcess, remoteMemory, pathBytes, (nuint)pathBytes.Length, out _))
                    throw new InvalidOperationException($"WriteProcessMemory failed: {Marshal.GetLastPInvokeError()}");

                nint hThread = CreateRemoteThread(hProcess, 0, 0, loadLibraryAddr, remoteMemory, 0, out _);
                if (hThread == 0)
                    throw new InvalidOperationException($"CreateRemoteThread failed: {Marshal.GetLastPInvokeError()}");

                WaitForSingleObject(hThread, 10000);
                GetExitCodeThread(hThread, out uint exitCode);
                CloseHandle(hThread);

                if (exitCode == 0)
                    throw new InvalidOperationException("LoadLibraryW returned NULL — injection failed. Check hook.log for details.");

                _logger.LogInformation("DLL injected successfully. Module handle: 0x{ModuleHandle:X}", exitCode);
            }
            finally
            {
                VirtualFreeEx(hProcess, remoteMemory, 0, MEM_RELEASE);
            }
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    /// <summary>
    /// Find the Webull Desktop process (largest working set if multiple).
    /// </summary>
    public Process? FindWebullProcess()
    {
        var processes = Process.GetProcessesByName("Webull Desktop");
        var result = processes.OrderByDescending(p => p.WorkingSet64).FirstOrDefault();

        if (result != null)
            _logger.LogInformation("Found Webull Desktop (PID: {Pid}, Memory: {MemoryMb} MB)", result.Id, result.WorkingSet64 / 1024 / 1024);
        else
            _logger.LogWarning("Webull Desktop is not running.");

        return result;
    }
}
