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

var apiPath = "api/information/financial/index?tickerId=913254235";

// Build header map
var headers = new SortedDictionary<string, string>();
foreach (var prop in auth.RootElement.EnumerateObject())
{
    var val = prop.Value.GetString() ?? prop.Value.ToString();
    if (!string.IsNullOrEmpty(val) && prop.Name != "content-type")
        headers[prop.Name] = val;
}

// Compute MD5 sign from sorted header values
// Try several combinations

string Md5(string input)
{
    var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
    return Convert.ToHexString(hash).ToLowerInvariant();
}

// Attempt 1: MD5 of all header values concatenated (sorted by key)
var concat1 = string.Join("", headers.OrderBy(h => h.Key).Select(h => h.Value));
var sign1 = Md5(concat1);
Console.WriteLine($"Sign attempt 1 (values concat): {sign1}");

// Attempt 2: MD5 of key=value pairs
var concat2 = string.Join("", headers.OrderBy(h => h.Key).Select(h => $"{h.Key}={h.Value}"));
var sign2 = Md5(concat2);
Console.WriteLine($"Sign attempt 2 (key=value): {sign2}");

// Attempt 3: MD5 of key=value&key=value (URL-style)
var concat3 = string.Join("&", headers.OrderBy(h => h.Key).Select(h => $"{h.Key}={h.Value}"));
var sign3 = Md5(concat3);
Console.WriteLine($"Sign attempt 3 (url-style): {sign3}");

// Try each sign variant
foreach (var (label, sign) in new[] { ("values", sign1), ("kv", sign2), ("url", sign3) })
{
    var req = new ClientRequest
    {
        RequestId = $"test-{DateTime.UtcNow.Ticks}",
        Type = MsgType.Request,
        Path = apiPath,
    };
    foreach (var h in headers)
        req.Header[h.Key] = h.Value;
    req.Header["_dsign"] = sign;

    try
    {
        using var call = client.StreamRequest();
        await call.RequestStream.WriteAsync(req);
        await call.RequestStream.CompleteAsync();
        if (await call.ResponseStream.MoveNext(default))
        {
            var r = call.ResponseStream.Current;
            var body = r.Msg?.ToStringUtf8() ?? r.Payload?.ToStringUtf8() ?? "";
            Console.WriteLine($"[{label}] code={r.Code} len={body.Length} {body[..Math.Min(body.Length, 150)]}");
        }
    }
    catch (Exception ex) { Console.WriteLine($"[{label}] ERR: {ex.Message[..Math.Min(ex.Message.Length, 100)]}"); }
}

// Also try without _dsign but with 'sign' header
foreach (var signKey in new[] { "sign", "x-sv", "_sign" })
{
    var req = new ClientRequest
    {
        RequestId = $"test-{DateTime.UtcNow.Ticks}",
        Type = MsgType.Request,
        Path = apiPath,
    };
    foreach (var h in headers)
        req.Header[h.Key] = h.Value;
    req.Header[signKey] = sign2;

    try
    {
        using var call = client.StreamRequest();
        await call.RequestStream.WriteAsync(req);
        await call.RequestStream.CompleteAsync();
        if (await call.ResponseStream.MoveNext(default))
        {
            var r = call.ResponseStream.Current;
            var body = r.Msg?.ToStringUtf8() ?? r.Payload?.ToStringUtf8() ?? "";
            Console.WriteLine($"[key={signKey}] code={r.Code} len={body.Length} {body[..Math.Min(body.Length, 150)]}");
        }
    }
    catch (Exception ex) { Console.WriteLine($"[key={signKey}] ERR: {ex.Message[..Math.Min(ex.Message.Length, 80)]}"); }
}

Console.WriteLine("Done");
'@

$tmpDir = "$env:TEMP\grpc_test5"
if (Test-Path $tmpDir) { Remove-Item $tmpDir -Recurse -Force }
New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null
Copy-Item 'D:\Third-Parties\TradingPilot\src\TradingPilot.Application\Protos\gateway.proto' "$tmpDir\gateway.proto"
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
  <ItemGroup><Protobuf Include="gateway.proto" GrpcServices="Client" /></ItemGroup>
  <ItemGroup>
    <PackageReference Include="Google.Protobuf" Version="3.*" />
    <PackageReference Include="Grpc.Net.Client" Version="2.*" />
    <PackageReference Include="Grpc.Tools" Version="2.*" PrivateAssets="All" />
  </ItemGroup>
</Project>
"@ | Set-Content "$tmpDir\GrpcTest.csproj"
$testCode | Set-Content "$tmpDir\Program.cs"
Push-Location $tmpDir
& dotnet run 2>&1 | ForEach-Object { [console]::WriteLine($_) }
Pop-Location
Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
