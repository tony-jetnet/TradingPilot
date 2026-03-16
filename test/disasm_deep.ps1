# Deep manual disassembly of getHeadMd5Sign
# Focus on understanding the string building logic between the key lookups
# Key offsets from earlier analysis:
# +0x033: LEA rdx, secret string 'u556uuu7qflha9xt'
# +0x03E: CALL to construct initial QString from secret
# +0x055: LEA rcx, 'ver' -> lookup ver in QMap
# +0x0C8: CALL (some operation with ver value)
# +0x103: LEA rcx, 'appid' -> lookup appid
# +0x178: CALL (operation with appid value)
# +0x187: CALL (another function - maybe QString::append?)
# +0x2EB: LEA rcx, 'device-type' -> lookup
# +0x4DB: LEA rcx, 'did' -> lookup

# The function at +0x187 imports from IAT 0x5917A0
# Let me check what function that import resolves to

$bytes = [System.IO.File]::ReadAllBytes('C:\Program Files (x86)\Webull Desktop\wbgrpc.dll')
$rdataRaw = 0x58F800
$rdataBase = 0x591000

# Check IAT entries to identify the called functions
# The IAT is in .rdata. Each entry is a pointer to an import name thunk.
# Let me look at what's at IAT addresses 0x5917A0, 0x591888, 0x5918A8, 0x591898, etc.

$iats = @(
    @{addr=0x5917A0; desc="called at +0x187, +0x377"},
    @{addr=0x5917B0; desc="called at +0x2B1"},
    @{addr=0x591798; desc="called at +0x27F"},
    @{addr=0x591888; desc="called at +0x1B2, +0x39C"},
    @{addr=0x5918A8; desc="called at +0x1DC, +0x3C6"},
    @{addr=0x591898; desc="called at +0x210"},
    @{addr=0x5919B8; desc="called at +0x05C, +0x10A, +0x19A, +0x2F2, +0x38A - QMap lookup"},
    @{addr=0x5919D0; desc="called at +0x089, +0x0B3, +0x139, +0x163, etc - tree traversal"},
    @{addr=0x5919E0; desc="called at +0x0D4, +0x0DF, etc - cleanup"},
    @{addr=0x5919E8; desc="called at +0x0C8, +0x178, +0x368 - copy/assign value"},
    @{addr=0x5919F0; desc="called at +0x048, +0x0F5, +0x2DD - construct/init"},
)

# For PE imports, the IAT at runtime contains function pointers.
# But in the file, the IAT contains RVAs to import name entries.
# Import Name Table entry: hint(2 bytes) + name(null-terminated string)

foreach ($iat in $iats) {
    $fileOff = $rdataRaw + ($iat.addr - $rdataBase)
    if ($fileOff -lt 0 -or $fileOff -ge $bytes.Length - 8) { continue }

    # Read the RVA stored at this IAT entry
    $rva = [BitConverter]::ToInt64($bytes, $fileOff)

    # Check if this is an RVA to an import name (high bit clear = by name)
    if ($rva -gt 0 -and $rva -lt 0x800000 -and ($rva -band 0x8000000000000000) -eq 0) {
        $nameOff = $rdataRaw + ([int]$rva - $rdataBase)
        if ($nameOff -gt 0 -and $nameOff -lt $bytes.Length - 100) {
            # Skip 2-byte hint, read name
            $name = [System.Text.Encoding]::ASCII.GetString($bytes, $nameOff + 2, 100)
            $end = $name.IndexOf([char]0)
            if ($end -gt 0) { $name = $name.Substring(0, $end) }
            [console]::WriteLine("IAT 0x$($iat.addr.ToString('X')): '$name'  ($($iat.desc))")
        }
    } else {
        [console]::WriteLine("IAT 0x$($iat.addr.ToString('X')): RVA=0x$($rva.ToString('X')) (ordinal?)  ($($iat.desc))")
    }
}

[console]::WriteLine("`nDone")
