# Working gRPC sign! Test different header placements for data response

$testCode = @'
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using TradingPilot.Webull.Grpc;

var authPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "WebullHook", "auth_header.json");
var auth = JsonDocument.Parse(File.ReadAllText(authPath));
using var channel = GrpcChannel.ForAddress("https://quotes-grpc-gw.webullfintech.com:443");
var client = new Gateway.GatewayClient(channel);

string secret = "u556uuu7qflha9xtlabgd5dzb0wk4a4i";
string[] signKeys = ["appid", "device-type", "did", "platform", "reqid", "ver"];

var h = new Dictionary<string, string>();
foreach (var prop in auth.RootElement.EnumerateObject())
{
    var val = prop.Value.GetString() ?? prop.Value.ToString();
    if (!string.IsNullOrEmpty(val) && prop.Name != "content-type")
        h[prop.Name] = val;
}
h["reqid"] = Guid.NewGuid().ToString();
h["t_time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

var sb = new StringBuilder();
foreach (var key in signKeys)
    sb.Append($"{key}={h.GetValueOrDefault(key, "")}&");
sb.Append($"secret={secret}");
string sign = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();

Console.WriteLine($"Sign: {sign}");

// Test: auth in proto header, sign in BOTH metadata and proto header
var metadata = new Metadata();
foreach (var kv in h) metadata.Add(kv.Key, kv.Value);
metadata.Add("sign", sign);

var request = new ClientRequest
{
    RequestId = h["reqid"],
    Type = MsgType.Request,
    Path = "api/securities/stock/913254235/info",
};
foreach (var kv in h) request.Header[kv.Key] = kv.Value;
request.Header["sign"] = sign;

Console.Write("[both] ");
try
{
    using var call = client.StreamRequest(headers: metadata);
    await call.RequestStream.WriteAsync(request);
    await call.RequestStream.CompleteAsync();
    if (await call.ResponseStream.MoveNext(CancellationToken.None))
    {
        var resp = call.ResponseStream.Current;
        Console.Write($"code={resp.Code} ");
        if (resp.Msg != null && !resp.Msg.IsEmpty)
            Console.WriteLine($"msg({resp.Msg.Length}B)={resp.Msg.ToStringUtf8()[..Math.Min(resp.Msg.Length, 500)]}");
        else if (resp.Payload != null && !resp.Payload.IsEmpty)
            Console.WriteLine($"payload({resp.Payload.Length}B)={resp.Payload.ToStringUtf8()[..Math.Min(resp.Payload.Length, 500)]}");
        else
            Console.WriteLine("(empty body)");
    }
    else Console.WriteLine("no response");
}
catch (Exception ex) { Console.WriteLine($"ERROR: {ex.Message}"); }

// Test: sign in metadata only, auth in proto header
Console.Write("[proto+meta_sign] ");
var request2 = new ClientRequest
{
    RequestId = Guid.NewGuid().ToString(),
    Type = MsgType.Request,
    Path = "api/securities/stock/913254235/info",
};
foreach (var kv in h) request2.Header[kv.Key] = kv.Value;
// Recompute sign with new reqid
h["reqid"] = request2.RequestId;
var sb2 = new StringBuilder();
foreach (var key in signKeys)
    sb2.Append($"{key}={h.GetValueOrDefault(key, "")}&");
sb2.Append($"secret={secret}");
string sign2 = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(sb2.ToString()))).ToLowerInvariant();
request2.Header["sign"] = sign2;
var meta2 = new Metadata();
meta2.Add("sign", sign2);

try
{
    using var call = client.StreamRequest(headers: meta2);
    await call.RequestStream.WriteAsync(request2);
    await call.RequestStream.CompleteAsync();
    if (await call.ResponseStream.MoveNext(CancellationToken.None))
    {
        var resp = call.ResponseStream.Current;
        Console.Write($"code={resp.Code} ");
        if (resp.Msg != null && !resp.Msg.IsEmpty)
            Console.WriteLine($"msg({resp.Msg.Length}B)={resp.Msg.ToStringUtf8()[..Math.Min(resp.Msg.Length, 500)]}");
        else if (resp.Payload != null && !resp.Payload.IsEmpty)
            Console.WriteLine($"payload({resp.Payload.Length}B)={resp.Payload.ToStringUtf8()[..Math.Min(resp.Payload.Length, 500)]}");
        else
            Console.WriteLine("(empty body)");
    }
    else Console.WriteLine("no response");
}
catch (Exception ex) { Console.WriteLine($"ERROR: {ex.Message}"); }
'@

$projDir = Split-Path $PSScriptRoot -Parent
$appProj = Join-Path $projDir "src\TradingPilot.Application\TradingPilot.Application.csproj"
$tmpDir = Join-Path $env:TEMP "grpc_sign_test_$(Get-Random)"
New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$appProj" />
  </ItemGroup>
</Project>
"@ | Set-Content (Join-Path $tmpDir "Test.csproj")
$testCode | Set-Content (Join-Path $tmpDir "Program.cs")
Push-Location $tmpDir
dotnet run --verbosity quiet
Pop-Location
Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
