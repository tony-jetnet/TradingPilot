# Deeper analysis of getHeadMd5Sign - follow the string references
$dllPath = 'C:\Program Files (x86)\Webull Desktop\wbgrpc.dll'
$bytes = [System.IO.File]::ReadAllBytes($dllPath)
$funcOffset = 0x6D4A0
$textBase = 0x1000      # .text VA
$textRaw = 0x400         # .text raw offset
$rdataBase = 0x591000    # .rdata VA
$rdataRaw = 0x58F800     # .rdata raw offset

# The function has LEA instructions referencing strings
# At offset 0x1C (funcOffset+0x1C): 4C 8D 05 CD 98 73 00 = LEA r8, [rip + 0x7398CD]
# RIP at that point = funcVA + 0x1C + 7 = 0x6E0A0 + 0x23 = 0x6E0C3
# Target = 0x6E0C3 + 0x7398CD = 0x7A7990
# That's in .rdata. File offset = rdataRaw + (0x7A7990 - rdataBase) = 0x58F800 + (0x7A7990 - 0x591000) = 0x58F800 + 0x216990 = 0x806790
# Wait, that's too big. Let me recalculate.

# Function RVA = 0x6E0A0 (from .text VA 0x1000, offset in section = 0x6E0A0 - 0x1000 = 0x6CCA0)
# Actually the hook log showed offset = 0x6E0A0 which is RVA directly

# LEA r8, [rip + 0x7398CD]
$rip1 = 0x6E0A0 + 0x1C + 7  # instruction at funcRVA+0x1C, size 7 bytes
$target1 = $rip1 + 0x7398CD
$fileTarget1 = $rdataRaw + ($target1 - $rdataBase)
[console]::WriteLine("LEA r8 target RVA=0x$($target1.ToString('X')), file=0x$($fileTarget1.ToString('X'))")
if ($fileTarget1 -gt 0 -and $fileTarget1 -lt $bytes.Length - 100) {
    $str1 = [System.Text.Encoding]::Unicode.GetString($bytes, $fileTarget1, 200)
    $end = $str1.IndexOf([char]0)
    if ($end -gt 0) { $str1 = $str1.Substring(0, $end) }
    [console]::WriteLine("  String (UTF-16): '$str1'")
}

# LEA rdx, [rip + 0x53162E]  at funcOffset+0x24: 48 8D 15 2E 16 53 00
$rip2 = 0x6E0A0 + 0x24 + 7
$target2 = $rip2 + 0x53162E
$fileTarget2 = $rdataRaw + ($target2 - $rdataBase)
[console]::WriteLine("`nLEA rdx target RVA=0x$($target2.ToString('X')), file=0x$($fileTarget2.ToString('X'))")
if ($fileTarget2 -gt 0 -and $fileTarget2 -lt $bytes.Length - 100) {
    $str2 = [System.Text.Encoding]::Unicode.GetString($bytes, $fileTarget2, 200)
    $end = $str2.IndexOf([char]0)
    if ($end -gt 0) { $str2 = $str2.Substring(0, $end) }
    [console]::WriteLine("  String (UTF-16): '$str2'")

    # Also try ASCII
    $str2a = [System.Text.Encoding]::ASCII.GetString($bytes, $fileTarget2, 100)
    $end = $str2a.IndexOf([char]0)
    if ($end -gt 0) { $str2a = $str2a.Substring(0, $end) }
    [console]::WriteLine("  String (ASCII): '$str2a'")
}

# LEA rcx, [rip + 0x52EA08] at funcOffset+0x4D: 48 8D 0D A8 EA 52 00
$rip3 = 0x6E0A0 + 0x4D + 7
$target3 = $rip3 + 0x52EAA8
$fileTarget3 = $rdataRaw + ($target3 - $rdataBase)
[console]::WriteLine("`nLEA rcx target RVA=0x$($target3.ToString('X')), file=0x$($fileTarget3.ToString('X'))")
if ($fileTarget3 -gt 0 -and $fileTarget3 -lt $bytes.Length - 200) {
    $str3 = [System.Text.Encoding]::Unicode.GetString($bytes, $fileTarget3, 200)
    $end = $str3.IndexOf([char]0)
    if ($end -gt 0) { $str3 = $str3.Substring(0, $end) }
    [console]::WriteLine("  String (UTF-16): '$str3'")
    $str3a = [System.Text.Encoding]::ASCII.GetString($bytes, $fileTarget3, 100)
    $end = $str3a.IndexOf([char]0)
    if ($end -gt 0) { $str3a = $str3a.Substring(0, $end) }
    [console]::WriteLine("  String (ASCII): '$str3a'")
}

# Dump more of the function to find all string references (LEA with [rip+disp32])
[console]::WriteLine("`n=== All RIP-relative LEA instructions in function (first 512 bytes) ===")
$funcBytes = $bytes[$funcOffset..($funcOffset + 511)]
for ($i = 0; $i -lt 500; $i++) {
    # Pattern: 48 8D xx yy yy yy yy (LEA reg, [rip+disp32]) or 4C 8D xx yy yy yy yy
    if (($funcBytes[$i] -eq 0x48 -or $funcBytes[$i] -eq 0x4C) -and $funcBytes[$i+1] -eq 0x8D) {
        $modrm = $funcBytes[$i+2]
        $mod = ($modrm -shr 6) -band 3
        $rm = $modrm -band 7
        if ($mod -eq 0 -and $rm -eq 5) {
            # RIP-relative addressing
            $disp = [BitConverter]::ToInt32($funcBytes, $i + 3)
            $instrRVA = 0x6E0A0 + $i + 7  # RIP points to next instruction
            $targetRVA = $instrRVA + $disp
            $reg = ($modrm -shr 3) -band 7
            $regName = @('rax','rcx','rdx','rbx','rsp','rbp','rsi','rdi','r8','r9','r10','r11','r12','r13','r14','r15')
            $regIdx = $reg
            if ($funcBytes[$i] -eq 0x4C) { $regIdx += 8 }

            # Try to read the target as a string
            $targetFile = 0
            if ($targetRVA -ge $rdataBase -and $targetRVA -lt ($rdataBase + 0x1DC1DE)) {
                $targetFile = $rdataRaw + ($targetRVA - $rdataBase)
            } elseif ($targetRVA -ge $textBase -and $targetRVA -lt ($textBase + 0x58F3D3)) {
                $targetFile = $textRaw + ($targetRVA - $textBase)
            }

            $preview = ""
            if ($targetFile -gt 0 -and $targetFile -lt $bytes.Length - 50) {
                $raw = $bytes[$targetFile..($targetFile + 49)]
                $ascii = [System.Text.Encoding]::ASCII.GetString($raw)
                $end = $ascii.IndexOf([char]0)
                if ($end -gt 0) { $ascii = $ascii.Substring(0, $end) }
                if ($ascii.Length -gt 3 -and $ascii -match '^[\x20-\x7E]+$') {
                    $preview = " str='$ascii'"
                }
            }

            [console]::WriteLine("  +0x$($i.ToString('X3')): LEA $($regName[$regIdx]), [rip+0x$($disp.ToString('X'))] -> RVA 0x$($targetRVA.ToString('X'))$preview")
        }
    }
}

[console]::WriteLine("`nDone")
