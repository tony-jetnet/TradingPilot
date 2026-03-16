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

string ver = authHeaders.GetValueOrDefault("ver", "");
string appid = authHeaders.GetValueOrDefault("appid", "");
string dt = authHeaders.GetValueOrDefault("device-type", "");
string did = authHeaders.GetValueOrDefault("did", "");

string[] signFormulas = [
    Md5(secret + ver + appid + dt + did),
    Md5(did + appid + dt + ver + secret),
    Md5(secret + did),
    Md5(did + secret),
];

foreach (var sign in signFormulas)
{
    // ALL auth + sign in gRPC metadata, empty proto header
    var metadata = new Metadata();
    foreach (var h in authHeaders)
    {
        // gRPC metadata keys must be lowercase
        metadata.Add(h.Key.ToLowerInvariant(), h.Value);
    }
    metadata.Add("sign", sign);

    var req = new ClientRequest
    {
        RequestId = $"t-{DateTime.UtcNow.Ticks}",
        Type = MsgType.Request,
        Path = apiPath,
    };
    // Empty proto header

    try
    {
        using var call = client.StreamRequest(metadata);
        await call.RequestStream.WriteAsync(req);
        await call.RequestStream.CompleteAsync();
        if (await call.ResponseStream.MoveNext(default))
        {
            var r = call.ResponseStream.Current;
            var body = r.Msg?.ToStringUtf8() ?? r.Payload?.ToStringUtf8() ?? "";
            Console.WriteLine($"[sign={sign[..8]}] code={r.Code} len={body.Length}");
            if (body.Length > 10)
            {
                Console.WriteLine($"*** SUCCESS *** {body[..Math.Min(body.Length, 300)]}");
                return;
            }
        }
    }
    catch (Exception ex)
    {
        var d = ex.Message.Contains("sign invalid") ? "SIGN_INVALID" :
                ex.Message.Contains("sign not found") ? "SIGN_NOT_FOUND" :
                ex.Message.Contains("header invalid") ? "HEADER_INVALID" :
                ex.Message[..Math.Min(ex.Message.Length, 60)];
        Console.WriteLine($"[sign={sign[..8]}] {d}");
    }
}

Console.WriteLine("Done");
'@

$tmpDir = "$env:TEMP\grpc_am"
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
