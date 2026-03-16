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

var authHeaders = new SortedDictionary<string, string>();
foreach (var prop in auth.RootElement.EnumerateObject())
{
    var val = prop.Value.GetString() ?? prop.Value.ToString();
    if (!string.IsNullOrEmpty(val) && prop.Name != "content-type")
        authHeaders[prop.Name] = val;
}

string Md5(string input) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

string v = authHeaders.GetValueOrDefault("ver", "");
string a = authHeaders.GetValueOrDefault("appid", "");
string d = authHeaders.GetValueOrDefault("device-type", "");
string i2 = authHeaders.GetValueOrDefault("did", "");
string t = authHeaders.GetValueOrDefault("access_token", "");
string hl = authHeaders.GetValueOrDefault("hl", "");
string p = authHeaders.GetValueOrDefault("platform", "");
string app = authHeaders.GetValueOrDefault("app", "");
string ag = authHeaders.GetValueOrDefault("app-group", "");
string os = authHeaders.GetValueOrDefault("os", "");
string osv = authHeaders.GetValueOrDefault("osv", "");
string ch = authHeaders.GetValueOrDefault("ch", "");
string odid = authHeaders.GetValueOrDefault("odid", "");
string ttime = authHeaders.GetValueOrDefault("t_time", "");
string locale = authHeaders.GetValueOrDefault("locale", "");

// The function first makes a QString from secret, then appends found values of ver, appid, device-type, did
// In the QMap, keys are sorted. So lookups for ver, appid, device-type, did happen in that code order,
// but the VALUES are appended to the secret string in the order: ver, appid, device-type, did

// BUT WAIT - maybe the function appends key+value, not just value
// Or maybe it iterates ALL sorted keys and appends all values

string[] attempts = [
    // Core: secret + ver_val + appid_val + devicetype_val + did_val
    $"{secret}{v}{a}{d}{i2}",
    // Reverse field order
    $"{secret}{i2}{d}{a}{v}",
    // With access_token
    $"{secret}{t}{v}{a}{d}{i2}",
    $"{secret}{v}{a}{d}{i2}{t}",
    // ALL headers sorted by key, values only, + secret
    string.Join("", authHeaders.OrderBy(h => h.Key).Select(h => h.Value)) + secret,
    secret + string.Join("", authHeaders.OrderBy(h => h.Key).Select(h => h.Value)),
    // ALL header key=value sorted + secret
    string.Join("", authHeaders.OrderBy(h => h.Key).Select(h => h.Key + h.Value)) + secret,
    secret + string.Join("", authHeaders.OrderBy(h => h.Key).Select(h => h.Key + h.Value)),
    // Only the 4 fields as key=value
    $"{secret}ver{v}appid{a}device-type{d}did{i2}",
    $"ver{v}appid{a}device-type{d}did{i2}{secret}",
    // With path
    $"{secret}{apiPath}{v}{a}{d}{i2}",
    $"{secret}{v}{a}{d}{i2}{apiPath}",
    // access_token + did + secret
    $"{t}{i2}{secret}",
    $"{secret}{t}{i2}",
    // Sorted 4 fields: appid, device-type, did, ver (alphabetical)
    $"{secret}{a}{d}{i2}{v}",
    $"{a}{d}{i2}{v}{secret}",
    // All sorted header values concatenated (no secret)
    string.Join("", authHeaders.OrderBy(h => h.Key).Select(h => h.Value)),
];

Console.WriteLine($"Testing {attempts.Length} sign formulas...\n");

for (int idx = 0; idx < attempts.Length; idx++)
{
    string sign = Md5(attempts[idx]);

    var metadata = new Metadata();
    foreach (var h in authHeaders) metadata.Add(h.Key.ToLowerInvariant(), h.Value);
    metadata.Add("sign", sign);

    var req = new ClientRequest
    {
        RequestId = $"t{idx}-{DateTime.UtcNow.Ticks}",
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
                Console.WriteLine($"[{idx}] *** SUCCESS *** sign={sign}");
                Console.WriteLine($"    input='{attempts[idx][..Math.Min(attempts[idx].Length, 80)]}'");
                Console.WriteLine($"    {body[..Math.Min(body.Length, 300)]}");
                return;
            }
        }
    }
    catch (Exception ex)
    {
        var det = ex.Message.Contains("invalid") ? "INV" : ex.Message.Contains("not found") ? "NF" : "?";
        Console.Write($"[{idx}]{det} ");
    }
}
Console.WriteLine("\nNo match");
'@

$tmpDir = "$env:TEMP\grpc_crack"
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
