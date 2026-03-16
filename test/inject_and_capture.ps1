# Inject hook DLL and call Initialize export using a temporary .NET console app
$projDir = Split-Path $PSScriptRoot -Parent

$testCode = @'
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

partial class Program
{
    [LibraryImport("kernel32.dll")] public static partial nint OpenProcess(uint a, [MarshalAs(UnmanagedType.Bool)] bool b, int c);
    [LibraryImport("kernel32.dll")] public static partial nint VirtualAllocEx(nint a, nint b, nuint c, uint d, uint e);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool WriteProcessMemory(nint a, nint b, byte[] c, nuint d, out nuint e);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial nint GetModuleHandleW(string a);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf8)] public static partial nint GetProcAddress(nint a, string b);
    [LibraryImport("kernel32.dll")] public static partial nint CreateRemoteThread(nint a, nint b, nuint c, nint d, nint e, uint f, out uint g);
    [LibraryImport("kernel32.dll")] public static partial uint WaitForSingleObject(nint a, uint b);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetExitCodeThread(nint a, out uint b);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool CloseHandle(nint a);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool VirtualFreeEx(nint a, nint b, nuint c, uint d);

    static void Main()
    {
        var webull = Process.GetProcessesByName("Webull Desktop")
            .OrderByDescending(p => p.WorkingSet64).FirstOrDefault();
        if (webull == null) { Console.WriteLine("Webull not running"); return; }
        Console.WriteLine($"Webull PID: {webull.Id}");

        string dllPath = @"D:\Third-Parties\TradingPilot\src\TradingPilot.Webull.Hook\bin\Release\net10.0\win-x64\publish\TradingPilot.Webull.Hook.dll";
        Console.WriteLine($"DLL: {dllPath}");
        if (!File.Exists(dllPath)) { Console.WriteLine("DLL not found!"); return; }

        nint hProc = OpenProcess(0x1F0FFF, false, webull.Id);
        if (hProc == 0) { Console.WriteLine("OpenProcess failed"); return; }

        // Step 1: LoadLibraryW
        byte[] pathBytes = Encoding.Unicode.GetBytes(dllPath + '\0');
        nint mem = VirtualAllocEx(hProc, 0, (nuint)pathBytes.Length, 0x3000, 0x04);
        WriteProcessMemory(hProc, mem, pathBytes, (nuint)pathBytes.Length, out _);
        nint loadLib = GetProcAddress(GetModuleHandleW("kernel32.dll"), "LoadLibraryW");
        nint t = CreateRemoteThread(hProc, 0, 0, loadLib, mem, 0, out _);
        WaitForSingleObject(t, 10000);
        GetExitCodeThread(t, out uint moduleHandle);
        CloseHandle(t);
        VirtualFreeEx(hProc, mem, 0, 0x8000);
        Console.WriteLine($"LoadLibraryW module handle: 0x{moduleHandle:X}");

        // Step 2: Call Initialize via shellcode
        nint getModHandle = GetProcAddress(GetModuleHandleW("kernel32.dll"), "GetModuleHandleW");
        nint getProcAddr = GetProcAddress(GetModuleHandleW("kernel32.dll"), "GetProcAddress");

        string dllName = Path.GetFileName(dllPath);
        byte[] dllNameBytes = Encoding.Unicode.GetBytes(dllName + '\0');
        byte[] initNameBytes = Encoding.ASCII.GetBytes("Initialize\0");

        int scSize = 128;
        int dllNameOff = scSize;
        int initNameOff = dllNameOff + dllNameBytes.Length;
        int totalSize = initNameOff + initNameBytes.Length + 64;

        nint remoteBuf = VirtualAllocEx(hProc, 0, (nuint)totalSize, 0x3000, 0x40);
        Console.WriteLine($"Shellcode buffer: 0x{remoteBuf:X}");

        byte[] sc = new byte[totalSize];
        int i = 0;

        // sub rsp, 0x28
        sc[i++] = 0x48; sc[i++] = 0x83; sc[i++] = 0xEC; sc[i++] = 0x28;

        // mov rcx, <remoteBuf + dllNameOff>
        sc[i++] = 0x48; sc[i++] = 0xB9;
        BitConverter.GetBytes((long)remoteBuf + dllNameOff).CopyTo(sc, i); i += 8;

        // mov rax, <GetModuleHandleW>
        sc[i++] = 0x48; sc[i++] = 0xB8;
        BitConverter.GetBytes((long)getModHandle).CopyTo(sc, i); i += 8;

        // call rax
        sc[i++] = 0xFF; sc[i++] = 0xD0;

        // mov rcx, rax (HMODULE)
        sc[i++] = 0x48; sc[i++] = 0x89; sc[i++] = 0xC1;

        // mov rdx, <remoteBuf + initNameOff>
        sc[i++] = 0x48; sc[i++] = 0xBA;
        BitConverter.GetBytes((long)remoteBuf + initNameOff).CopyTo(sc, i); i += 8;

        // mov rax, <GetProcAddress>
        sc[i++] = 0x48; sc[i++] = 0xB8;
        BitConverter.GetBytes((long)getProcAddr).CopyTo(sc, i); i += 8;

        // call rax
        sc[i++] = 0xFF; sc[i++] = 0xD0;

        // test rax, rax / jz skip
        sc[i++] = 0x48; sc[i++] = 0x85; sc[i++] = 0xC0;
        sc[i++] = 0x74; sc[i++] = 0x05;

        // xor ecx, ecx
        sc[i++] = 0x31; sc[i++] = 0xC9;

        // call rax (Initialize(0))
        sc[i++] = 0xFF; sc[i++] = 0xD0;

        // nop (skip target)
        sc[i++] = 0x90;

        // add rsp, 0x28
        sc[i++] = 0x48; sc[i++] = 0x83; sc[i++] = 0xC4; sc[i++] = 0x28;

        // ret
        sc[i++] = 0xC3;

        dllNameBytes.CopyTo(sc, dllNameOff);
        initNameBytes.CopyTo(sc, initNameOff);

        WriteProcessMemory(hProc, remoteBuf, sc, (nuint)totalSize, out _);

        nint t2 = CreateRemoteThread(hProc, 0, 0, remoteBuf, 0, 0, out _);
        WaitForSingleObject(t2, 10000);
        GetExitCodeThread(t2, out uint initResult);
        CloseHandle(t2);
        VirtualFreeEx(hProc, remoteBuf, 0, 0x8000);
        CloseHandle(hProc);

        Console.WriteLine($"Initialize returned: {initResult}");
        Console.WriteLine("Done. Check hook.log for gRPC captures.");
    }
}
'@

$tmpDir = Join-Path $env:TEMP "inject_$(Get-Random)"
New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
</Project>
"@ | Set-Content (Join-Path $tmpDir "Inject.csproj")
$testCode | Set-Content (Join-Path $tmpDir "Program.cs")
Push-Location $tmpDir
dotnet run --verbosity quiet
Pop-Location

Write-Host "Waiting 15s for gRPC activity..."
Start-Sleep -Seconds 15

$logPath = 'C:\Users\chenx\AppData\Local\WebullHook\hook.log'
Write-Host ""
Write-Host "=== GRPC-related log entries ==="
Get-Content $logPath -Tail 200 | Where-Object { $_ -match 'GRPC|grpc|wbgrpc|Hook|HOOK|hook|Write|All hooks' } | ForEach-Object { Write-Host $_.Trim() }

Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
