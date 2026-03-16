using System.Runtime.InteropServices;

namespace TradingPilot.Webull.Hook;

/// <summary>
/// x64 inline function hook. Patches the first 14 bytes of the target with:
///   mov rax, <detour_address>   ; 48 B8 xx xx xx xx xx xx xx xx
///   jmp rax                      ; FF E0
/// A trampoline is allocated that contains the saved original bytes + jmp back.
/// </summary>
internal static unsafe class X64Hook
{
    private const int PatchSize = 14; // mov rax, imm64 (10) + jmp rax (2) + 2 padding = 14

    /// <summary>
    /// Install a hook on the target function.
    /// Returns a pointer to the trampoline (call this to invoke the original function).
    /// </summary>
    public static nint Install(nint target, nint detour)
    {
        if (target == 0 || detour == 0)
            throw new ArgumentException("Target and detour must be non-zero.");

        // Allocate trampoline: saved bytes + 14-byte jump back
        nint trampoline = NativeMethods.VirtualAlloc(
            0,
            (nuint)(PatchSize + 14),
            NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
            NativeMethods.PAGE_EXECUTE_READWRITE);

        if (trampoline == 0)
            throw new InvalidOperationException($"VirtualAlloc failed: {Marshal.GetLastPInvokeError()}");

        // Save original bytes to trampoline
        Buffer.MemoryCopy((void*)target, (void*)trampoline, PatchSize, PatchSize);

        // Write jump back to (target + PatchSize) at end of trampoline
        nint trampolineJmp = trampoline + PatchSize;
        nint returnAddr = target + PatchSize;
        WriteAbsoluteJump(trampolineJmp, returnAddr);

        // Unprotect target
        if (!NativeMethods.VirtualProtect(target, (nuint)PatchSize, NativeMethods.PAGE_EXECUTE_READWRITE, out uint oldProtect))
            throw new InvalidOperationException($"VirtualProtect failed: {Marshal.GetLastPInvokeError()}");

        // Patch target with jump to detour
        WriteAbsoluteJump(target, detour);

        // Restore protection
        NativeMethods.VirtualProtect(target, (nuint)PatchSize, oldProtect, out _);

        // Flush instruction cache
        NativeMethods.FlushInstructionCache(NativeMethods.GetCurrentProcess(), target, (nuint)PatchSize);
        NativeMethods.FlushInstructionCache(NativeMethods.GetCurrentProcess(), trampoline, (nuint)(PatchSize + 14));

        return trampoline;
    }

    /// <summary>
    /// Write a 14-byte absolute jump: mov rax, addr; jmp rax; nop; nop
    /// </summary>
    private static void WriteAbsoluteJump(nint location, nint targetAddr)
    {
        byte* p = (byte*)location;

        // mov rax, imm64
        p[0] = 0x48;
        p[1] = 0xB8;
        *(long*)(p + 2) = (long)targetAddr;

        // jmp rax
        p[10] = 0xFF;
        p[11] = 0xE0;

        // nop padding
        p[12] = 0x90;
        p[13] = 0x90;
    }
}
