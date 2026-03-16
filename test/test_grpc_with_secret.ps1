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

string Md5(string input)
{
    var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
    return Convert.ToHexString(hash).ToLowerInvariant();
}

// Try various combinations with the secret
string appid = headers.GetValueOrDefault("appid", "wb_desktop");
string did = headers.GetValueOrDefault("did", "");
string accessToken = headers.GetValueOrDefault("access_token", "");

string[] attempts = [
    // secret + appid
    secret + appid,
    // appid + secret
    appid + secret,
    // secret + did
    secret + did,
    // did + secret
    did + secret,
    // sorted header values + secret
    string.Join("", headers.OrderBy(h => h.Key).Select(h => h.Value)) + secret,
    // secret + sorted header values
    secret + string.Join("", headers.OrderBy(h => h.Key).Select(h => h.Value)),
    // secret + access_token
    secret + accessToken,
    // just the secret
    secret,
    // MD5(appid) + secret
    Md5(appid) + secret,
    // secret + MD5(appid)
    secret + Md5(appid),
    // sorted key=value + secret
    string.Join("&", headers.OrderBy(h => h.Key).Select(h => $"{h.Key}={h.Value}")) + secret,
    // appid + did + secret
    appid + did + secret,
    // did + appid + secret
    did + appid + secret,
];

string apiPath = "api/information/financial/index?tickerId=913254235";
Console.WriteLine($"Testing {attempts.Length} sign variants with secret '{secret}'...\n");

for (int i = 0; i < attempts.Length; i++)
{
    var sign = Md5(attempts[i]);
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
            Console.WriteLine($"[{i}] code={r.Code} len={body.Length} sign={sign}");
            Console.WriteLine($"    input={attempts[i][..Math.Min(attempts[i].Length, 60)]}");
            if (body.Length > 10)
            {
                Console.WriteLine($"    *** SUCCESS! *** {body[..Math.Min(body.Length, 200)]}");
                return;
            }
        }
    }
    catch (Exception ex)
    {
        var msg = ex.Message;
        var detail = msg.Contains("sign invalid") ? "invalid" : msg.Contains("sign not found") ? "not found" : msg[..Math.Min(msg.Length, 60)];
        Console.WriteLine($"[{i}] {detail} | input={attempts[i][..Math.Min(attempts[i].Length, 50)]}");
    }
}
Console.WriteLine("\nNo valid sign found yet");
'@

$tmpDir = "$env:TEMP\grpc_test_secret"
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
