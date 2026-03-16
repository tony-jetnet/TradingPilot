$testCode = @'
using System.Text.Json;
using Google.Protobuf;
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
    "api/securities/financial/earnings/913254235",
    "api/securities/stock/913254235/shortInterest",
    "api/securities/stock/913254235/insiderActivity",
    "api/securities/stock/913254235/profile",
    "api/securities/market/top/gainers?regionId=6&count=5",
    "api/securities/market/top/active?regionId=6&count=5",
    "api/quote/option/913254235/expirationDate",
    "api/information/calendar/earnings?regionId=6&pageSize=5",
    "api/screener/ng/query/rules",
];

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
        var resp = await client.RequestAsync(req);
        var body = resp.Msg?.ToStringUtf8() ?? "";
        var preview = body.Length > 150 ? body[..150] : body;
        Console.WriteLine($"[{body.Length,6}B] {apiPath}");
        if (body.Length > 0) Console.WriteLine($"         {preview}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[  ERR ] {apiPath}: {ex.Message[..Math.Min(ex.Message.Length, 100)]}");
    }
}
'@

$tmpDir = "$env:TEMP\grpc_test2"
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
  <ItemGroup>
    <Protobuf Include="gateway.proto" GrpcServices="Client" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Google.Protobuf" Version="3.*" />
    <PackageReference Include="Grpc.Net.Client" Version="2.*" />
    <PackageReference Include="Grpc.Tools" Version="2.*" PrivateAssets="All" />
  </ItemGroup>
</Project>
"@ | Set-Content "$tmpDir\GrpcTest.csproj"

$testCode | Set-Content "$tmpDir\Program.cs"

[console]::WriteLine("Building and running gRPC test...")
Push-Location $tmpDir
& dotnet run 2>&1 | ForEach-Object { [console]::WriteLine($_) }
Pop-Location
Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
[console]::WriteLine("Done")
