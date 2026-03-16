# WebullApiTests.ps1 — Test all Webull API endpoints
# Usage: powershell -File test/WebullApiTests.ps1

$ErrorActionPreference = 'Continue'

# Load auth from persisted file
$authPath = "$env:LOCALAPPDATA\WebullHook\auth_header.json"
$auth = @{}
if (Test-Path $authPath) {
    $auth = Get-Content $authPath | ConvertFrom-Json
    Write-Host "[OK] Auth loaded: did=$($auth.did), token=$($auth.access_token.Substring(0,20))..." -ForegroundColor Green
} else {
    Write-Host "[WARN] No auth file at $authPath" -ForegroundColor Yellow
}

$did = $auth.did
$token = $auth.access_token

# Known ticker IDs
$AMD_ID = 913254235
$RKLB_ID = 950178054

$passed = 0
$failed = 0

function Test-Endpoint {
    param(
        [string]$Name,
        [string]$Url,
        [hashtable]$Headers = @{},
        [scriptblock]$Validate
    )
    Write-Host ""
    Write-Host "=== $Name ===" -ForegroundColor Cyan
    Write-Host "  URL: $Url"
    try {
        $response = Invoke-WebRequest -Uri $Url -Headers $Headers -UseBasicParsing -TimeoutSec 10
        $status = $response.StatusCode
        $body = $response.Content
        $json = $null
        try { $json = $body | ConvertFrom-Json } catch {}

        Write-Host "  Status: $status" -ForegroundColor $(if ($status -eq 200) { 'Green' } else { 'Yellow' })
        Write-Host "  Body length: $($body.Length) chars"

        if ($json) {
            $preview = $body.Substring(0, [Math]::Min($body.Length, 300))
            Write-Host "  Preview: $preview"
        }

        if ($Validate) {
            $result = & $Validate $json $body
            if ($result) {
                Write-Host "  PASS: $result" -ForegroundColor Green
                $script:passed++
            } else {
                Write-Host "  FAIL: validation returned false" -ForegroundColor Red
                $script:failed++
            }
        } else {
            $script:passed++
        }
    } catch {
        Write-Host "  ERROR: $($_.Exception.Message)" -ForegroundColor Red
        # Try to get response body
        if ($_.Exception.Response) {
            $reader = [System.IO.StreamReader]::new($_.Exception.Response.GetResponseStream())
            $errBody = $reader.ReadToEnd()
            Write-Host "  Response body: $errBody" -ForegroundColor Red
        }
        $script:failed++
    }
}

$webHeaders = @{
    'did' = $did
    'access_token' = $token
    'app' = 'global'
    'appid' = 'wb_web_app'
    'device-type' = 'Web'
    'platform' = 'web'
    'hl' = 'en'
}

$desktopHeaders = @{
    'did' = $did
    'access_token' = $token
    'app' = 'ca'
    'appid' = 'wb_desktop'
    'device-type' = 'Windows'
    'platform' = 'qt'
    'hl' = 'en'
    'ver' = '8.19.9'
}

$minimalHeaders = @{ 'did' = $did }

Write-Host "`n========================================" -ForegroundColor White
Write-Host "  WEBULL API ENDPOINT TESTS" -ForegroundColor White
Write-Host "========================================" -ForegroundColor White

# ── SEARCH ──
Test-Endpoint -Name "Search: AMD (web headers)" `
    -Url "https://quotes-gw.webullfintech.com/api/search/pc/tickers?keyword=AMD&pageIndex=1&pageSize=20" `
    -Headers $webHeaders `
    -Validate { param($j,$b) if ($j.data.Count -gt 0) { "Found $($j.data.Count) results" } }

# ── CHARTS - DAILY ──
Test-Endpoint -Name "Chart: AMD daily count=20 (web headers)" `
    -Url "https://quotes-gw.webullfintech.com/api/quote/charts/query?tickerIds=$AMD_ID&type=d1&count=20&extendTrading=0" `
    -Headers $webHeaders `
    -Validate { param($j,$b) "Got $($j[0].data.Count) bars" }

Test-Endpoint -Name "Chart: AMD daily count=20 (desktop headers)" `
    -Url "https://quotes-gw.webullfintech.com/api/quote/charts/query?tickerIds=$AMD_ID&type=d1&count=20&extendTrading=0" `
    -Headers $desktopHeaders `
    -Validate { param($j,$b) "Got $($j[0].data.Count) bars" }

Test-Endpoint -Name "Chart: AMD daily count=20 (minimal headers)" `
    -Url "https://quotes-gw.webullfintech.com/api/quote/charts/query?tickerIds=$AMD_ID&type=d1&count=20&extendTrading=0" `
    -Headers $minimalHeaders `
    -Validate { param($j,$b) "Got $($j[0].data.Count) bars" }

Test-Endpoint -Name "Chart: AMD daily count=20 (NO headers)" `
    -Url "https://quotes-gw.webullfintech.com/api/quote/charts/query?tickerIds=$AMD_ID&type=d1&count=20&extendTrading=0" `
    -Validate { param($j,$b) "Got $($j[0].data.Count) bars" }

# ── CHARTS - INTRADAY ──
Test-Endpoint -Name "Chart: AMD m1 count=2000 (web)" `
    -Url "https://quotes-gw.webullfintech.com/api/quote/charts/query?tickerIds=$AMD_ID&type=m1&count=2000&extendTrading=0" `
    -Headers $webHeaders `
    -Validate { param($j,$b) "Got $($j[0].data.Count) bars" }

Test-Endpoint -Name "Chart: AMD m5 count=2000 (web)" `
    -Url "https://quotes-gw.webullfintech.com/api/quote/charts/query?tickerIds=$AMD_ID&type=m5&count=2000&extendTrading=0" `
    -Headers $webHeaders `
    -Validate { param($j,$b) "Got $($j[0].data.Count) bars" }

# ── CHARTS - extendTrading=1 ──
Test-Endpoint -Name "Chart: AMD daily count=20 extendTrading=1" `
    -Url "https://quotes-gw.webullfintech.com/api/quote/charts/query?tickerIds=$AMD_ID&type=d1&count=20&extendTrading=1" `
    -Headers $webHeaders `
    -Validate { param($j,$b) "Got $($j[0].data.Count) bars" }

Test-Endpoint -Name "Chart: AMD m1 count=2000 extendTrading=1" `
    -Url "https://quotes-gw.webullfintech.com/api/quote/charts/query?tickerIds=$AMD_ID&type=m1&count=2000&extendTrading=1" `
    -Headers $webHeaders `
    -Validate { param($j,$b) "Got $($j[0].data.Count) bars" }

# ── DEPTH / L2 ──
Test-Endpoint -Name "Depth: AMD (quotes-gw, web)" `
    -Url "https://quotes-gw.webullfintech.com/api/stock/tickerRealTime/getDepth?tickerId=$AMD_ID" `
    -Headers $webHeaders `
    -Validate { param($j,$b) "Response: $($b.Substring(0, [Math]::Min($b.Length, 200)))" }

# ── QUOTE / REALTIME ──
Test-Endpoint -Name "Quote realtime: AMD (bgw)" `
    -Url "https://quotes-gw.webullfintech.com/api/bgw/quote/realtime?tickerIds=$AMD_ID" `
    -Headers $webHeaders `
    -Validate { param($j,$b) "Response: $($b.Substring(0, [Math]::Min($b.Length, 200)))" }

Test-Endpoint -Name "Quote microTrend: AMD" `
    -Url "https://quotes-gw.webullfintech.com/api/bgw/quote/microTrend?tickerIds=$AMD_ID" `
    -Headers $webHeaders `
    -Validate { param($j,$b) "Response: $($b.Substring(0, [Math]::Min($b.Length, 200)))" }

# ── NEWS ──
Test-Endpoint -Name "News: AMD (quotes-gw domain)" `
    -Url "https://quotes-gw.webullfintech.com/api/information/news/tickerNewses/v9?tickerId=$AMD_ID&currentNewsId=0&pageSize=20" `
    -Headers $webHeaders `
    -Validate { param($j,$b) "Response: $($b.Substring(0, [Math]::Min($b.Length, 200)))" }

Test-Endpoint -Name "News: AMD (infoapi domain)" `
    -Url "https://infoapi.webullfintech.com/api/information/news/tickerNewses/v9?tickerId=$AMD_ID&currentNewsId=0&pageSize=20" `
    -Headers $webHeaders `
    -Validate { param($j,$b) "Response: $($b.Substring(0, [Math]::Min($b.Length, 200)))" }

Test-Endpoint -Name "News: AMD (securitiesapi domain)" `
    -Url "https://securitiesapi.webullfintech.com/api/information/news/tickerNewses/v9?tickerId=$AMD_ID&currentNewsId=0&pageSize=20" `
    -Headers $webHeaders `
    -Validate { param($j,$b) "Response: $($b.Substring(0, [Math]::Min($b.Length, 200)))" }

# Try alternate news endpoints
Test-Endpoint -Name "News: AMD (quotes-gw, /v5 path)" `
    -Url "https://quotes-gw.webullfintech.com/api/information/news/tickerNews?tickerId=$AMD_ID&currentNewsId=0&pageSize=20" `
    -Headers $webHeaders `
    -Validate { param($j,$b) "Response: $($b.Substring(0, [Math]::Min($b.Length, 200)))" }

# ── FINANCIALS / ANALYSIS ──
Test-Endpoint -Name "Ticker detail: AMD" `
    -Url "https://quotes-gw.webullfintech.com/api/bgw/ticker/detail?tickerId=$AMD_ID" `
    -Headers $webHeaders `
    -Validate { param($j,$b) "Response: $($b.Substring(0, [Math]::Min($b.Length, 200)))" }

# ── ALTERNATIVE CHART ENDPOINTS ──
Test-Endpoint -Name "Chart: AMD kdata/d1 (alt endpoint)" `
    -Url "https://quotes-gw.webullfintech.com/api/quote/tickerChartDatas/v5/d1?tickerId=$AMD_ID&count=20" `
    -Headers $webHeaders `
    -Validate { param($j,$b) "Response: $($b.Substring(0, [Math]::Min($b.Length, 200)))" }

Test-Endpoint -Name "Chart: AMD charts/v3 (alt endpoint)" `
    -Url "https://quotes-gw.webullfintech.com/api/quote/charts/v3/query?tickerIds=$AMD_ID&type=d1&count=20&extendTrading=0" `
    -Headers $webHeaders `
    -Validate { param($j,$b) "Response: $($b.Substring(0, [Math]::Min($b.Length, 200)))" }

# ── SUMMARY ──
Write-Host "`n========================================" -ForegroundColor White
Write-Host "  RESULTS: $passed passed, $failed failed" -ForegroundColor $(if ($failed -eq 0) { 'Green' } else { 'Yellow' })
Write-Host "========================================" -ForegroundColor White
