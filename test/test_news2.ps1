$AMD = 913254235
$did = '0cbeb8ac323a472cd748d6b094305438'
$token = 'dc_ca1.19cf36fdf99-1a9f9f3f271b4dd9a0dfa8e46ad46dc5'

$webH = @{ 'did'=$did; 'access_token'=$token; 'app'='global'; 'appid'='wb_web_app'; 'platform'='web'; 'hl'='en' }

# Try region-specific domains (ca = Canada)
$domains = @(
    'https://quotes-gw.webullfintech.com',
    'https://quotes-gw.webullbroker.com',
    'https://nacomm.webullfintech.com',
    'https://act.webullfintech.com'
)

$paths = @(
    '/api/information/news/tickerNewses/v9?tickerId={0}&currentNewsId=0&pageSize=20',
    '/api/information/news/tickerNewses?tickerId={0}&currentNewsId=0&pageSize=20',
    '/api/information/news/tickerNewses/v9?tickerId={0}&currentNewsId=0&pageSize=20&regionId=6',
    '/api/information/news/tickerNewses/v9?tickerId={0}&currentNewsId=0&pageSize=20&regionId=6&type=0'
)

foreach ($domain in $domains) {
    foreach ($path in $paths) {
        $url = $domain + ($path -f $AMD)
        try {
            $resp = Invoke-WebRequest -Uri $url -Headers $webH -UseBasicParsing -TimeoutSec 5
            $len = $resp.Content.Length
            $preview = $resp.Content.Substring(0, [Math]::Min($len, 200))
            [console]::WriteLine("[${len}B] $url => $preview")
        } catch {
            $status = $_.Exception.Response.StatusCode.value__
            [console]::WriteLine("[${status}] $url")
        }
    }
}

# Also try: stock-specific news endpoints
$extraUrls = @(
    "https://quotes-gw.webullfintech.com/api/information/news/tickerNewses/v9?tickerId=$AMD&currentNewsId=0&pageSize=20&label=All",
    "https://quotes-gw.webullfintech.com/api/information/news/v5/tickerNewses?tickerId=$AMD&currentNewsId=0&pageSize=20",
    "https://quotes-gw.webullfintech.com/api/information/news/tickerNewsList?tickerId=$AMD&currentNewsId=0&pageSize=20",
    "https://quotes-gw.webullfintech.com/api/information/news/ticker?tickerId=$AMD&pageSize=20",
    "https://quotes-gw.webullfintech.com/api/securities/news/v2/list/ticker/$AMD`?pageSize=20",
    "https://quotes-gw.webullfintech.com/api/information/ticker/news?tickerId=$AMD&pageSize=20"
)
foreach ($url in $extraUrls) {
    try {
        $resp = Invoke-WebRequest -Uri $url -Headers $webH -UseBasicParsing -TimeoutSec 5
        $len = $resp.Content.Length
        $preview = $resp.Content.Substring(0, [Math]::Min($len, 200))
        [console]::WriteLine("[${len}B] $url => $preview")
    } catch {
        $status = $_.Exception.Response.StatusCode.value__
        [console]::WriteLine("[${status}] $url")
    }
}
[console]::WriteLine("Done")
