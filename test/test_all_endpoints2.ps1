$AMD = 913254235
$auth = Get-Content "$env:LOCALAPPDATA\WebullHook\auth_header.json" | ConvertFrom-Json
$h = @{
    'did'=$auth.did; 'access_token'=$auth.access_token
    'app'='global'; 'appid'='wb_web_app'; 'platform'='web'
    'hl'='en'; 'ver'='8.19.9'
}
$base = 'https://quotes-gw.webullfintech.com'

$endpoints = @(
    # --- Old-style quote endpoints ---
    @{name='Quote getQuote'; url="/api/quote/tickerRealTimes?tickerIds=$AMD&includeSecu=1"},
    @{name='Quote getStockInfo'; url="/api/stock/tickerRealTime/v2/getStockInfo?tickerId=$AMD"},
    @{name='Quote snapshot v5'; url="/api/quote/v5/tickerRealTimes?tickerIds=$AMD"},

    # --- Fundamentals (older paths) ---
    @{name='Fundamental v1'; url="/api/stock/fundamental?tickerId=$AMD"},
    @{name='Financial v1'; url="/api/stock/financial?tickerId=$AMD"},
    @{name='Analysis v1'; url="/api/stock/financial/analysis?tickerId=$AMD"},
    @{name='Brief info'; url="/api/stock/brief/$AMD"},
    @{name='Ticker detail v1'; url="/api/stock/detail/$AMD"},

    # --- Earnings ---
    @{name='Earnings v1'; url="/api/stock/earnings?tickerId=$AMD"},
    @{name='Earnings history'; url="/api/stock/earnings/history?tickerId=$AMD&count=10"},

    # --- Analyst ---
    @{name='Analyst v1'; url="/api/stock/analyst/$AMD"},
    @{name='Forecast v1'; url="/api/stock/forecast/$AMD"},
    @{name='Rating v1'; url="/api/stock/rating/$AMD"},

    # --- Short interest ---
    @{name='Short interest v1'; url="/api/stock/shortInterest?tickerId=$AMD"},
    @{name='Short volume'; url="/api/stock/shortVolume?tickerId=$AMD"},

    # --- Institutional ---
    @{name='Institutional v1'; url="/api/stock/institutional?tickerId=$AMD"},
    @{name='Institutional holder'; url="/api/stock/institutionalHolding?tickerId=$AMD"},

    # --- Options ---
    @{name='Option expiry v1'; url="/api/quote/option/expiration/list?tickerId=$AMD"},
    @{name='Option strategy'; url="/api/quote/option/strategy/list?tickerId=$AMD&type=call&queryAll=0"},
    @{name='Option chain v1'; url="/api/quote/option/chain?tickerId=$AMD"},
    @{name='Option chain list'; url="/api/quote/option/chain/list?tickerId=$AMD"},

    # --- Capital flow ---
    @{name='Capital flow daily'; url="/api/stock/capitalflow/ticker?tickerId=$AMD&showDm=false&type=d"},
    @{name='Capital flow 5d'; url="/api/stock/capitalflow/ticker?tickerId=$AMD&showDm=false&type=5d"},

    # --- Market movers ---
    @{name='Market hot list'; url="/api/stock/hotList?regionId=6&count=20"},
    @{name='Market gainers v1'; url="/api/stock/gainList?regionId=6&count=20"},
    @{name='Market active v1'; url="/api/stock/activeList?regionId=6&count=20"},
    @{name='Market losers v1'; url="/api/stock/dropList?regionId=6&count=20"},
    @{name='Market 52wHigh'; url="/api/stock/52weekHighList?regionId=6&count=20"},
    @{name='Market 52wLow'; url="/api/stock/52weekLowList?regionId=6&count=20"},

    # --- Screener ---
    @{name='Screener rules'; url="/api/screener/ng/query/rules"},
    @{name='Sector performance'; url="/api/stock/sectorPerformance?regionId=6"},

    # --- Insider ---
    @{name='Insider v1'; url="/api/stock/insider?tickerId=$AMD"},
    @{name='Insider trade'; url="/api/stock/insiderTrade?tickerId=$AMD&count=20"},

    # --- Dividend ---
    @{name='Dividend v1'; url="/api/stock/dividend?tickerId=$AMD"},

    # --- Social ---
    @{name='Social ticker'; url="/api/social/ticker/$AMD/feed?pageSize=5"},
    @{name='Guess ticker'; url="/api/social/guessStock/ticker/$AMD"},

    # --- Corp actions ---
    @{name='Corp actions v1'; url="/api/stock/corpActions?tickerId=$AMD"},

    # --- Technical signals ---
    @{name='Technical signals'; url="/api/stock/technicalSignal?tickerId=$AMD"},
    @{name='Technical analysis'; url="/api/stock/technicalAnalysis?tickerId=$AMD"},

    # --- ETF ---
    @{name='ETF holding v1'; url="/api/stock/etfHolding?tickerId=$AMD"},

    # --- SEC filings ---
    @{name='SEC filings v1'; url="/api/stock/secFilings?tickerId=$AMD&count=10"},

    # --- Profile ---
    @{name='Company profile v1'; url="/api/stock/profile?tickerId=$AMD"},

    # --- Other ---
    @{name='Exchange status'; url="/api/quote/exchange/status"},
    @{name='Ticker types'; url="/api/quote/tickerTypes"},
    @{name='Market calendar'; url="/api/stock/marketCalendar?regionId=6"}
)

$working = 0
$empty = 0
$failed = 0

foreach ($ep in $endpoints) {
    $url = $base + $ep.url
    try {
        $r = Invoke-WebRequest -Uri $url -Headers $h -UseBasicParsing -TimeoutSec 8
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
