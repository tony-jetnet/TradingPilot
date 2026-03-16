$AMD = 913254235
$cursor = $null
$total = 0
for ($i = 0; $i -lt 10; $i++) {
    $url = "https://quotes-gw.webullfintech.com/api/quote/charts/query?tickerIds=$AMD&type=d1&count=20&extendTrading=0"
    if ($null -ne $cursor) { $url += "&timestamp=$cursor" }
    $resp = Invoke-WebRequest -Uri $url -UseBasicParsing -ErrorAction Stop
    $j = $resp.Content | ConvertFrom-Json
    $data = $j[0].data
    $hasMore = $j[0].hasMore
    [console]::WriteLine("Page $i : $($data.Count) bars, hasMore=$hasMore")
    foreach ($d in $data) {
        $ts = [long]($d.Split(',')[0])
        $close = $d.Split(',')[2]
        $dt = [DateTimeOffset]::FromUnixTimeSeconds($ts).DateTime.ToString('yyyy-MM-dd HH:mm')
        [console]::WriteLine("  $dt close=$close")
        $total++
        $cursor = $ts - 1
    }
    if ($hasMore -ne 1 -or $data.Count -eq 0) { [console]::WriteLine("Done"); break }
    Start-Sleep -Milliseconds 300
}
[console]::WriteLine("Total: $total daily bars")
