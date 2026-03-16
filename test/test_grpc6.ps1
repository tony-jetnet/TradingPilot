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

var apiPath = "api/information/financial/index?tickerId=913254235";

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

var concat = string.Join("", headers.OrderBy(h => h.Key).Select(h => h.Value));
var sign = Md5(concat);

// Try: put ALL auth + sign in gRPC metadata, and ALSO in proto header
var signKeys = new[] { "_dsign", "dsign", "sign", "x-sign" };

foreach (var signKey in signKeys)
{
    var metadata = new Metadata();
    foreach (var h in headers)
        metadata.Add(h.Key, h.Value);
    metadata.Add(signKey, sign);

    var req = new ClientRequest
    {
        RequestId = $"test-{DateTime.UtcNow.Ticks}",
        Type = MsgType.Request,
        Path = apiPath,
    };
    foreach (var h in headers)
        req.Header[h.Key] = h.Value;
    req.Header[signKey] = sign;

    try
    {
        using var call = client.StreamRequest(metadata);
        await call.RequestStream.WriteAsync(req);
        await call.RequestStream.CompleteAsync();
        if (await call.ResponseStream.MoveNext(default))
        {
            var r = call.ResponseStream.Current;
            var body = r.Msg?.ToStringUtf8() ?? r.Payload?.ToStringUtf8() ?? "";
            Console.WriteLine($"[meta+proto {signKey}] code={r.Code} len={body.Length} {body[..Math.Min(body.Length, 150)]}");
        }
    }
    catch (Exception ex) { Console.WriteLine($"[meta+proto {signKey}] ERR: {ex.Message[..Math.Min(ex.Message.Length, 80)]}"); }
}

// Try: only in metadata, NOT in proto header
Console.WriteLine("\n--- Only metadata, no proto header ---");
{
    var metadata = new Metadata();
    foreach (var h in headers)
        metadata.Add(h.Key, h.Value);
    metadata.Add("_dsign", sign);

    var req = new ClientRequest
    {
        RequestId = $"test-{DateTime.UtcNow.Ticks}",
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
            Console.WriteLine($"  code={r.Code} len={body.Length} {body[..Math.Min(body.Length, 150)]}");
        }
    }
    catch (Exception ex) { Console.WriteLine($"  ERR: {ex.Message[..Math.Min(ex.Message.Length, 80)]}"); }
}

Console.WriteLine("Done");
'@

$tmpDir = "$env:TEMP\grpc_test6"
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
