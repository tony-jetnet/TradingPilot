# Extract ALL string references from getHeadMd5Sign (full function, ~1KB)
$dllPath = 'C:\Program Files (x86)\Webull Desktop\wbgrpc.dll'
$bytes = [System.IO.File]::ReadAllBytes($dllPath)
$funcOffset = 0x6D4A0
$rdataBase = 0x591000
$rdataRaw = 0x58F800
$textBase = 0x1000
$textRaw = 0x400
$dataBase = 0x76E000
$dataRaw = 0x76BA00
$funcRVA = 0x6E0A0

# Scan 1024 bytes for ALL RIP-relative references
[console]::WriteLine("=== All RIP-relative references in getHeadMd5Sign ===")
$funcBytes = $bytes[$funcOffset..($funcOffset + 1023)]

for ($i = 0; $i -lt 1010; $i++) {
    # LEA patterns: 48/4C 8D with modrm indicating [rip+disp32]
    if (($funcBytes[$i] -eq 0x48 -or $funcBytes[$i] -eq 0x4C) -and $funcBytes[$i+1] -eq 0x8D) {
        $modrm = $funcBytes[$i+2]
        if (($modrm -band 0xC7) -eq 0x05) {  # mod=00, rm=101 = RIP-relative
            $disp = [BitConverter]::ToInt32($funcBytes, $i + 3)
            $instrRVA = $funcRVA + $i + 7
            $targetRVA = $instrRVA + $disp
            $reg = ($modrm -shr 3) -band 7
            if ($funcBytes[$i] -eq 0x4C) { $reg += 8 }
            $regNames = @('rax','rcx','rdx','rbx','rsp','rbp','rsi','rdi','r8','r9','r10','r11','r12','r13','r14','r15')

            $targetFile = 0
            if ($targetRVA -ge $rdataBase) { $targetFile = $rdataRaw + ($targetRVA - $rdataBase) }
            elseif ($targetRVA -ge $dataBase) { $targetFile = $dataRaw + ($targetRVA - $dataBase) }
            elseif ($targetRVA -ge $textBase) { $targetFile = $textRaw + ($targetRVA - $textBase) }

            $str = ""
            if ($targetFile -gt 0 -and $targetFile -lt ($bytes.Length - 100)) {
                # Try ASCII
                $raw = $bytes[$targetFile..($targetFile + 99)]
                $ascii = [System.Text.Encoding]::ASCII.GetString($raw)
                $end = $ascii.IndexOf([char]0)
                if ($end -gt 0) { $ascii = $ascii.Substring(0, $end) }
                if ($ascii.Length -ge 2 -and $ascii -match '^[\x20-\x7E]+$') {
                    $str = "  str='$ascii'"
                }
                # Try UTF-16 if no ASCII
                if (-not $str) {
                    $u16 = [System.Text.Encoding]::Unicode.GetString($raw)
                    $end = $u16.IndexOf([char]0)
                    if ($end -gt 0) { $u16 = $u16.Substring(0, $end) }
                    if ($u16.Length -ge 2 -and $u16 -match '^[\x20-\x7E]+$') {
                        $str = "  u16='$u16'"
                    }
                }
            }

            [console]::WriteLine("+0x$($i.ToString('X3')): LEA $($regNames[$reg]), [rip+0x$($disp.ToString('X'))] -> 0x$($targetRVA.ToString('X'))$str")
        }
    }

    # Also check for CALL [rip+disp32] patterns: FF 15 xx xx xx xx
    if ($funcBytes[$i] -eq 0xFF -and $funcBytes[$i+1] -eq 0x15) {
        $disp = [BitConverter]::ToInt32($funcBytes, $i + 2)
        $instrRVA = $funcRVA + $i + 6
        $targetRVA = $instrRVA + $disp

        # This is an indirect call through a pointer - the target is a function pointer in .rdata
        $targetFile = 0
        if ($targetRVA -ge $rdataBase) { $targetFile = $rdataRaw + ($targetRVA - $rdataBase) }

        $funcName = ""
        if ($targetFile -gt 0 -and $targetFile -lt ($bytes.Length - 8)) {
            # Read the function pointer value (but we can't resolve it without relocations)
            $funcName = " (import)"
        }

        [console]::WriteLine("+0x$($i.ToString('X3')): CALL [rip+0x$($disp.ToString('X'))] -> IAT 0x$($targetRVA.ToString('X'))$funcName")
    }
}

# Also extract the full ASCII readout of the function
[console]::WriteLine("`n=== Function hex dump (first 512 bytes) ===")
for ($j = 0; $j -lt 512; $j += 16) {
    $off = $funcOffset + $j
    $hex = ($bytes[$off..($off+15)] | ForEach-Object { $_.ToString('X2') }) -join ' '
    [console]::WriteLine("$($j.ToString('X3')): $hex")
}

[console]::WriteLine("Done")
