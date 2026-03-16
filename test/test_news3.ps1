$AMD = 913254235
$auth = Get-Content "$env:LOCALAPPDATA\WebullHook\auth_header.json" | ConvertFrom-Json

# Test 1: Use ALL captured headers exactly as-is (full desktop identity)
$fullHeaders = @{}
$auth.PSObject.Properties | ForEach-Object {
    if ($_.Name -ne 'content-type' -and $_.Value) {
        $fullHeaders[$_.Name] = $_.Value
    }
}
[console]::WriteLine("Full headers: $($fullHeaders.Keys -join ', ')")

$url = "https://quotes-gw.webullfintech.com/api/information/news/tickerNewses/v9?tickerId=$AMD&currentNewsId=0&pageSize=20"
try {
    $resp = Invoke-WebRequest -Uri $url -Headers $fullHeaders -UseBasicParsing -TimeoutSec 10
    [console]::WriteLine("[Test1 Full desktop headers] $($resp.StatusCode) - $($resp.Content.Length)B: $($resp.Content.Substring(0, [Math]::Min($resp.Content.Length, 200)))")
} catch {
    [console]::WriteLine("[Test1] Error: $($_.Exception.Message)")
}

# Test 2: POST instead of GET
try {
    $body = @{ tickerId = $AMD; currentNewsId = 0; pageSize = 20 } | ConvertTo-Json
    $resp2 = Invoke-WebRequest -Uri $url -Method POST -Headers $fullHeaders -Body $body -ContentType 'application/json' -UseBasicParsing -TimeoutSec 10
    [console]::WriteLine("[Test2 POST] $($resp2.StatusCode) - $($resp2.Content.Length)B: $($resp2.Content.Substring(0, [Math]::Min($resp2.Content.Length, 200)))")
} catch {
    $status = $_.Exception.Response.StatusCode.value__
    [console]::WriteLine("[Test2 POST] Error $status : $($_.Exception.Message)")
}

# Test 3: Try the grpc-style endpoint
try {
    $resp3 = Invoke-WebRequest -Uri "https://quotes-gw.webullfintech.com/api/bgw/quote/realtime?tickerIds=$AMD&includeSecu=1&delay=0&more=1" -Headers $fullHeaders -UseBasicParsing -TimeoutSec 10
    [console]::WriteLine("[Test3 bgw realtime+more] $($resp3.StatusCode) - $($resp3.Content.Length)B: $($resp3.Content.Substring(0, [Math]::Min($resp3.Content.Length, 200)))")
} catch {
    $status = $_.Exception.Response.StatusCode.value__
    [console]::WriteLine("[Test3] Error $status")
}

# Test 4: Try with regionId=3 (Canada) in headers
$caHeaders = @{}
$fullHeaders.Keys | ForEach-Object { $caHeaders[$_] = $fullHeaders[$_] }
$caHeaders['regionId'] = '3'
try {
    $resp4 = Invoke-WebRequest -Uri $url -Headers $caHeaders -UseBasicParsing -TimeoutSec 10
    [console]::WriteLine("[Test4 regionId=3] $($resp4.StatusCode) - $($resp4.Content.Length)B: $($resp4.Content.Substring(0, [Math]::Min($resp4.Content.Length, 200)))")
} catch {
    [console]::WriteLine("[Test4] Error: $($_.Exception.Message)")
}

# Test 5: Try general news (not ticker-specific)
$newsUrls = @(
    "https://quotes-gw.webullfintech.com/api/information/news/tickerNewses/v9?tickerId=$AMD&currentNewsId=0&pageSize=20",
    "https://quotes-gw.webullfintech.com/api/information/news/importantNewsList?regionId=6&pageSize=20",
    "https://quotes-gw.webullfintech.com/api/information/news/importantNewsList?regionId=3&pageSize=20",
    "https://quotes-gw.webullfintech.com/api/information/news/v5/listMultiSource?tickerId=$AMD&type=0&pageSize=20",
    "https://quotes-gw.webullfintech.com/api/information/news/v5/listMultiSource?tickerId=$AMD&type=tickerNews&pageSize=20",
    "https://quotes-gw.webullfintech.com/api/information/news/importantNewsList?pageSize=20",
    "https://quotes-gw.webullfintech.com/api/wlas/news/home?regionId=6&pageSize=20",
    "https://quotes-gw.webullfintech.com/api/wlas/news/ticker/$AMD`?pageSize=20"
)
foreach ($u in $newsUrls) {
    try {
        $r = Invoke-WebRequest -Uri $u -Headers $fullHeaders -UseBasicParsing -TimeoutSec 5
        $len = $r.Content.Length
        if ($len -gt 5) {
            [console]::WriteLine("[HIT $len B] $u")
            [console]::WriteLine("  $($r.Content.Substring(0, [Math]::Min($len, 300)))")
        } else {
            [console]::WriteLine("[empty] $u => $($r.Content)")
        }
    } catch {
        $status = $_.Exception.Response.StatusCode.value__
        [console]::WriteLine("[${status}] $u")
    }
}

[console]::WriteLine("Done")
