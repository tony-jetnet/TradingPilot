using System.Runtime.InteropServices;

namespace TradingPilot.Webull.Hook;

/// <summary>
/// x64 inline function hook with instruction-aware relocation.
/// Copies complete instructions (>= 14 bytes) to a trampoline,
/// fixing up RIP-relative addressing and short branches.
/// </summary>
internal static unsafe class X64Hook
{
    private const int MinPatchSize = 14; // mov rax, imm64 (10) + jmp rax (2) + nop*2 (2)

    /// <summary>
    /// Install a hook on the target function.
    /// Returns a pointer to the trampoline (call this to invoke the original function).
    /// </summary>
    public static nint Install(nint target, nint detour)
    {
        if (target == 0 || detour == 0)
            throw new ArgumentException("Target and detour must be non-zero.");

        byte* src = (byte*)target;

        // Step 1: Determine how many bytes to copy (complete instructions >= 14 bytes)
        int copyLen = 0;
        while (copyLen < MinPatchSize)
        {
            int instrLen = GetInstructionLength(src + copyLen);
            if (instrLen <= 0)
                throw new InvalidOperationException($"Failed to decode instruction at offset {copyLen}, byte=0x{src[copyLen]:X2}");
            HookLog.Write($"    instr @+{copyLen}: {instrLen}B [{HexBytes(src + copyLen, instrLen)}]");
            copyLen += instrLen;
        }
        HookLog.Write($"    total copy: {copyLen}B");

        // Step 2: Allocate trampoline NEAR the target (within ±2GB) so RIP-relative
        // displacements in relocated instructions remain valid as 32-bit offsets.
        int trampolineSize = copyLen * 3 + 14; // worst case expansion + jump back
        nint trampoline = AllocateNear(target, (nuint)trampolineSize);

        if (trampoline == 0)
            throw new InvalidOperationException($"Failed to allocate trampoline near 0x{target:X}");

        // Step 3: Copy and relocate instructions to trampoline
        byte* dst = (byte*)trampoline;
        int srcOff = 0;
        int dstOff = 0;

        while (srcOff < copyLen)
        {
            int instrLen = GetInstructionLength(src + srcOff);
            nint srcAddr = target + srcOff;
            nint dstAddr = trampoline + dstOff;

            int written = RelocateInstruction(src + srcOff, instrLen, srcAddr, dst + dstOff, dstAddr);
            srcOff += instrLen;
            dstOff += written;
        }

        // Step 4: Write jump back to (target + copyLen)
        WriteAbsoluteJump((nint)(dst + dstOff), target + copyLen);
        dstOff += 14;

        // Step 5: Mark detour as valid CFG target
        try
        {
            long detourPage = (long)detour & ~0xFFF;
            var detourInfo = new NativeMethods.CFG_CALL_TARGET_INFO
            {
                Offset = (nuint)((long)detour - detourPage),
                Flags = NativeMethods.CFG_CALL_TARGET_VALID
            };
            NativeMethods.SetProcessValidCallTargets(
                NativeMethods.GetCurrentProcess(),
                (nint)detourPage, 0x1000, 1, ref detourInfo);
        }
        catch { }

        // Mark trampoline as valid CFG target
        try
        {
            long trampolinePage = (long)trampoline & ~0xFFF;
            var trampolineInfo = new NativeMethods.CFG_CALL_TARGET_INFO
            {
                Offset = (nuint)((long)trampoline - trampolinePage),
                Flags = NativeMethods.CFG_CALL_TARGET_VALID
            };
            NativeMethods.SetProcessValidCallTargets(
                NativeMethods.GetCurrentProcess(),
                (nint)trampolinePage, 0x1000, 1, ref trampolineInfo);
        }
        catch { }

        // Step 5b: Patch target with jump to detour
        if (!NativeMethods.VirtualProtect(target, (nuint)copyLen, NativeMethods.PAGE_EXECUTE_READWRITE, out uint oldProtect))
            throw new InvalidOperationException($"VirtualProtect failed: {Marshal.GetLastPInvokeError()}");

        WriteAbsoluteJump(target, detour);
        // NOP-fill any remaining bytes beyond the 14-byte jump
        for (int i = MinPatchSize; i < copyLen; i++)
            src[i] = 0x90;

        NativeMethods.VirtualProtect(target, (nuint)copyLen, oldProtect, out _);

        // Step 6: Mark original+copyLen as valid CFG call target (for the trampoline's jmp back)
        try
        {
            nint returnAddr2 = target + copyLen;
            // Get the page base for the return address
            long pageBase = (long)returnAddr2 & ~0xFFF;
            var info = new NativeMethods.CFG_CALL_TARGET_INFO
            {
                Offset = (nuint)((long)returnAddr2 - pageBase),
                Flags = NativeMethods.CFG_CALL_TARGET_VALID
            };
            NativeMethods.SetProcessValidCallTargets(
                NativeMethods.GetCurrentProcess(),
                (nint)pageBase, 0x1000, 1, ref info);
        }
        catch { }

        // Step 7: Flush instruction caches
        NativeMethods.FlushInstructionCache(NativeMethods.GetCurrentProcess(), target, (nuint)copyLen);
        NativeMethods.FlushInstructionCache(NativeMethods.GetCurrentProcess(), trampoline, (nuint)dstOff);

        // Debug: dump trampoline content and patched target
        HookLog.Write($"    trampoline [{dstOff}B]: {HexBytes((byte*)trampoline, dstOff)}");
        HookLog.Write($"    patched target [{copyLen}B]: {HexBytes(src, copyLen)}");

        return trampoline;
    }

    /// <summary>
    /// Relocate a single instruction from src to dst, adjusting RIP-relative operands.
    /// Returns the number of bytes written to dst.
    /// </summary>
    private static int RelocateInstruction(byte* src, int len, nint srcAddr, byte* dst, nint dstAddr)
    {
        // Check for RIP-relative addressing (ModR/M byte with mod=00, rm=101)
        // and relative branches (Jcc, JMP, CALL)

        int i = 0;

        // Skip prefixes
        while (i < len)
        {
            byte b = src[i];
            if (b == 0x66 || b == 0x67 || b == 0xF2 || b == 0xF3 ||
                b == 0x26 || b == 0x2E || b == 0x36 || b == 0x3E || b == 0x64 || b == 0x65)
            {
                i++;
                continue;
            }
            if ((b & 0xF0) == 0x40) // REX prefix
            {
                i++;
                continue;
            }
            break;
        }

        if (i >= len)
        {
            // Just prefixes? Copy as-is
            Buffer.MemoryCopy(src, dst, len, len);
            return len;
        }

        byte opcode = src[i];

        // ── Short conditional branch (0x70-0x7F): 2 bytes, rel8 ──
        if (opcode >= 0x70 && opcode <= 0x7F && len == 2)
        {
            sbyte rel8 = (sbyte)src[i + 1];
            long target = (long)srcAddr + len + rel8;

            // Expand to: Jcc near (6 bytes) + jmp abs (14 bytes) pattern
            // Actually simpler: rewrite as near Jcc rel32
            dst[0] = 0x0F;
            dst[1] = (byte)(opcode + 0x10); // 0x70→0x80, etc.
            int rel32 = (int)(target - ((long)dstAddr + 6));
            *(int*)(dst + 2) = rel32;
            return 6;
        }

        // ── Short JMP (0xEB): 2 bytes, rel8 ──
        if (opcode == 0xEB && len == 2)
        {
            sbyte rel8 = (sbyte)src[i + 1];
            long target = (long)srcAddr + len + rel8;

            // Rewrite as near JMP rel32
            dst[0] = 0xE9;
            int rel32 = (int)(target - ((long)dstAddr + 5));
            *(int*)(dst + 1) = rel32;
            return 5;
        }

        // ── CALL rel32 (0xE8) or JMP rel32 (0xE9): 5 bytes ──
        if ((opcode == 0xE8 || opcode == 0xE9) && len == 5)
        {
            int origRel32 = *(int*)(src + i + 1);
            long target = (long)srcAddr + len + origRel32;

            dst[0] = opcode;
            int newRel32 = (int)(target - ((long)dstAddr + 5));
            *(int*)(dst + 1) = newRel32;
            return 5;
        }

        // ── Near conditional branch (0x0F 0x80-0x8F): 6 bytes, rel32 ──
        if (opcode == 0x0F && i + 1 < len && src[i + 1] >= 0x80 && src[i + 1] <= 0x8F && len == 6)
        {
            int origRel32 = *(int*)(src + i + 2);
            long target = (long)srcAddr + len + origRel32;

            dst[0] = 0x0F;
            dst[1] = src[i + 1];
            int newRel32 = (int)(target - ((long)dstAddr + 6));
            *(int*)(dst + 2) = newRel32;
            return 6;
        }

        // ── Instructions with RIP-relative ModR/M (mod=00, rm=101) ──
        // These have [RIP+disp32] addressing that needs fixup
        int modrm_offset = FindModRMOffset(src, len, i);
        if (modrm_offset >= 0 && modrm_offset < len)
        {
            byte modrm = src[modrm_offset];
            byte mod = (byte)((modrm >> 6) & 3);
            byte rm = (byte)(modrm & 7);

            if (mod == 0 && rm == 5) // RIP-relative
            {
                // disp32 follows the ModR/M byte (and SIB if present, but rm=5 has no SIB in mod=0)
                int dispOffset = modrm_offset + 1;
                if (dispOffset + 4 <= len)
                {
                    int origDisp = *(int*)(src + dispOffset);
                    long targetAddr = (long)srcAddr + len + origDisp;

                    // Copy instruction, fix up the disp32
                    Buffer.MemoryCopy(src, dst, len, len);
                    int newDisp = (int)(targetAddr - ((long)dstAddr + len));
                    *(int*)(dst + dispOffset) = newDisp;
                    return len;
                }
            }
        }

        // ── FF /4 or FF /5 with RIP-relative (JMP/CALL [rip+disp32]) ──
        if (opcode == 0xFF && i + 1 < len)
        {
            byte modrm = src[i + 1];
            byte mod2 = (byte)((modrm >> 6) & 3);
            byte reg2 = (byte)((modrm >> 3) & 7);
            byte rm2 = (byte)(modrm & 7);

            if (mod2 == 0 && rm2 == 5 && (reg2 == 4 || reg2 == 5)) // JMP/CALL [rip+disp32]
            {
                int dispOff = i + 2;
                if (dispOff + 4 <= len)
                {
                    int origDisp = *(int*)(src + dispOff);
                    long targetAddr = (long)srcAddr + len + origDisp;

                    Buffer.MemoryCopy(src, dst, len, len);
                    int newDisp = (int)(targetAddr - ((long)dstAddr + len));
                    *(int*)(dst + dispOff) = newDisp;
                    return len;
                }
            }
        }

        // ── Default: copy as-is (no relocation needed) ──
        Buffer.MemoryCopy(src, dst, len, len);
        return len;
    }

    /// <summary>
    /// Find the offset of the ModR/M byte in an instruction starting at src[opcodeOffset].
    /// Returns -1 if the instruction has no ModR/M byte.
    /// </summary>
    private static int FindModRMOffset(byte* src, int len, int opcodeOffset)
    {
        byte opcode = src[opcodeOffset];

        // Two-byte opcode (0x0F xx)
        if (opcode == 0x0F && opcodeOffset + 1 < len)
            return opcodeOffset + 2; // ModR/M follows 0x0F xx

        // Most single-byte opcodes with ModR/M:
        // 00-3F (arithmetic), 80-8F (arithmetic imm), 63, 69, 6B,
        // 88-8F, C0-C1, C4-C5, C6-C7, D0-D3, D8-DF (FPU),
        // F6-F7, FE-FF
        // Also: 84-85 (TEST), 86-87 (XCHG), 88-8D (MOV variants)
        if ((opcode & 0xC0) == 0x00 && (opcode & 0x07) < 6 && opcode < 0x40) return opcodeOffset + 1;
        if (opcode >= 0x80 && opcode <= 0x8F) return opcodeOffset + 1;
        if (opcode >= 0x88 && opcode <= 0x8F) return opcodeOffset + 1;
        if (opcode == 0x63 || opcode == 0x69 || opcode == 0x6B) return opcodeOffset + 1;
        if (opcode == 0xC4 || opcode == 0xC5) return opcodeOffset + 1;
        if (opcode == 0xFF || opcode == 0xFE) return opcodeOffset + 1;
        if (opcode == 0xF6 || opcode == 0xF7) return opcodeOffset + 1;
        if (opcode == 0xD1 || opcode == 0xD3) return opcodeOffset + 1;

        return -1;
    }

    /// <summary>
    /// Minimal x64 instruction length decoder.
    /// Handles the common instructions found in MSVC-compiled function prologues.
    /// </summary>
    private static int GetInstructionLength(byte* ip)
    {
        byte* start = ip;
        bool hasRex = false;
        byte rex = 0;

        // Skip prefixes
        while (true)
        {
            byte b = *ip;
            if (b == 0x66 || b == 0x67 || b == 0xF2 || b == 0xF3) { ip++; continue; }
            if (b == 0x26 || b == 0x2E || b == 0x36 || b == 0x3E || b == 0x64 || b == 0x65) { ip++; continue; }
            if ((b & 0xF0) == 0x40) { hasRex = true; rex = b; ip++; continue; } // REX
            break;
        }

        byte opcode = *ip++;

        // Two-byte opcode escape
        if (opcode == 0x0F)
        {
            byte op2 = *ip++;

            // Jcc near rel32: 0F 80-8F
            if (op2 >= 0x80 && op2 <= 0x8F)
                return (int)(ip - start) + 4;

            // MOVSS/MOVSD/MOVAPS/etc with ModR/M
            // Most 0F xx instructions have ModR/M
            return (int)(ip - start) + DecodeModRM(ip);
        }

        // ── Single byte instructions without operands ──
        // NOP, RET, LEAVE, etc.
        if (opcode == 0x90) return (int)(ip - start); // NOP
        if (opcode == 0xC3) return (int)(ip - start); // RET
        if (opcode == 0xCC) return (int)(ip - start); // INT3
        if (opcode == 0xC9) return (int)(ip - start); // LEAVE

        // PUSH/POP register (50-5F)
        if (opcode >= 0x50 && opcode <= 0x5F) return (int)(ip - start);

        // XCHG eax,reg (91-97)
        if (opcode >= 0x91 && opcode <= 0x97) return (int)(ip - start);

        // ── rel8 branches (2 byte total) ──
        if (opcode >= 0x70 && opcode <= 0x7F) return (int)(ip - start) + 1; // Jcc short
        if (opcode == 0xEB) return (int)(ip - start) + 1; // JMP short

        // ── rel32 branches/calls (5 bytes total) ──
        if (opcode == 0xE8) return (int)(ip - start) + 4; // CALL rel32
        if (opcode == 0xE9) return (int)(ip - start) + 4; // JMP rel32

        // ── MOV reg, imm (B0-BF) ──
        if (opcode >= 0xB0 && opcode <= 0xB7) return (int)(ip - start) + 1; // MOV r8, imm8
        if (opcode >= 0xB8 && opcode <= 0xBF)
        {
            // MOV r32/r64, imm32/imm64
            if (hasRex && (rex & 0x08) != 0) // REX.W
                return (int)(ip - start) + 8; // imm64
            return (int)(ip - start) + 4; // imm32
        }

        // ── Instructions with ModR/M byte ──
        // Arithmetic (00-3F even opcodes + some odd): ADD, OR, ADC, SBB, AND, SUB, XOR, CMP
        if (opcode <= 0x3F)
        {
            byte col = (byte)(opcode & 7);
            if (col <= 3) // ModR/M variants
                return (int)(ip - start) + DecodeModRM(ip);
            if (col == 4) // AL, imm8
                return (int)(ip - start) + 1;
            if (col == 5) // eAX, imm32
                return (int)(ip - start) + 4;
            // col 6,7 are segment prefixes handled above
            return (int)(ip - start);
        }

        // LEA, MOV variants, TEST (63, 69, 6B, 84-8F)
        if (opcode == 0x63) return (int)(ip - start) + DecodeModRM(ip);
        if (opcode == 0x69) return (int)(ip - start) + DecodeModRM(ip) + 4; // imm32
        if (opcode == 0x6B) return (int)(ip - start) + DecodeModRM(ip) + 1; // imm8
        if (opcode >= 0x84 && opcode <= 0x8F) return (int)(ip - start) + DecodeModRM(ip);

        // 80-83: arithmetic with immediate
        if (opcode == 0x80 || opcode == 0x82) return (int)(ip - start) + DecodeModRM(ip) + 1; // imm8
        if (opcode == 0x81) return (int)(ip - start) + DecodeModRM(ip) + 4; // imm32
        if (opcode == 0x83) return (int)(ip - start) + DecodeModRM(ip) + 1; // imm8

        // C0-C1: shift with imm8
        if (opcode == 0xC0 || opcode == 0xC1) return (int)(ip - start) + DecodeModRM(ip) + 1;
        // D0-D3: shift by 1 or CL
        if (opcode >= 0xD0 && opcode <= 0xD3) return (int)(ip - start) + DecodeModRM(ip);

        // C6: MOV r/m8, imm8
        if (opcode == 0xC6) return (int)(ip - start) + DecodeModRM(ip) + 1;
        // C7: MOV r/m32, imm32
        if (opcode == 0xC7) return (int)(ip - start) + DecodeModRM(ip) + 4;

        // F6: TEST/NOT/NEG/MUL/DIV r/m8 (group 3)
        if (opcode == 0xF6)
        {
            byte modrm = *ip;
            byte reg = (byte)((modrm >> 3) & 7);
            int mrmLen = DecodeModRM(ip);
            if (reg == 0 || reg == 1) // TEST r/m8, imm8
                return (int)(ip - start) + mrmLen + 1;
            return (int)(ip - start) + mrmLen;
        }
        // F7: TEST/NOT/NEG/MUL/DIV r/m32 (group 3)
        if (opcode == 0xF7)
        {
            byte modrm = *ip;
            byte reg = (byte)((modrm >> 3) & 7);
            int mrmLen = DecodeModRM(ip);
            if (reg == 0 || reg == 1) // TEST r/m32, imm32
                return (int)(ip - start) + mrmLen + 4;
            return (int)(ip - start) + mrmLen;
        }

        // FE: INC/DEC r/m8
        if (opcode == 0xFE) return (int)(ip - start) + DecodeModRM(ip);
        // FF: INC/DEC/CALL/JMP/PUSH r/m
        if (opcode == 0xFF) return (int)(ip - start) + DecodeModRM(ip);

        // RET imm16
        if (opcode == 0xC2) return (int)(ip - start) + 2;

        // PUSH imm
        if (opcode == 0x68) return (int)(ip - start) + 4; // PUSH imm32
        if (opcode == 0x6A) return (int)(ip - start) + 1; // PUSH imm8

        // If we can't decode, return 0 to signal failure
        HookLog.Write($"  WARNING: Unknown opcode 0x{opcode:X2} at offset {(int)(start - ip)}");
        return 0;
    }

    /// <summary>
    /// Decode ModR/M (and optional SIB + displacement) to determine the total
    /// number of bytes consumed by the ModR/M operand encoding.
    /// ip points to the ModR/M byte.
    /// </summary>
    private static int DecodeModRM(byte* ip)
    {
        byte modrm = *ip;
        byte mod = (byte)((modrm >> 6) & 3);
        byte rm = (byte)(modrm & 7);

        int size = 1; // ModR/M byte itself

        if (mod == 3) return size; // Register direct, no memory operand

        bool hasSIB = (rm == 4 && mod != 3);
        if (hasSIB) size++;

        if (mod == 0)
        {
            if (rm == 5) size += 4; // [RIP+disp32] or [disp32]
            else if (hasSIB && (ip[1] & 7) == 5) size += 4; // [disp32 + index*scale]
        }
        else if (mod == 1)
        {
            size += 1; // disp8
        }
        else if (mod == 2)
        {
            size += 4; // disp32
        }

        return size;
    }

    /// <summary>
    /// Write a 14-byte absolute jump: mov rax, addr; jmp rax; nop; nop
    /// </summary>
    /// <summary>
    /// Install a hook that uses patch/unpatch to call the original.
    /// Returns a HookContext pointer that must be used with CallOriginal.
    /// For functions with security cookies that can't use a trampoline.
    /// </summary>
    public static nint InstallPatchUnpatch(nint target, nint detour)
    {
        if (target == 0 || detour == 0)
            throw new ArgumentException("Target and detour must be non-zero.");

        byte* src = (byte*)target;

        // We need at least 14 bytes for the jump patch
        // Save the original bytes
        byte[] saved = new byte[MinPatchSize];
        Marshal.Copy(target, saved, 0, MinPatchSize);

        // Store the context (target + saved bytes) in a managed object
        var ctx = new HookContext { Target = target, Detour = detour, SavedBytes = saved };
        nint ctxHandle = (nint)System.Runtime.InteropServices.GCHandle.Alloc(ctx);

        // Make target writable permanently (for patch/unpatch)
        NativeMethods.VirtualProtect(target, (nuint)MinPatchSize, NativeMethods.PAGE_EXECUTE_READWRITE, out _);

        // Patch target with jump to detour
        WriteAbsoluteJump(target, detour);
        NativeMethods.FlushInstructionCache(NativeMethods.GetCurrentProcess(), target, (nuint)MinPatchSize);

        return ctxHandle;
    }

    /// <summary>
    /// Temporarily restores original bytes, calls the original function, then re-patches.
    /// Thread-safe via lock. Returns the GCHandle as a nint.
    /// </summary>
    internal static void CallOriginalVoid3(nint ctxHandle, nint arg1, nint arg2, nint arg3)
    {
        var ctx = (HookContext)System.Runtime.InteropServices.GCHandle.FromIntPtr(ctxHandle).Target!;
        lock (ctx)
        {
            // Restore original bytes
            Marshal.Copy(ctx.SavedBytes, 0, ctx.Target, MinPatchSize);
            NativeMethods.FlushInstructionCache(NativeMethods.GetCurrentProcess(), ctx.Target, (nuint)MinPatchSize);

            // Call original
            var fn = (delegate* unmanaged<nint, nint, nint, void>)ctx.Target;
            fn(arg1, arg2, arg3);

            // Re-patch
            // Recalculate jump in case (though target doesn't change)
            // Actually we just need to restore the jump we wrote before
            // We don't have the detour address stored... let's store it
            WriteAbsoluteJump(ctx.Target, ctx.Detour);
            NativeMethods.FlushInstructionCache(NativeMethods.GetCurrentProcess(), ctx.Target, (nuint)MinPatchSize);
        }
    }

    internal class HookContext
    {
        public nint Target;
        public nint Detour;
        public byte[] SavedBytes = [];
        // lock object is 'this'
    }

    /// <summary>
    /// Allocate executable memory within ±2GB of the target address.
    /// Searches downward then upward in 64KB-aligned increments.
    /// </summary>
    private static nint AllocateNear(nint target, nuint size)
    {
        const long range = 0x7FFF0000L; // just under 2GB
        const long step = 0x10000; // 64KB allocation granularity

        long lo = Math.Max((long)target - range, 0x10000);
        long hi = Math.Min((long)target + range, 0x7FFFFFFEFFFF);

        // Search downward from target
        for (long addr = ((long)target - step) & ~(step - 1); addr >= lo; addr -= step)
        {
            nint result = NativeMethods.VirtualAlloc(
                (nint)addr, size,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
                NativeMethods.PAGE_EXECUTE_READWRITE);
            if (result != 0) return result;
        }

        // Search upward from target
        for (long addr = ((long)target + step) & ~(step - 1); addr <= hi; addr += step)
        {
            nint result = NativeMethods.VirtualAlloc(
                (nint)addr, size,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
                NativeMethods.PAGE_EXECUTE_READWRITE);
            if (result != 0) return result;
        }

        return 0;
    }

    private static string HexBytes(byte* p, int count)
    {
        var sb = new System.Text.StringBuilder(count * 2);
        for (int i = 0; i < count; i++)
            sb.Append(p[i].ToString("X2"));
        return sb.ToString();
    }

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
