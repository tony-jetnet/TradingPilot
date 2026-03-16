# Test: unary Request vs StreamRequest, query params in payload vs path

$testCode = @'
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
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

(Metadata meta, string sign) BuildAuth()
{
    h["reqid"] = Guid.NewGuid().ToString();
    h["t_time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
    var sb = new StringBuilder();
    foreach (var key in signKeys) sb.Append($"{key}={h.GetValueOrDefault(key, "")}&");
    sb.Append($"secret={secret}");
    string sign = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    var metadata = new Metadata();
    foreach (var kv in h) metadata.Add(kv.Key, kv.Value);
    metadata.Add("sign", sign);
    return (metadata, sign);
}

string FormatResp(ClientResponse resp)
{
    var parts = new List<string> { $"type={resp.Type} code={resp.Code}" };
    if (!string.IsNullOrEmpty(resp.Module)) parts.Add($"mod={resp.Module}");
    if (resp.Msg != null && !resp.Msg.IsEmpty) parts.Add($"msg({resp.Msg.Length})={resp.Msg.ToStringUtf8()[..Math.Min(resp.Msg.Length, 300)]}");
    if (resp.Payload != null && !resp.Payload.IsEmpty)
    {
        var raw = resp.Payload.ToByteArray();
        string s = Encoding.UTF8.GetString(raw);
        parts.Add($"payload({raw.Length})={s[..Math.Min(s.Length, 300)]}");
    }
    if (resp.Extras.Count > 0) parts.Add($"extras=[{string.Join(",", resp.Extras.Select(e => $"{e.Key}={e.Value}"))}]");
    return string.Join(" | ", parts);
}

// Test 1: Unary Request RPC
Console.WriteLine("=== Unary Request RPC ===");
{
    var (meta, sign) = BuildAuth();
    var req = new ClientRequest { RequestId = h["reqid"], Type = MsgType.Request, Path = "api/bgw/quote/realtime?ids=913254235&includeSecu=1&delay=0&more=1" };
    foreach (var kv in h) req.Header[kv.Key] = kv.Value;
    req.Header["sign"] = sign;
    Console.Write("  [unary] ");
    try { var resp = await client.RequestAsync(req, meta); Console.WriteLine(FormatResp(resp)); }
    catch (RpcException ex) { Console.WriteLine($"gRPC: {ex.StatusCode} - {ex.Status.Detail[..Math.Min(ex.Status.Detail.Length, 100)]}"); }
}
await Task.Delay(300);

// Test 2: Path without query, params in payload
Console.WriteLine("\n=== Path without query, params as payload ===");
{
    var (meta, sign) = BuildAuth();
    var req = new ClientRequest
    {
        RequestId = h["reqid"], Type = MsgType.Request,
        Path = "api/bgw/quote/realtime",
        Payload = ByteString.CopyFromUtf8("ids=913254235&includeSecu=1&delay=0&more=1")
    };
    foreach (var kv in h) req.Header[kv.Key] = kv.Value;
    req.Header["sign"] = sign;
    Console.Write("  [payload-params] ");
    try
    {
        using var call = client.StreamRequest(headers: meta);
        await call.RequestStream.WriteAsync(req);
        await call.RequestStream.CompleteAsync();
        if (await call.ResponseStream.MoveNext(CancellationToken.None))
            Console.WriteLine(FormatResp(call.ResponseStream.Current));
        else Console.WriteLine("no response");
    }
    catch (RpcException ex) { Console.WriteLine($"gRPC: {ex.StatusCode} - {ex.Status.Detail[..Math.Min(ex.Status.Detail.Length, 100)]}"); }
}
await Task.Delay(300);

// Test 3: JSON payload
Console.WriteLine("\n=== JSON payload ===");
{
    var (meta, sign) = BuildAuth();
    var req = new ClientRequest
    {
        RequestId = h["reqid"], Type = MsgType.Request,
        Path = "api/bgw/quote/realtime",
        Payload = ByteString.CopyFromUtf8("{\"ids\":\"913254235\",\"includeSecu\":1,\"delay\":0,\"more\":1}")
    };
    foreach (var kv in h) req.Header[kv.Key] = kv.Value;
    req.Header["sign"] = sign;
    Console.Write("  [json-payload] ");
    try
    {
        using var call = client.StreamRequest(headers: meta);
        await call.RequestStream.WriteAsync(req);
        await call.RequestStream.CompleteAsync();
        if (await call.ResponseStream.MoveNext(CancellationToken.None))
            Console.WriteLine(FormatResp(call.ResponseStream.Current));
        else Console.WriteLine("no response");
    }
    catch (RpcException ex) { Console.WriteLine($"gRPC: {ex.StatusCode} - {ex.Status.Detail[..Math.Min(ex.Status.Detail.Length, 100)]}"); }
}
await Task.Delay(300);

// Test 4: Unary for multiple paths
Console.WriteLine("\n=== Unary Request for various paths ===");
string[] paths = [
    "api/bgw/quote/realtime?ids=913254235&includeSecu=1&delay=0&more=1",
    "api/securities/ticker/v5/list?tickerIds=913254235",
    "api/search/pc/tickers?keyword=AAPL&pageIndex=1&pageSize=5",
    "api/stock/tickerRealTime/getQuote?tickerId=913254235",
    "api/information/news/tickerNewses/v9?tickerId=913254235&pageSize=3",
];
foreach (var path in paths)
{
    var (meta, sign) = BuildAuth();
    var req = new ClientRequest { RequestId = h["reqid"], Type = MsgType.Request, Path = path };
    foreach (var kv in h) req.Header[kv.Key] = kv.Value;
    req.Header["sign"] = sign;
    Console.Write($"  {path[..Math.Min(path.Length, 65)],-65} ");
    try { var resp = await client.RequestAsync(req, meta); Console.WriteLine(FormatResp(resp)); }
    catch (RpcException ex) { Console.WriteLine($"gRPC: {ex.StatusCode} - {ex.Status.Detail[..Math.Min(ex.Status.Detail.Length, 80)]}"); }
    await Task.Delay(300);
}
'@

$projDir = Split-Path $PSScriptRoot -Parent
$appProj = Join-Path $projDir "src\TradingPilot.Application\TradingPilot.Application.csproj"
$tmpDir = Join-Path $env:TEMP "grpc_test_$(Get-Random)"
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
