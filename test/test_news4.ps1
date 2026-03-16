$AMD = 913254235
$auth = Get-Content "$env:LOCALAPPDATA\WebullHook\auth_header.json" | ConvertFrom-Json

$url = "https://quotes-gw.webullfintech.com/api/information/news/tickerNewses/v9?tickerId=$AMD&currentNewsId=0&pageSize=20"

# Baseline: web headers return empty
$base = @{ 'did'=$auth.did; 'access_token'=$auth.access_token; 'app'='global'; 'appid'='wb_web_app'; 'platform'='web'; 'hl'='en' }

# Try adding desktop headers one at a time to find which one causes 403
$extras = @(
    @{name='app=ca'; key='app'; val='ca'},
    @{name='appid=wb_desktop'; key='appid'; val='wb_desktop'},
    @{name='platform=qt'; key='platform'; val='qt'},
    @{name='device-type=Windows'; key='device-type'; val='Windows'},
    @{name='ver=8.19.9'; key='ver'; val='8.19.9'},
    @{name='t_time'; key='t_time'; val=$auth.t_time},
    @{name='x-sv'; key='x-sv'; val=$auth.'x-sv'},
    @{name='os=windows'; key='os'; val='windows'},
    @{name='osv=11'; key='osv'; val='11'},
    @{name='ch=qt_webull'; key='ch'; val='qt_webull'},
    @{name='odid'; key='odid'; val=$auth.odid},
    @{name='app-group=broker'; key='app-group'; val='broker'},
    @{name='user-agent=windows_desktop_https'; key='user-agent'; val='windows_desktop_https'}
)

foreach ($extra in $extras) {
    $h = @{}; $base.Keys | ForEach-Object { $h[$_] = $base[$_] }
    $h[$extra.key] = $extra.val
    try {
        $r = Invoke-WebRequest -Uri $url -Headers $h -UseBasicParsing -TimeoutSec 5
        [console]::WriteLine("[200 $($r.Content.Length)B] +$($extra.name)")
    } catch {
        $status = $_.Exception.Response.StatusCode.value__
        [console]::WriteLine("[$status] +$($extra.name)")
    }
}

# Now try: desktop identity but with fresh t_time and WITHOUT x-sv
[console]::WriteLine("")
[console]::WriteLine("--- Desktop identity, fresh t_time, no x-sv ---")
$freshTime = [long]([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())
$deskNoSv = @{
    'did'=$auth.did; 'access_token'=$auth.access_token
    'app'='ca'; 'appid'='wb_desktop'; 'platform'='qt'
    'device-type'='Windows'; 'hl'='en'; 'ver'='8.19.9'
    'os'='windows'; 'osv'='11'; 't_time'="$freshTime"
    'locale'='en_US'; 'tz'='America/Edmonton'
}
try {
    $r = Invoke-WebRequest -Uri $url -Headers $deskNoSv -UseBasicParsing -TimeoutSec 5
    [console]::WriteLine("[200 $($r.Content.Length)B] desktop no x-sv: $($r.Content.Substring(0, [Math]::Min($r.Content.Length, 200)))")
} catch {
    $status = $_.Exception.Response.StatusCode.value__
    $reader = [System.IO.StreamReader]::new($_.Exception.Response.GetResponseStream())
    $body = $reader.ReadToEnd()
    [console]::WriteLine("[$status] desktop no x-sv: $body")
}

# Try with just did + access_token + app=ca (no appid override)
[console]::WriteLine("")
[console]::WriteLine("--- Minimal CA identity ---")
$minCA = @{ 'did'=$auth.did; 'access_token'=$auth.access_token; 'app'='ca'; 'hl'='en' }
try {
    $r = Invoke-WebRequest -Uri $url -Headers $minCA -UseBasicParsing -TimeoutSec 5
    [console]::WriteLine("[200 $($r.Content.Length)B] min CA: $($r.Content.Substring(0, [Math]::Min($r.Content.Length, 200)))")
} catch {
    $status = $_.Exception.Response.StatusCode.value__
    [console]::WriteLine("[$status] min CA")
}

[console]::WriteLine("Done")
