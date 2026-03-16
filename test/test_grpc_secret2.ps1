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

string ver = headers.GetValueOrDefault("ver", "8.19.9");
string appid = headers.GetValueOrDefault("appid", "wb_desktop");
string deviceType = headers.GetValueOrDefault("device-type", "Windows");

string Md5(string input)
{
    var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
    return Convert.ToHexString(hash).ToLowerInvariant();
}

// The function loads secret first, then looks up ver, appid, device-type
// Try all permutations of concatenation
string[][] combos = [
    [secret, ver, appid, deviceType],
    [ver, appid, deviceType, secret],
    [appid, deviceType, ver, secret],
    [secret, appid, ver, deviceType],
    [secret, deviceType, appid, ver],
    [secret, ver, deviceType, appid],
    [ver, secret, appid, deviceType],
    [appid, secret, ver, deviceType],
    [deviceType, secret, ver, appid],
    // With separator
    [secret + "&" + ver + "&" + appid + "&" + deviceType],
    [ver + "&" + appid + "&" + deviceType + "&" + secret],
    // Just ver+appid+devicetype without secret (secret might be a key, not part of data)
    [ver, appid, deviceType],
    // HMAC-style: secret as key
    // For now just md5
];

string apiPath = "api/information/financial/index?tickerId=913254235";
Console.WriteLine($"ver={ver}, appid={appid}, deviceType={deviceType}");
Console.WriteLine($"Testing {combos.Length} combinations...\n");

for (int i = 0; i < combos.Length; i++)
{
    string input = string.Join("", combos[i]);
    string sign = Md5(input);

    var req = new ClientRequest
    {
        RequestId = $"test-{i}-{DateTime.UtcNow.Ticks}",
        Type = MsgType.Request,
        Path = apiPath,
    };
    foreach (var h in headers)
        req.Header[h.Key] = h.Value;
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
                Console.WriteLine($"[{i}] *** SUCCESS! *** sign={sign} input='{input}'");
                Console.WriteLine($"    {body[..Math.Min(body.Length, 200)]}");
                return;
            }
            Console.WriteLine($"[{i}] code={r.Code} len={body.Length} input='{input[..Math.Min(input.Length,60)]}'");
        }
    }
    catch (Exception ex)
    {
        var msg = ex.Message;
        var detail = msg.Contains("sign invalid") ? "INVALID" : msg.Contains("sign not found") ? "NOT FOUND" : msg[..Math.Min(msg.Length, 40)];
        Console.WriteLine($"[{i}] {detail} | input='{input[..Math.Min(input.Length,60)]}'");
    }
}

// Also try with _dsign key instead of sign
Console.WriteLine("\n--- Testing with '_dsign' key ---");
string bestSign = Md5(secret + ver + appid + deviceType);
var req2 = new ClientRequest
{
    RequestId = $"dsign-{DateTime.UtcNow.Ticks}",
    Type = MsgType.Request,
    Path = apiPath,
};
foreach (var h in headers) req2.Header[h.Key] = h.Value;
req2.Header["_dsign"] = bestSign;
try
{
    using var call2 = client.StreamRequest();
    await call2.RequestStream.WriteAsync(req2);
    await call2.RequestStream.CompleteAsync();
    if (await call2.ResponseStream.MoveNext(default))
    {
        var r = call2.ResponseStream.Current;
        var body = r.Msg?.ToStringUtf8() ?? "";
        Console.WriteLine($"_dsign: code={r.Code} len={body.Length}");
    }
}
catch (Exception ex) { Console.WriteLine($"_dsign: {ex.Message[..Math.Min(ex.Message.Length, 60)]}"); }

Console.WriteLine("\nDone");
'@

$tmpDir = "$env:TEMP\grpc_secret2"
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
