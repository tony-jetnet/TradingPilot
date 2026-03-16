$AMD = 913254235
$auth = Get-Content "$env:LOCALAPPDATA\WebullHook\auth_header.json" | ConvertFrom-Json

$headers = @{
    'did'=$auth.did; 'access_token'=$auth.access_token
    'app'='global'; 'appid'='wb_web_app'; 'platform'='web'; 'hl'='en'
    'ver'='8.19.9'
}

$url = "https://quotes-gw.webullfintech.com/api/information/news/tickerNewses/v9?tickerId=$AMD&currentNewsId=0&pageSize=20"
$r = Invoke-WebRequest -Uri $url -Headers $headers -UseBasicParsing
$j = $r.Content | ConvertFrom-Json
[console]::WriteLine("AMD news count: $($j.Count)")
if ($j.Count -gt 0) {
    [console]::WriteLine("First: id=$($j[0].id) title=$($j[0].title)")
    [console]::WriteLine("Source: $($j[0].sourceName) time=$($j[0].newsTime)")
    [console]::WriteLine("Keys: $($j[0].PSObject.Properties.Name -join ', ')")
}

# Also test RKLB
$RKLB = 950178054
$url2 = "https://quotes-gw.webullfintech.com/api/information/news/tickerNewses/v9?tickerId=$RKLB&currentNewsId=0&pageSize=20"
$r2 = Invoke-WebRequest -Uri $url2 -Headers $headers -UseBasicParsing
$j2 = $r2.Content | ConvertFrom-Json
[console]::WriteLine("`nRKLB news count: $($j2.Count)")
if ($j2.Count -gt 0) {
    [console]::WriteLine("First: id=$($j2[0].id) title=$($j2[0].title)")
}

[console]::WriteLine("Done")
