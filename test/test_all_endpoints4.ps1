$AMD = 913254235
$auth = Get-Content "$env:LOCALAPPDATA\WebullHook\auth_header.json" | ConvertFrom-Json
$h = @{
    'did'=$auth.did; 'access_token'=$auth.access_token
    'app'='global'; 'appid'='wb_web_app'; 'platform'='web'
    'hl'='en'; 'ver'='8.19.9'
}
$base = 'https://quotes-gw.webullfintech.com'

$endpoints = @(
    # --- Financial statements with params ---
    @{name='Balance sheet annual'; url="/api/information/financial/balancesheet?tickerId=$AMD&type=annual&count=5"},
    @{name='Balance sheet quarterly'; url="/api/information/financial/balancesheet?tickerId=$AMD&type=quarterly&count=5"},
    @{name='Income annual'; url="/api/information/financial/incomestatement?tickerId=$AMD&type=annual&count=5"},
    @{name='Income quarterly'; url="/api/information/financial/incomestatement?tickerId=$AMD&type=quarterly&count=5"},
    @{name='Cash flow annual'; url="/api/information/financial/cashflow?tickerId=$AMD&type=annual&count=5"},
    @{name='Cash flow quarterly'; url="/api/information/financial/cashflow?tickerId=$AMD&type=quarterly&count=5"},

    # --- More financial info ---
    @{name='Financial estimate'; url="/api/information/financial/estimate?tickerId=$AMD"},
    @{name='Financial growth'; url="/api/information/financial/growth?tickerId=$AMD"},
    @{name='Financial profitability'; url="/api/information/financial/profitability?tickerId=$AMD"},
    @{name='Financial valuation'; url="/api/information/financial/valuation?tickerId=$AMD"},
    @{name='Financial leverage'; url="/api/information/financial/leverage?tickerId=$AMD"},
    @{name='Financial efficiency'; url="/api/information/financial/efficiency?tickerId=$AMD"},

    # --- Capital flow variants ---
    @{name='Capital flow latest'; url="/api/stock/capitalflow/ticker?tickerId=$AMD&showDm=false"},
    @{name='Capital flow daily chart'; url="/api/stock/capitalflow/ticker?tickerId=$AMD&showDm=false&type=d&count=10"},
    @{name='Capital flow weekly'; url="/api/stock/capitalflow/ticker?tickerId=$AMD&showDm=false&type=w&count=10"},

    # --- Charts with explicit timestamps ---
    @{name='Chart d1 count=20'; url="/api/quote/charts/query?tickerIds=$AMD&type=d1&count=20&extendTrading=0"},
    @{name='Chart w1'; url="/api/quote/charts/query?tickerIds=$AMD&type=w1&count=52&extendTrading=0"},
    @{name='Chart M1 (monthly)'; url="/api/quote/charts/query?tickerIds=$AMD&type=M1&count=24&extendTrading=0"},

    # --- Quote ticks ---
    @{name='Tick data'; url="/api/quote/tick/getTick?tickerId=$AMD"},
    @{name='Tick list'; url="/api/quote/tick/list?tickerId=$AMD&count=50"},

    # --- Options deeper ---
    @{name='Option expiry list2'; url="/api/quote/option/$AMD/expirationDate/list"},
    @{name='Option chain query2'; url="/api/quote/option/chain/queryPage?tickerId=$AMD&expireDate=2026-03-21&type=call&pageSize=20"},

    # --- ETF specific ---
    @{name='ETF composition'; url="/api/information/etf/composition?tickerId=$AMD"},
    @{name='ETF performance'; url="/api/information/etf/performance?tickerId=$AMD"},

    # --- Insider ---
    @{name='Insider trades'; url="/api/information/insider/trades?tickerId=$AMD&pageSize=10"},

    # --- Analyst / Research ---
    @{name='Analyst consensus'; url="/api/information/analyst/consensus?tickerId=$AMD"},
    @{name='Analyst recommend'; url="/api/information/analyst/recommend?tickerId=$AMD"},
    @{name='Price target'; url="/api/information/analyst/priceTarget?tickerId=$AMD"},
    @{name='Research report'; url="/api/information/research/report?tickerId=$AMD&pageSize=5"},

    # --- Press release / SEC ---
    @{name='Press release v2'; url="/api/information/news/pressRelease?tickerId=$AMD&pageSize=10"},
    @{name='SEC filing v2'; url="/api/information/filing/sec?tickerId=$AMD&pageSize=10"}
)

$working = 0

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
        }
    } catch {
        $status = $_.Exception.Response.StatusCode.value__
        [console]::WriteLine("[${status}] $($ep.name)")
    }
}

[console]::WriteLine("`n=== $working endpoints returned data ===")
