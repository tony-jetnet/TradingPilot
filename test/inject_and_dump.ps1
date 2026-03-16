Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public class Inj {
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool b, int c);
    [DllImport("kernel32.dll")] public static extern IntPtr VirtualAllocEx(IntPtr a, IntPtr b, uint c, uint d, uint e);
    [DllImport("kernel32.dll")] public static extern bool WriteProcessMemory(IntPtr a, IntPtr b, byte[] c, uint d, out int e);
    [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandle(string a);
    [DllImport("kernel32.dll")] public static extern IntPtr GetProcAddress(IntPtr a, string b);
    [DllImport("kernel32.dll")] public static extern IntPtr CreateRemoteThread(IntPtr a, IntPtr b, uint c, IntPtr d, IntPtr e, uint f, out uint g);
    [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr a, uint b);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr a);
    [DllImport("kernel32.dll")] public static extern bool VirtualFreeEx(IntPtr a, IntPtr b, uint c, uint d);
    public static void Do(int pid, string dll) {
        IntPtr h = OpenProcess(0x1F0FFF, false, pid);
        if (h == IntPtr.Zero) throw new Exception("OpenProcess failed - run as admin?");
        byte[] p = Encoding.Unicode.GetBytes(dll + "\0");
        IntPtr m = VirtualAllocEx(h, IntPtr.Zero, (uint)p.Length, 0x3000, 0x04);
        int bw; WriteProcessMemory(h, m, p, (uint)p.Length, out bw);
        IntPtr ll = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryW");
        uint tid; IntPtr t = CreateRemoteThread(h, IntPtr.Zero, 0, ll, m, 0, out tid);
        WaitForSingleObject(t, 10000);
        VirtualFreeEx(h, m, 0, 0x8000);
        CloseHandle(t); CloseHandle(h);
    }
}
'@

$webull = Get-Process 'Webull Desktop' | Sort-Object WorkingSet64 -Descending | Select-Object -First 1
Write-Host "Webull PID: $($webull.Id)"

$hookDll = 'D:\Third-Parties\TradingPilot\src\TradingPilot.Webull.Hook\bin\Release\net10.0\win-x64\publish\TradingPilot.Webull.Hook.dll'
[Inj]::Do($webull.Id, $hookDll)
Write-Host "Injected. Waiting 5s..."
Start-Sleep -Seconds 5

# Connect to command pipe
$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'WebullMqttHookCmd', 'InOut')
$pipe.Connect(10000)
$reader = New-Object System.IO.StreamReader($pipe, [System.Text.Encoding]::UTF8, $false, 4096, $true)
$writer = New-Object System.IO.StreamWriter($pipe, (New-Object System.Text.UTF8Encoding($false)))
$writer.AutoFlush = $true

function Send($cmd) {
    $writer.WriteLine($cmd)
    return $reader.ReadLine()
}

Write-Host "Ping: $(Send '{"cmd":"ping"}')"
Write-Host ""
Write-Host "Sign info: $(Send '{"cmd":"call_sign"}')"
Write-Host ""
Write-Host "Data @0x7A79A0: $(Send '{"cmd":"dump_mem","dll":"wbgrpc.dll","rva":"7A79A0","size":64}')"
Write-Host ""
Write-Host "Embedded @0x59F708: $(Send '{"cmd":"dump_mem","dll":"wbgrpc.dll","rva":"59F708","size":32}')"

$pipe.Close()
