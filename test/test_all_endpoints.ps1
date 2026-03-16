$AMD = 913254235
$RKLB = 950178054
$auth = Get-Content "$env:LOCALAPPDATA\WebullHook\auth_header.json" | ConvertFrom-Json

$h = @{
    'did'=$auth.did; 'access_token'=$auth.access_token
    'app'='global'; 'appid'='wb_web_app'; 'platform'='web'
    'hl'='en'; 'ver'='8.19.9'
}

$base = 'https://quotes-gw.webullfintech.com'

$endpoints = @(
    # --- Quote / Realtime ---
    @{name='Quote snapshot'; url="/api/stocks/ticker/getTickerRealTime?tickerId=$AMD"},
    @{name='Quote v2'; url="/api/quote/tickerRealTimes/v2/$AMD"},
    @{name='Quote detail'; url="/api/quote/tickerDetail/$AMD"},

    # --- Fundamentals ---
    @{name='Financials'; url="/api/securities/financial/index/$AMD"},
    @{name='Financial analysis'; url="/api/securities/financial/analysis/$AMD"},
    @{name='Financial sheet'; url="/api/securities/financial/balancesheet/$AMD"},
    @{name='Income statement'; url="/api/securities/financial/incomestatement/$AMD"},
    @{name='Cash flow'; url="/api/securities/financial/cashflow/$AMD"},
    @{name='Earnings calendar'; url="/api/securities/financial/earnings/$AMD"},
    @{name='Revenue estimate'; url="/api/securities/financial/revenueEstimate/$AMD"},
    @{name='EPS estimate'; url="/api/securities/financial/epsEstimate/$AMD"},
    @{name='Analyst rating'; url="/api/securities/analyst/rating/$AMD"},
    @{name='Analyst forecast'; url="/api/securities/analyst/forecast/$AMD"},
    @{name='Analyst target'; url="/api/securities/analyst/target/$AMD"},

    # --- Stock info ---
    @{name='Ticker info'; url="/api/securities/stock/$AMD/info"},
    @{name='Ticker brief'; url="/api/securities/stock/brief/$AMD"},
    @{name='Short interest'; url="/api/securities/stock/$AMD/shortInterest"},
    @{name='Institutional holding'; url="/api/securities/institutional/holding/$AMD"},
    @{name='ETF holding'; url="/api/securities/stock/$AMD/etfHolding"},
    @{name='Insider activity'; url="/api/securities/stock/$AMD/insiderActivity"},
    @{name='Dividend history'; url="/api/securities/stock/$AMD/dividend"},
    @{name='Split history'; url="/api/securities/stock/$AMD/split"},
    @{name='Company profile'; url="/api/securities/stock/$AMD/profile"},
    @{name='Sector industry'; url="/api/securities/stock/$AMD/sectorIndustry"},

    # --- Options ---
    @{name='Option expiry dates'; url="/api/quote/option/$AMD/expirationDate"},
    @{name='Option chain'; url="/api/quote/option/chain/query?tickerId=$AMD&count=20"},
    @{name='Option quotes'; url="/api/quote/option/strategy/list?tickerId=$AMD&type=call"},

    # --- Market / Screener ---
    @{name='Market overview'; url="/api/securities/market/overviews"},
    @{name='Top gainers'; url="/api/securities/market/top/gainers?regionId=6&count=20"},
    @{name='Top losers'; url="/api/securities/market/top/losers?regionId=6&count=20"},
    @{name='Most active'; url="/api/securities/market/top/active?regionId=6&count=20"},
    @{name='52wk high'; url="/api/securities/market/top/52wHigh?regionId=6&count=20"},
    @{name='52wk low'; url="/api/securities/market/top/52wLow?regionId=6&count=20"},

    # --- L2 / Order Book ---
    @{name='Depth L2'; url="/api/stock/tickerRealTime/getDepth?tickerId=$AMD"},
    @{name='Depth full'; url="/api/stock/tickerRealTime/getDepth?tickerId=$AMD&type=FULL"},

    # --- Time & Sales / Trades ---
    @{name='Time sales'; url="/api/stock/tickerRealTime/getTimeSales?tickerId=$AMD"},
    @{name='Trades'; url="/api/stock/tickerRealTime/trades?tickerId=$AMD"},

    # --- Technical ---
    @{name='Capital flow'; url="/api/securities/stock/$AMD/capitalFlow"},
    @{name='Capital flow intraday'; url="/api/stock/capitalflow/ticker?tickerId=$AMD&showDm=false"},
    @{name='Volume analysis'; url="/api/securities/stock/$AMD/volumeAnalysis"},
    @{name='Volume profile'; url="/api/quote/tickerChartDatas/volumeProfile?tickerId=$AMD&type=d1&count=1"},

    # --- Social ---
    @{name='Social posts'; url="/api/social/feed/ticker/$AMD`?pageSize=20"},
    @{name='Social sentiment'; url="/api/securities/stock/$AMD/socialSentiment"},

    # --- ETF / Index ---
    @{name='ETF profile'; url="/api/securities/etf/$AMD/profile"},

    # --- Misc ---
    @{name='Trading calendar'; url="/api/securities/market/tradingCalendar?regionId=6"},
    @{name='Market status'; url="/api/securities/market/status?regionId=6"},
    @{name='Ticker suggestion'; url="/api/search/pc/tickers?keyword=AMD&pageIndex=1&pageSize=5"},
    @{name='News ticker'; url="/api/information/news/tickerNewses/v9?tickerId=$AMD&currentNewsId=0&pageSize=5"},
    @{name='News important'; url="/api/information/news/importantNewsList?pageSize=5"},
    @{name='Press release'; url="/api/information/news/tickerPressRelease?tickerId=$AMD&pageSize=5"},
    @{name='Corp actions'; url="/api/securities/stock/$AMD/corporateActions"},
    @{name='SEC filings'; url="/api/securities/stock/$AMD/secFilings?pageSize=5"}
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
            $preview = $r.Content.Substring(0, [Math]::Min($len, 150)) -replace "`n",' '
            [console]::WriteLine("[OK ${len}B] $($ep.name): $preview")
            $working++
        } else {
            [console]::WriteLine("[EMPTY] $($ep.name)")
            $empty++
        }
    } catch {
        $status = $_.Exception.Response.StatusCode.value__
        [console]::WriteLine("[${status}] $($ep.name)")
        $failed++
    }
}

[console]::WriteLine("`n=== SUMMARY: $working working, $empty empty, $failed failed ===")
