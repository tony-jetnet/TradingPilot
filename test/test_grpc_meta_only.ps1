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

// Sign formulas to try
string[] signs = [
    Md5(secret + ver + appid + dt + did),
    Md5(did + appid + dt + ver + secret),
    Md5(ver + appid + dt + did + secret),
    Md5(secret + did + appid + dt + ver),
    Md5(secret + appid + did + dt + ver),
    Md5(did + secret),
    Md5(secret + did),
    Md5(appid + secret),
    Md5(secret + appid),
    Md5(did + ver + appid + dt + secret),
    Md5(secret),
    Md5(""),
];
string[] descs = [
    "s+v+a+d+did", "did+a+d+v+s", "v+a+d+did+s", "s+did+a+d+v",
    "s+a+did+d+v", "did+s", "s+did", "a+s", "s+a",
    "did+v+a+d+s", "just_secret", "empty"
];

for (int i = 0; i < signs.Length; i++)
{
    // Sign ONLY in gRPC metadata, auth ONLY in proto header
    var metadata = new Metadata();
    metadata.Add("sign", signs[i]);

    var req = new ClientRequest
    {
        RequestId = $"t{i}-{DateTime.UtcNow.Ticks}",
        Type = MsgType.Request,
        Path = apiPath,
    };
    foreach (var h in authHeaders) req.Header[h.Key] = h.Value;
    // NO sign in proto header

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
                Console.WriteLine($"[{i}] *** SUCCESS *** formula={descs[i]} sign={signs[i]}");
                Console.WriteLine($"  {body[..Math.Min(body.Length, 300)]}");
                return;
            }
            Console.Write($"[{i}]c{r.Code} ");
        }
    }
    catch (Exception ex)
    {
        var d = ex.Message.Contains("sign invalid") ? "INV" :
                ex.Message.Contains("sign not found") ? "NF" :
                ex.Message.Contains("header invalid") ? "HDR" :
                ex.Message[..Math.Min(ex.Message.Length, 30)];
        Console.Write($"[{i}]{d} ");
    }
}

Console.WriteLine("\n\nDone");
'@

$tmpDir = "$env:TEMP\grpc_meta"
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
