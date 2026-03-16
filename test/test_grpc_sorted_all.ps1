$testCode = @'
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using TradingPilot.Webull.Grpc;

var auth = JsonDocument.Parse(File.ReadAllText(
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "WebullHook", "auth_header.json")));

using var channel = GrpcChannel.ForAddress("https://quotes-grpc-gw.webullfintech.com:443");
var client = new Gateway.GatewayClient(channel);

string secret = "u556uuu7qflha9xt";
string apiPath = "api/information/financial/index?tickerId=913254235";

// Only send the MINIMUM required headers to simplify sign computation
// The function looks up: ver, appid, device-type, did
// Maybe the sign is computed from ONLY those 4 values
var minHeaders = new SortedDictionary<string, string> {
    ["did"] = "0cbeb8ac323a472cd748d6b094305438",
    ["access_token"] = auth.RootElement.GetProperty("access_token").GetString()!,
    ["appid"] = "wb_desktop",
    ["device-type"] = "Windows",
    ["ver"] = "8.19.9",
    ["hl"] = "en",
    ["app"] = "ca",
    ["platform"] = "qt",
};

string Md5(string input) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

// The function iterates the QMap (sorted by key):
// QMap sorts alphabetically: app, appid, device-type, did, hl, platform, ver
// For each of ver, appid, device-type, did it finds the value and appends
// But the initial string is the secret

// What if: sign = MD5(secret + QMap.values in alphabetical key order)?
// = MD5(secret + app_val + appid_val + device-type_val + did_val + hl_val + platform_val + ver_val)

string allVals = string.Join("", minHeaders.OrderBy(h => h.Key).Select(h => h.Value));
string[] attempts = [
    Md5(secret + allVals),
    Md5(allVals + secret),
    Md5(allVals),
    // Only the 4 looked-up fields (sorted: appid, device-type, did, ver)
    Md5(secret + minHeaders["appid"] + minHeaders["device-type"] + minHeaders["did"] + minHeaders["ver"]),
    Md5(minHeaders["appid"] + minHeaders["device-type"] + minHeaders["did"] + minHeaders["ver"] + secret),
    // Include access_token (sorted: access_token, appid, device-type, did, ver)
    Md5(secret + minHeaders["access_token"] + minHeaders["appid"] + minHeaders["device-type"] + minHeaders["did"] + minHeaders["ver"]),
    Md5(minHeaders["access_token"] + minHeaders["appid"] + minHeaders["device-type"] + minHeaders["did"] + minHeaders["ver"] + secret),
];
string[] descs = [
    "s+allVals_sorted", "allVals_sorted+s", "allVals_only",
    "s+a+d+did+v(sorted)", "a+d+did+v+s(sorted)",
    "s+at+a+d+did+v", "at+a+d+did+v+s"
];

Console.WriteLine($"Min headers: {string.Join(", ", minHeaders.Keys)}");
Console.WriteLine($"All vals sorted: {allVals[..Math.Min(allVals.Length, 80)]}\n");

for (int i = 0; i < attempts.Length; i++)
{
    var metadata = new Metadata();
    foreach (var h in minHeaders) metadata.Add(h.Key, h.Value);
    metadata.Add("sign", attempts[i]);

    var req = new ClientRequest
    {
        RequestId = $"t{i}-{DateTime.UtcNow.Ticks}",
        Type = MsgType.Request,
        Path = apiPath,
    };

    try
    {
        using var call = client.StreamRequest(metadata);
        await call.RequestStream.WriteAsync(req);
        await call.RequestStream.CompleteAsync();
        if (await call.ResponseStream.MoveNext(default))
        {
            var r = call.ResponseStream.Current;
            var body = r.Msg?.ToStringUtf8() ?? r.Payload?.ToStringUtf8() ?? "";
            if (body.Length > 10)
            {
                Console.WriteLine($"[{i}] *** SUCCESS *** formula={descs[i]}");
                Console.WriteLine($"    sign={attempts[i]}");
                Console.WriteLine($"    {body[..Math.Min(body.Length, 300)]}");
                return;
            }
        }
    }
    catch (Exception ex)
    {
        var det = ex.Message.Contains("sign invalid") ? "INV" :
                  ex.Message.Contains("not found") ? "NF" :
                  ex.Message.Contains("header invalid") ? "HDR" :
                  ex.Message[..Math.Min(ex.Message.Length, 30)];
        Console.Write($"[{i}]{det} ");
    }
}
Console.WriteLine("\nNo match. Trying HMAC-MD5...");

// Maybe it's HMAC-MD5, not plain MD5
string HmacMd5(string key, string data)
{
    using var hmac = new HMACMD5(Encoding.UTF8.GetBytes(key));
    return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
}

string[] hmacAttempts = [
    HmacMd5(secret, allVals),
    HmacMd5(secret, minHeaders["appid"] + minHeaders["device-type"] + minHeaders["did"] + minHeaders["ver"]),
    HmacMd5(allVals, secret),
    HmacMd5(secret, minHeaders["did"]),
];

for (int i = 0; i < hmacAttempts.Length; i++)
{
    var metadata = new Metadata();
    foreach (var h in minHeaders) metadata.Add(h.Key, h.Value);
    metadata.Add("sign", hmacAttempts[i]);

    var req = new ClientRequest
    {
        RequestId = $"h{i}-{DateTime.UtcNow.Ticks}",
        Type = MsgType.Request,
        Path = apiPath,
    };

    try
    {
        using var call = client.StreamRequest(metadata);
        await call.RequestStream.WriteAsync(req);
        await call.RequestStream.CompleteAsync();
        if (await call.ResponseStream.MoveNext(default))
        {
            var r = call.ResponseStream.Current;
            var body = r.Msg?.ToStringUtf8() ?? r.Payload?.ToStringUtf8() ?? "";
            if (body.Length > 10) { Console.WriteLine($"\nHMAC[{i}] *** SUCCESS ***\n{body[..Math.Min(body.Length, 200)]}"); return; }
        }
    }
    catch { Console.Write($"H{i} "); }
}
Console.WriteLine("\nDone");
'@

$tmpDir = "$env:TEMP\grpc_sorted"
if (Test-Path $tmpDir) { Remove-Item $tmpDir -Recurse -Force }
New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null
Copy-Item 'D:\Third-Parties\TradingPilot\src\TradingPilot.Application\Protos\gateway.proto' "$tmpDir\gateway.proto"
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
  <ItemGroup><Protobuf Include="gateway.proto" GrpcServices="Client" /></ItemGroup>
  <ItemGroup>
    <PackageReference Include="Google.Protobuf" Version="3.*" /><PackageReference Include="Grpc.Net.Client" Version="2.*" />
    <PackageReference Include="Grpc.Tools" Version="2.*" PrivateAssets="All" />
  </ItemGroup>
</Project>
"@ | Set-Content "$tmpDir\GrpcTest.csproj"
$testCode | Set-Content "$tmpDir\Program.cs"
Push-Location $tmpDir
& dotnet run 2>&1 | ForEach-Object { [console]::WriteLine($_) }
Pop-Location
Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
