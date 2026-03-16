# Read the runtime secret from wbgrpc.dll in the Webull process
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;

public class MemReader {
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool b, int c);
    [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int sz, out int read);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);

    [DllImport("psapi.dll", SetLastError = true)]
    public static extern bool EnumProcessModulesEx(IntPtr hProcess, IntPtr[] lphModule, int cb, out int lpcbNeeded, uint dwFilterFlag);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode)]
    public static extern uint GetModuleFileNameEx(IntPtr hProcess, IntPtr hModule, StringBuilder lpFilename, int nSize);

    public static IntPtr FindModule(IntPtr hProcess, string name) {
        IntPtr[] modules = new IntPtr[1024];
        int needed;
        if (!EnumProcessModulesEx(hProcess, modules, modules.Length * IntPtr.Size, out needed, 3))
            return IntPtr.Zero;
        int count = needed / IntPtr.Size;
        StringBuilder sb = new StringBuilder(260);
        for (int i = 0; i < count; i++) {
            GetModuleFileNameEx(hProcess, modules[i], sb, 260);
            if (sb.ToString().EndsWith(name, StringComparison.OrdinalIgnoreCase))
                return modules[i];
        }
        return IntPtr.Zero;
    }

    public static byte[] Read(IntPtr hProcess, IntPtr addr, int size) {
        byte[] buf = new byte[size];
        int read;
        ReadProcessMemory(hProcess, addr, buf, size, out read);
        return buf;
    }
}
'@

$webull = Get-Process 'Webull Desktop' | Sort-Object WorkingSet64 -Descending | Select-Object -First 1
Write-Host "Webull PID: $($webull.Id)"

$h = [MemReader]::OpenProcess(0x1F0FFF, $false, $webull.Id)
if ($h -eq [IntPtr]::Zero) { Write-Host "OpenProcess failed"; exit }

$grpcBase = [MemReader]::FindModule($h, "wbgrpc.dll")
Write-Host "wbgrpc.dll base: 0x$($grpcBase.ToString('X'))"

if ($grpcBase -ne [IntPtr]::Zero) {
    # Read the embedded secret at RVA 0x59F708
    $embAddr = [IntPtr]::Add($grpcBase, 0x59F708)
    $embBytes = [MemReader]::Read($h, $embAddr, 32)
    $embStr = [System.Text.Encoding]::ASCII.GetString($embBytes).Split([char]0)[0]
    Write-Host "Embedded secret (@RVA 0x59F708): '$embStr'"

    # Read the .data pointer at RVA 0x7A79A0 (this is a QString = QArrayData*)
    $dataAddr = [IntPtr]::Add($grpcBase, 0x7A79A0)
    $ptrBytes = [MemReader]::Read($h, $dataAddr, 8)
    $qstrDataPtr = [BitConverter]::ToInt64($ptrBytes, 0)
    Write-Host ".data @0x7A79A0 -> QArrayData* = 0x$($qstrDataPtr.ToString('X'))"

    if ($qstrDataPtr -gt 0x10000) {
        # Read QArrayData: ref(4) + size(4) + alloc(4) + pad(4) + offset(8) = 24 bytes header
        $qaBytes = [MemReader]::Read($h, [IntPtr]$qstrDataPtr, 48)
        $ref = [BitConverter]::ToInt32($qaBytes, 0)
        $size = [BitConverter]::ToInt32($qaBytes, 4)
        $alloc = [BitConverter]::ToInt32($qaBytes, 8)
        $offset = [BitConverter]::ToInt64($qaBytes, 16)
        Write-Host "QArrayData: ref=$ref size=$size alloc=$alloc offset=$offset"

        if ($size -gt 0 -and $size -lt 1000) {
            # Read UTF-16 data at qstrDataPtr + offset
            $strAddr = [IntPtr]::Add([IntPtr]$qstrDataPtr, [int]$offset)
            $strBytes = [MemReader]::Read($h, $strAddr, $size * 2)
            $str = [System.Text.Encoding]::Unicode.GetString($strBytes)
            Write-Host "QString value (size=$size): '$str'"
        } else {
            Write-Host "String size=$size (empty or invalid)"
        }
    }

    # Also try reading .data as a larger area to see the structure
    $dataArea = [MemReader]::Read($h, $dataAddr, 128)
    Write-Host ""
    Write-Host "Raw .data at 0x7A79A0 (128 bytes):"
    Write-Host ([BitConverter]::ToString($dataArea).Replace('-', ' '))
}

[MemReader]::CloseHandle($h) | Out-Null
