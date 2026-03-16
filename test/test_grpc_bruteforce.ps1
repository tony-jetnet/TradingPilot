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
string apiPath = "api/information/financial/index?tickerId=913254235";

var authHeaders = new SortedDictionary<string, string>();
foreach (var prop in auth.RootElement.EnumerateObject())
{
    var val = prop.Value.GetString() ?? prop.Value.ToString();
    if (!string.IsNullOrEmpty(val) && prop.Name != "content-type")
        authHeaders[prop.Name] = val;
}

string Md5(string input) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

// The getHeadMd5Sign function: builds string from secret + looked-up values of ver, appid, device-type, did
// Then calls getMd5 on it.
// The 'sign' key gave INVALID before when we had wrong value.
// But NOW with ALL headers in the map, the server sees 'sign' in our proto headers.
//
// KEY INSIGHT: Maybe the server checks a DIFFERENT sign location:
// - The sign might go in gRPC CALL metadata, not the proto header map
// - Or the sign might be a specific header like x-s, x-sign, etc.

// Let's try: compute sign = MD5(secret + ver + appid + device-type + did)
// And put it in various locations

string ver = authHeaders.GetValueOrDefault("ver", "");
string appid = authHeaders.GetValueOrDefault("appid", "");
string dt = authHeaders.GetValueOrDefault("device-type", "");
string did = authHeaders.GetValueOrDefault("did", "");
string sign = Md5(secret + ver + appid + dt + did);

Console.WriteLine($"Computed sign: {sign}");
Console.WriteLine($"From: secret+ver+appid+dt+did = '{secret}'+'{ver}'+'{appid}'+'{dt}'+'{did}'\n");

// Test 1: sign in gRPC metadata only (not in proto header)
Console.WriteLine("Test 1: sign in gRPC metadata only");
{
    var metadata = new Grpc.Core.Metadata();
    metadata.Add("sign", sign);

    var req = new ClientRequest { RequestId = $"t1-{DateTime.UtcNow.Ticks}", Type = MsgType.Request, Path = apiPath };
    foreach (var h in authHeaders) req.Header[h.Key] = h.Value;

    try
    {
        using var call = client.StreamRequest(metadata);
        await call.RequestStream.WriteAsync(req);
        await call.RequestStream.CompleteAsync();
        if (await call.ResponseStream.MoveNext(default))
        {
            var r = call.ResponseStream.Current;
            var body = r.Msg?.ToStringUtf8() ?? "";
            Console.WriteLine($"  code={r.Code} len={body.Length} {body[..Math.Min(body.Length, 150)]}");
            if (body.Length > 10) { Console.WriteLine("  *** SUCCESS ***"); return; }
        }
    }
    catch (Exception ex) { Console.WriteLine($"  {ex.Message[..Math.Min(ex.Message.Length, 80)]}"); }
}

// Test 2: sign in both metadata AND proto header
Console.WriteLine("\nTest 2: sign in metadata + proto header");
{
    var metadata = new Grpc.Core.Metadata();
    metadata.Add("sign", sign);

    var req = new ClientRequest { RequestId = $"t2-{DateTime.UtcNow.Ticks}", Type = MsgType.Request, Path = apiPath };
    foreach (var h in authHeaders) req.Header[h.Key] = h.Value;
    req.Header["sign"] = sign;

    try
    {
        using var call = client.StreamRequest(metadata);
        await call.RequestStream.WriteAsync(req);
        await call.RequestStream.CompleteAsync();
        if (await call.ResponseStream.MoveNext(default))
        {
            var r = call.ResponseStream.Current;
            var body = r.Msg?.ToStringUtf8() ?? "";
            Console.WriteLine($"  code={r.Code} len={body.Length} {body[..Math.Min(body.Length, 150)]}");
            if (body.Length > 10) { Console.WriteLine("  *** SUCCESS ***"); return; }
        }
    }
    catch (Exception ex) { Console.WriteLine($"  {ex.Message[..Math.Min(ex.Message.Length, 80)]}"); }
}

// Test 3: Various sign formulas, all in proto header with key="sign"
Console.WriteLine("\nTest 3: Various MD5 formulas with key='sign' in proto header");
string[] formulas = [
    Md5(did + appid + dt + ver + secret),
    Md5(appid + did + dt + ver + secret),
    Md5(did + ver + appid + dt + secret),
    Md5(secret + did + ver + appid + dt),
    Md5(secret + did),
    Md5(did + secret),
    Md5(did + appid + secret),
    Md5(secret + did + appid),
    // With path included
    Md5(secret + ver + appid + dt + did + apiPath),
    Md5(apiPath + secret + ver + appid + dt + did),
    // Separator versions
    Md5($"{did}&{appid}&{dt}&{ver}&{secret}"),
    Md5($"{secret}&{did}&{appid}&{dt}&{ver}"),
];
string[] formulaDescs = [
    "did+appid+dt+ver+secret",
    "appid+did+dt+ver+secret",
    "did+ver+appid+dt+secret",
    "secret+did+ver+appid+dt",
    "secret+did",
    "did+secret",
    "did+appid+secret",
    "secret+did+appid",
    "secret+ver+appid+dt+did+path",
    "path+secret+ver+appid+dt+did",
    "did&appid&dt&ver&secret",
    "secret&did&appid&dt&ver",
];

for (int i = 0; i < formulas.Length; i++)
{
    var req = new ClientRequest { RequestId = $"f{i}-{DateTime.UtcNow.Ticks}", Type = MsgType.Request, Path = apiPath };
    foreach (var h in authHeaders) req.Header[h.Key] = h.Value;
    req.Header["sign"] = formulas[i];

    try
    {
        using var call = client.StreamRequest();
        await call.RequestStream.WriteAsync(req);
        await call.RequestStream.CompleteAsync();
        if (await call.ResponseStream.MoveNext(default))
        {
            var r = call.ResponseStream.Current;
            var body = r.Msg?.ToStringUtf8() ?? "";
            if (body.Length > 10)
            {
                Console.WriteLine($"  [{i}] *** SUCCESS *** formula={formulaDescs[i]}");
                Console.WriteLine($"  {body[..Math.Min(body.Length, 200)]}");
                return;
            }
        }
    }
    catch (Exception ex)
    {
        var d = ex.Message.Contains("invalid") ? "INV" : ex.Message.Contains("not found") ? "NF" : "ERR";
        Console.Write($"[{i}]{d} ");
    }
}
Console.WriteLine("\nNo valid formula found");
'@

$tmpDir = "$env:TEMP\grpc_bf"
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
