$testCode = @'
using System.Text.Json;
using Grpc.Net.Client;
using TradingPilot.Webull.Grpc;

var auth = JsonDocument.Parse(File.ReadAllText(
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "WebullHook", "auth_header.json")));

using var channel = GrpcChannel.ForAddress("https://quotes-grpc-gw.webullfintech.com:443");
var client = new Gateway.GatewayClient(channel);

string[] apiPaths = [
    "api/bgw/quote/realtime?tickerIds=913254235&includeSecu=1&delay=0&more=1",
    "api/information/financial/index?tickerId=913254235",
    "api/securities/stock/913254235/info",
    "api/securities/analyst/rating/913254235",
    "api/securities/stock/913254235/shortInterest",
    "api/securities/stock/913254235/profile",
    "api/securities/market/top/gainers?regionId=6&count=5",
    "api/securities/market/top/active?regionId=6&count=5",
    "api/quote/option/913254235/expirationDate",
    "api/information/calendar/earnings?regionId=6&pageSize=5",
    "api/information/news/tickerNewses/v9?tickerId=913254235&currentNewsId=0&pageSize=5",
];

int ok = 0, fail = 0;
foreach (var apiPath in apiPaths)
{
    var req = new ClientRequest
    {
        RequestId = $"test-{DateTime.UtcNow.Ticks}",
        Type = MsgType.Request,
        Path = apiPath,
    };
    foreach (var prop in auth.RootElement.EnumerateObject())
    {
        var val = prop.Value.GetString() ?? prop.Value.ToString();
        if (!string.IsNullOrEmpty(val) && prop.Name != "content-type")
            req.Header[prop.Name] = val;
    }

    try
    {
        using var call = client.StreamRequest();
        await call.RequestStream.WriteAsync(req);
        await call.RequestStream.CompleteAsync();

        if (await call.ResponseStream.MoveNext(default))
        {
            var resp = call.ResponseStream.Current;
            var msg = resp.Msg?.ToStringUtf8() ?? "";
            var payload = resp.Payload?.ToStringUtf8() ?? "";
            var body = msg.Length > 0 ? msg : payload;
            var preview = body.Length > 150 ? body[..150] : body;
            Console.WriteLine($"[{body.Length,6}B code={resp.Code}] {apiPath}");
            if (body.Length > 0) Console.WriteLine($"  {preview}");
            ok++;
        }
        else
        {
            Console.WriteLine($"[NO RSP] {apiPath}");
            fail++;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[  ERR ] {apiPath}: {ex.Message[..Math.Min(ex.Message.Length, 120)]}");
        fail++;
    }
}
Console.WriteLine($"\n=== {ok} OK, {fail} failed ===");
'@

$tmpDir = "$env:TEMP\grpc_test3"
if (Test-Path $tmpDir) { Remove-Item $tmpDir -Recurse -Force }
New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null
Copy-Item 'D:\Third-Parties\TradingPilot\src\TradingPilot.Application\Protos\gateway.proto' "$tmpDir\gateway.proto"

@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup><Protobuf Include="gateway.proto" GrpcServices="Client" /></ItemGroup>
  <ItemGroup>
    <PackageReference Include="Google.Protobuf" Version="3.*" />
    <PackageReference Include="Grpc.Net.Client" Version="2.*" />
    <PackageReference Include="Grpc.Tools" Version="2.*" PrivateAssets="All" />
  </ItemGroup>
</Project>
"@ | Set-Content "$tmpDir\GrpcTest.csproj"
$testCode | Set-Content "$tmpDir\Program.cs"

[console]::WriteLine("Building and running gRPC streaming test...")
Push-Location $tmpDir
& dotnet run 2>&1 | ForEach-Object { [console]::WriteLine($_) }
Pop-Location
Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
