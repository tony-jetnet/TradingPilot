$testCode = @'
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Grpc.Net.Client;
using TradingPilot.Webull.Grpc;

var auth = JsonDocument.Parse(File.ReadAllText(
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "WebullHook", "auth_header.json")));

using var channel = GrpcChannel.ForAddress("https://quotes-grpc-gw.webullfintech.com:443");
var client = new Gateway.GatewayClient(channel);

string secret = "u556uuu7qflha9xt";

var headers = new SortedDictionary<string, string>();
foreach (var prop in auth.RootElement.EnumerateObject())
{
    var val = prop.Value.GetString() ?? prop.Value.ToString();
    if (!string.IsNullOrEmpty(val) && prop.Name != "content-type")
        headers[prop.Name] = val;
}

string ver = headers.GetValueOrDefault("ver", "");
string appid = headers.GetValueOrDefault("appid", "");
string deviceType = headers.GetValueOrDefault("device-type", "");
string did = headers.GetValueOrDefault("did", "");

string Md5(string input)
{
    var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
    return Convert.ToHexString(hash).ToLowerInvariant();
}

// Function looks up: ver, appid, device-type, did (in that order from disassembly)
// Try all permutations of these 4 values + secret
var parts = new[] { secret, ver, appid, deviceType, did };
var labels = new[] { "secret", "ver", "appid", "devtype", "did" };

// Key permutations based on function flow
string[][] combos = [
    // Secret first, then the 4 fields in disassembly order
    [secret, ver, appid, deviceType, did],
    // Fields in disasm order, then secret
    [ver, appid, deviceType, did, secret],
    // Each field pair with secret between
    [ver, secret, appid, secret, deviceType, secret, did, secret],
    // Secret at end only
    [ver, appid, deviceType, did, secret],
    // Secret at start only
    [secret, ver, appid, deviceType, did],
    // Reverse disasm order + secret
    [did, deviceType, appid, ver, secret],
    // secret+did+appid+devicetype+ver
    [secret, did, appid, deviceType, ver],
    // Try with "111" (found near secret in binary)
    [secret, "111"],
    // The function likely builds: secret + verValue + appidValue + deviceTypeValue + didValue
    // OR: MD5(verValue + appidValue + deviceTypeValue + didValue + secret)
    // Test both
];

string apiPath = "api/information/financial/index?tickerId=913254235";
Console.WriteLine($"ver={ver}, appid={appid}, deviceType={deviceType}, did={did}");
Console.WriteLine($"secret={secret}\n");

for (int i = 0; i < combos.Length; i++)
{
    string input = string.Join("", combos[i]);
    string sign = Md5(input);

    var req = new ClientRequest
    {
        RequestId = $"t{i}-{DateTime.UtcNow.Ticks}",
        Type = MsgType.Request,
        Path = apiPath,
    };
    foreach (var h in headers) req.Header[h.Key] = h.Value;
    req.Header["sign"] = sign;

    try
    {
        using var call = client.StreamRequest();
        await call.RequestStream.WriteAsync(req);
        await call.RequestStream.CompleteAsync();
        if (await call.ResponseStream.MoveNext(default))
        {
            var r = call.ResponseStream.Current;
            var body = r.Msg?.ToStringUtf8() ?? r.Payload?.ToStringUtf8() ?? "";
            if (body.Length > 10)
            {
                Console.WriteLine($"[{i}] *** SUCCESS *** sign={sign}");
                Console.WriteLine($"    input='{input}'");
                Console.WriteLine($"    {body[..Math.Min(body.Length, 300)]}");

                // Test more endpoints with this sign formula
                Console.WriteLine("\n=== Testing more endpoints ===");
                string[] moreEndpoints = [
                    "api/securities/stock/913254235/profile",
                    "api/securities/analyst/rating/913254235",
                    "api/securities/market/top/gainers?regionId=6&count=5",
                    "api/quote/option/913254235/expirationDate",
                ];
                foreach (var ep in moreEndpoints)
                {
                    var req2 = new ClientRequest
                    {
                        RequestId = $"ep-{DateTime.UtcNow.Ticks}",
                        Type = MsgType.Request,
                        Path = ep,
                    };
                    foreach (var h in headers) req2.Header[h.Key] = h.Value;
                    req2.Header["sign"] = sign;
                    try
                    {
                        using var call2 = client.StreamRequest();
                        await call2.RequestStream.WriteAsync(req2);
                        await call2.RequestStream.CompleteAsync();
                        if (await call2.ResponseStream.MoveNext(default))
                        {
                            var r2 = call2.ResponseStream.Current;
                            var b2 = r2.Msg?.ToStringUtf8() ?? r2.Payload?.ToStringUtf8() ?? "";
                            Console.WriteLine($"  [{b2.Length}B] {ep}: {b2[..Math.Min(b2.Length, 100)]}");
                        }
                    }
                    catch (Exception ex2) { Console.WriteLine($"  [ERR] {ep}: {ex2.Message[..Math.Min(ex2.Message.Length, 60)]}"); }
                }
                return;
            }
            Console.WriteLine($"[{i}] code={r.Code} empty");
        }
    }
    catch (Exception ex)
    {
        var detail = ex.Message.Contains("invalid") ? "INVALID" : ex.Message.Contains("not found") ? "NOT FOUND" : ex.Message[..Math.Min(ex.Message.Length, 40)];
        Console.WriteLine($"[{i}] {detail} | '{input[..Math.Min(input.Length, 60)]}'");
    }
}
Console.WriteLine("\nNo valid combination found");
'@

$tmpDir = "$env:TEMP\grpc_final"
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
