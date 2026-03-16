$AMD = 913254235
$did = '0cbeb8ac323a472cd748d6b094305438'
$token = 'dc_ca1.19cf36fdf99-1a9f9f3f271b4dd9a0dfa8e46ad46dc5'

$domains = @(
    'https://quotes-gw.webullfintech.com',
    'https://infoapi.webullfintech.com',
    'https://securitiesapi.webullfintech.com',
    'https://userapi.webullfintech.com',
    'https://quoteapi.webullfintech.com',
    'https://newsapi.webullfintech.com',
    'https://api.webullfintech.com',
    'https://quotes-gw.webullbroker.com',
    'https://infoapi.webullbroker.com',
    'https://securitiesapi.webullbroker.com',
    'https://newsapi.webullbroker.com'
)

$paths = @(
    '/api/information/news/tickerNewses/v9?tickerId={0}&currentNewsId=0&pageSize=20',
    '/api/information/news/tickerNews?tickerId={0}&currentNewsId=0&pageSize=20',
    '/api/news/ticker/{0}?currentNewsId=0&pageSize=20',
    '/api/information/news/v5/tickerNewses?tickerId={0}&currentNewsId=0&pageSize=20'
)

$headerSets = @(
    @{ name='web'; headers=@{ 'did'=$did; 'access_token'=$token; 'app'='global'; 'appid'='wb_web_app'; 'platform'='web'; 'hl'='en' } },
    @{ name='desktop'; headers=@{ 'did'=$did; 'access_token'=$token; 'app'='ca'; 'appid'='wb_desktop'; 'platform'='qt'; 'hl'='en'; 'ver'='8.19.9'; 'device-type'='Windows' } },
    @{ name='none'; headers=@{} }
)

foreach ($hs in $headerSets) {
    foreach ($domain in $domains) {
        foreach ($path in $paths) {
            $url = $domain + ($path -f $AMD)
            try {
                $resp = Invoke-WebRequest -Uri $url -Headers $hs.headers -UseBasicParsing -TimeoutSec 5
                $len = $resp.Content.Length
                $preview = $resp.Content.Substring(0, [Math]::Min($len, 150))
                if ($len -gt 5) {
                    [console]::WriteLine("[HIT!] $($hs.name) $url => $len chars: $preview")
                }
            } catch {
                $status = $_.Exception.Response.StatusCode.value__
                # Only log non-404 errors
                if ($status -and $status -ne 404 -and $status -ne 417) {
                    [console]::WriteLine("[${status}] $($hs.name) $url")
                }
            }
        }
    }
}
[console]::WriteLine("Done scanning")
