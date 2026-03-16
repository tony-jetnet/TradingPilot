# Extract protobuf file descriptors from wbgrpc.dll
# Protobuf FileDescriptorProto starts with field 1 (name, string) containing .proto filename
$bytes = [System.IO.File]::ReadAllBytes('C:\Program Files (x86)\Webull Desktop\wbgrpc.dll')

# Search for "quote_grpc_server.proto" and "gateway" descriptor patterns
$text = [System.Text.Encoding]::ASCII.GetString($bytes)

# Find positions of key proto file names
$patterns = @('quote_grpc_server.proto', 'chart.proto', 'realtime.proto', 'micro_trend.proto')
foreach ($pat in $patterns) {
    $idx = $text.IndexOf($pat)
    if ($idx -ge 0) {
        # Walk backwards to find the FileDescriptorProto start (0x0A byte followed by length)
        $start = $idx
        for ($i = $idx - 1; $i -ge [Math]::Max(0, $idx - 10); $i--) {
            if ($bytes[$i] -eq 0x0A) {
                $len = $bytes[$i + 1]
                if ($len -eq $pat.Length) {
                    $start = $i
                    break
                }
            }
        }

        # Read ~2KB from this position and hex dump first 200 bytes
        $chunk = $bytes[$start..([Math]::Min($start + 2000, $bytes.Length - 1))]
        $hex = [BitConverter]::ToString($chunk[0..199]) -replace '-',' '
        [console]::WriteLine("=== $pat at offset $start ===")
        [console]::WriteLine("Hex: $hex")

        # Also show ASCII printable chars from the chunk
        $ascii = ($chunk | ForEach-Object { if ($_ -ge 0x20 -and $_ -le 0x7E) { [char]$_ } else { '.' } }) -join ''
        [console]::WriteLine("ASCII: $($ascii.Substring(0, [Math]::Min($ascii.Length, 300)))")
        [console]::WriteLine("")
    }
}

# Also look for "gateway.v1" descriptor
$gwIdx = $text.IndexOf('gateway.v1"')
if ($gwIdx -ge 0) {
    # Walk back further to find start
    $start = $gwIdx
    for ($i = $gwIdx - 1; $i -ge [Math]::Max(0, $gwIdx - 200); $i--) {
        if ($bytes[$i] -eq 0x0A) {
            $nameLen = $bytes[$i + 1]
            $nameBytes = $bytes[($i+2)..($i+1+$nameLen)]
            $name = [System.Text.Encoding]::ASCII.GetString($nameBytes)
            if ($name -match '\.proto') {
                $start = $i
                [console]::WriteLine("Found proto file descriptor at offset ${i}: name='${name}'")
                break
            }
        }
    }

    $chunk = $bytes[$start..([Math]::Min($start + 500, $bytes.Length - 1))]
    $ascii = ($chunk | ForEach-Object { if ($_ -ge 0x20 -and $_ -le 0x7E) { [char]$_ } else { '.' } }) -join ''
    [console]::WriteLine("=== gateway descriptor at $start ===")
    [console]::WriteLine("ASCII: $($ascii.Substring(0, [Math]::Min($ascii.Length, 500)))")
}

[console]::WriteLine("Done")
