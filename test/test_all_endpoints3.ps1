$AMD = 913254235
$auth = Get-Content "$env:LOCALAPPDATA\WebullHook\auth_header.json" | ConvertFrom-Json
$h = @{
    'did'=$auth.did; 'access_token'=$auth.access_token
    'app'='global'; 'appid'='wb_web_app'; 'platform'='web'
    'hl'='en'; 'ver'='8.19.9'
}
$base = 'https://quotes-gw.webullfintech.com'

$endpoints = @(
    # --- bgw prefix (found in DLL) ---
    @{name='bgw quote realtime'; url="/api/bgw/quote/realtime?tickerIds=$AMD&includeSecu=1&delay=0&more=1"},
    @{name='bgw quote realtime POST'; url="/api/bgw/quote/realtime"; method='POST'; body="{`"tickerIds`":[$AMD],`"includeSecu`":1}"},
    @{name='bgw microTrend'; url="/api/bgw/quote/microTrend?tickerIds=$AMD"},
    @{name='bgw ticker detail'; url="/api/bgw/ticker/detail?tickerId=$AMD"},
    @{name='bgw stock brief'; url="/api/bgw/stock/brief?tickerId=$AMD"},
    @{name='bgw option expiry'; url="/api/bgw/option/expiration/list?tickerId=$AMD"},
    @{name='bgw option chain'; url="/api/bgw/option/chain/query?tickerId=$AMD"},

    # --- information prefix ---
    @{name='info news general'; url="/api/information/news/v5/listMultiSource?pageSize=10"},
    @{name='info news flash'; url="/api/information/flash/news?pageSize=10"},
    @{name='info calendar earnings'; url="/api/information/calendar/earnings?regionId=6&pageSize=20"},
    @{name='info calendar ipo'; url="/api/information/calendar/ipo?regionId=6&pageSize=20"},
    @{name='info calendar economic'; url="/api/information/calendar/economic?regionId=6&pageSize=20"},
    @{name='info calendar dividend'; url="/api/information/calendar/dividend?regionId=6&pageSize=20"},
    @{name='info calendar split'; url="/api/information/calendar/split?regionId=6&pageSize=20"},

    # --- analysis / research ---
    @{name='analyst rating ticker'; url="/api/information/analyst/rating?tickerId=$AMD"},
    @{name='analyst forecast ticker'; url="/api/information/analyst/forecast?tickerId=$AMD"},
    @{name='analyst upgrade'; url="/api/information/analyst/upgradeDowngrade?tickerId=$AMD&pageSize=10"},
    @{name='analyst target'; url="/api/information/analyst/target?tickerId=$AMD"},

    # --- ticker data ---
    @{name='ticker financials'; url="/api/information/financial/index?tickerId=$AMD"},
    @{name='ticker balance sheet'; url="/api/information/financial/balancesheet?tickerId=$AMD"},
    @{name='ticker income'; url="/api/information/financial/incomestatement?tickerId=$AMD"},
    @{name='ticker cashflow'; url="/api/information/financial/cashflow?tickerId=$AMD"},
    @{name='ticker profile'; url="/api/information/stock/profile?tickerId=$AMD"},
    @{name='ticker short interest'; url="/api/information/stock/shortInterest?tickerId=$AMD"},
    @{name='ticker insider'; url="/api/information/stock/insiderActivity?tickerId=$AMD&pageSize=10"},
    @{name='ticker institutional'; url="/api/information/stock/institutionalHolding?tickerId=$AMD"},
    @{name='ticker etf holding'; url="/api/information/stock/etfHolding?tickerId=$AMD"},
    @{name='ticker dividend'; url="/api/information/stock/dividend?tickerId=$AMD"},
    @{name='ticker earnings'; url="/api/information/financial/earnings?tickerId=$AMD&count=10"},
    @{name='ticker eps estimate'; url="/api/information/financial/epsEstimate?tickerId=$AMD"},
    @{name='ticker revenue est'; url="/api/information/financial/revenueEstimate?tickerId=$AMD"},
    @{name='ticker sec filings'; url="/api/information/sec/filings?tickerId=$AMD&pageSize=10"},
    @{name='ticker corp actions'; url="/api/information/stock/corporateActions?tickerId=$AMD"},

    # --- market data ---
    @{name='market overview'; url="/api/information/market/overview?regionId=6"},
    @{name='market sector'; url="/api/information/market/sectorPerformance?regionId=6"},
    @{name='market gainers'; url="/api/information/market/gainers?regionId=6&count=20"},
    @{name='market losers'; url="/api/information/market/losers?regionId=6&count=20"},
    @{name='market active'; url="/api/information/market/active?regionId=6&count=20"}
)

$working = 0
$empty = 0
$failed = 0

foreach ($ep in $endpoints) {
    $url = $base + $ep.url
    try {
        if ($ep.method -eq 'POST') {
            $r = Invoke-WebRequest -Uri $url -Method POST -Headers $h -Body $ep.body -ContentType 'application/json' -UseBasicParsing -TimeoutSec 8
        } else {
            $r = Invoke-WebRequest -Uri $url -Headers $h -UseBasicParsing -TimeoutSec 8
        }
        $len = $r.Content.Length
        if ($len -gt 5) {
            $preview = $r.Content.Substring(0, [Math]::Min($len, 120)) -replace "`n",' '
            [console]::WriteLine("[OK ${len}B] $($ep.name): $preview")
            $working++
        } else {
            [console]::WriteLine("[EMPTY] $($ep.name): $($r.Content)")
            $empty++
        }
    } catch {
        $status = $_.Exception.Response.StatusCode.value__
        [console]::WriteLine("[${status}] $($ep.name)")
        $failed++
    }
}

[console]::WriteLine("`n=== SUMMARY: $working working, $empty empty, $failed failed ===")
