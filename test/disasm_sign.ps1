# Extract the raw bytes of getHeadMd5Sign from wbgrpc.dll
# We know from the hook log: wbgrpc.dll base=0x7FFCE09D0000, getHeadMd5Sign at 0x7FFCE0A3E0A0
# Offset = 0x7FFCE0A3E0A0 - 0x7FFCE09D0000 = 0x6E0A0

$dllPath = 'C:\Program Files (x86)\Webull Desktop\wbgrpc.dll'
$bytes = [System.IO.File]::ReadAllBytes($dllPath)

# Get the PE offset to convert RVA to file offset
# PE header is at offset stored in bytes[0x3C..0x3F]
$peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
[console]::WriteLine("PE header at: 0x$($peOffset.ToString('X'))")

# Number of sections
$numSections = [BitConverter]::ToInt16($bytes, $peOffset + 6)
$optHeaderSize = [BitConverter]::ToInt16($bytes, $peOffset + 20)
$sectionTableOffset = $peOffset + 24 + $optHeaderSize

[console]::WriteLine("Sections: $numSections, section table at: 0x$($sectionTableOffset.ToString('X'))")

# ImageBase from optional header (offset 24 in PE64)
$imageBase = [BitConverter]::ToInt64($bytes, $peOffset + 24 + 24)
[console]::WriteLine("ImageBase: 0x$($imageBase.ToString('X'))")

# Find .text section to map RVA to file offset
for ($i = 0; $i -lt $numSections; $i++) {
    $off = $sectionTableOffset + $i * 40
    $name = [System.Text.Encoding]::ASCII.GetString($bytes, $off, 8).TrimEnd([char]0)
    $virtualSize = [BitConverter]::ToInt32($bytes, $off + 8)
    $virtualAddr = [BitConverter]::ToInt32($bytes, $off + 12)
    $rawSize = [BitConverter]::ToInt32($bytes, $off + 16)
    $rawAddr = [BitConverter]::ToInt32($bytes, $off + 20)
    [console]::WriteLine("  Section '$name': VA=0x$($virtualAddr.ToString('X')) Size=0x$($virtualSize.ToString('X')) RawAddr=0x$($rawAddr.ToString('X'))")

    # Check if our RVA falls in this section
    $rva = 0x6E0A0  # getHeadMd5Sign RVA from hook log offset
    if ($rva -ge $virtualAddr -and $rva -lt ($virtualAddr + $virtualSize)) {
        $fileOffset = $rawAddr + ($rva - $virtualAddr)
        [console]::WriteLine("  ** getHeadMd5Sign is in section '$name' at file offset 0x$($fileOffset.ToString('X'))")

        # Extract 512 bytes of the function
        $funcBytes = $bytes[$fileOffset..($fileOffset + 511)]

        # Hex dump
        [console]::WriteLine("`n=== getHeadMd5Sign raw bytes (first 256) ===")
        for ($j = 0; $j -lt 256; $j += 16) {
            $hex = ($funcBytes[$j..($j+15)] | ForEach-Object { $_.ToString('X2') }) -join ' '
            $ascii = ($funcBytes[$j..($j+15)] | ForEach-Object { if ($_ -ge 0x20 -and $_ -le 0x7E) { [char]$_ } else { '.' } }) -join ''
            [console]::WriteLine("  $($($fileOffset+$j).ToString('X8')): $hex  $ascii")
        }

        # Also look for getMd5 function nearby - search for the string "getMd5" reference
        # and any hardcoded salt/secret strings near the function
        [console]::WriteLine("`n=== Nearby ASCII strings (within 4KB) ===")
        $region = $bytes[($fileOffset-2048)..($fileOffset+2048)]
        $regionText = [System.Text.Encoding]::ASCII.GetString($region)
        $m = [regex]::Matches($regionText, '[\x20-\x7E]{4,}')
        $m.Value | Sort-Object -Unique | ForEach-Object { [console]::WriteLine("  $_") }
    }
}

[console]::WriteLine("`nDone")
