$testCode = @'
using System.Text.Json;
using Grpc.Net.Client;
using TradingPilot.Webull.Grpc;

var auth = JsonDocument.Parse(File.ReadAllText(
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "WebullHook", "auth_header.json")));

using var channel = GrpcChannel.ForAddress("https://quotes-grpc-gw.webullfintech.com:443");
var client = new Gateway.GatewayClient(channel);

var headers = new SortedDictionary<string, string>();
foreach (var prop in auth.RootElement.EnumerateObject())
{
    var val = prop.Value.GetString() ?? prop.Value.ToString();
    if (!string.IsNullOrEmpty(val) && prop.Name != "content-type")
        headers[prop.Name] = val;
}

string xsv = headers.GetValueOrDefault("x-sv", "");
Console.WriteLine($"x-sv from auth: '{xsv}'");

string apiPath = "api/information/financial/index?tickerId=913254235";

// Try x-sv as the sign value
string[] signKeys = ["sign", "_dsign", "dsign"];
string[] signVals = [xsv, xsv + "sign", "sign" + xsv];

foreach (var key in signKeys)
{
    foreach (var val in signVals)
    {
        var req = new ClientRequest
        {
            RequestId = $"t-{DateTime.UtcNow.Ticks}",
            Type = MsgType.Request,
            Path = apiPath,
        };
        foreach (var h in headers) req.Header[h.Key] = h.Value;
        req.Header[key] = val;

        try
        {
            using var call = client.StreamRequest();
            await call.RequestStream.WriteAsync(req);
            await call.RequestStream.CompleteAsync();
            if (await call.ResponseStream.MoveNext(default))
            {
                var r = call.ResponseStream.Current;
                var body = r.Msg?.ToStringUtf8() ?? r.Payload?.ToStringUtf8() ?? "";
                if (body.Length > 10)
                {
                    Console.WriteLine($"*** SUCCESS *** key={key} val={val}");
                    Console.WriteLine($"  {body[..Math.Min(body.Length, 200)]}");
                    return;
                }
                Console.WriteLine($"[{key}={val}] code={r.Code} empty");
            }
        }
        catch (Exception ex)
        {
            var d = ex.Message.Contains("invalid") ? "INVALID" : ex.Message.Contains("not found") ? "NOT_FOUND" : "ERR";
            Console.WriteLine($"[{key}={val}] {d}");
        }
    }
}

// Try with x-sv already in headers (it is!) and no additional sign
Console.WriteLine("\n--- Test: headers as-is (x-sv already included) ---");
{
    var req = new ClientRequest
    {
        RequestId = $"asis-{DateTime.UtcNow.Ticks}",
        Type = MsgType.Request,
        Path = apiPath,
    };
    foreach (var h in headers) req.Header[h.Key] = h.Value;
    // x-sv is already in headers from the auth capture

    try
    {
        using var call = client.StreamRequest();
        await call.RequestStream.WriteAsync(req);
        await call.RequestStream.CompleteAsync();
        if (await call.ResponseStream.MoveNext(default))
        {
            var r = call.ResponseStream.Current;
            var body = r.Msg?.ToStringUtf8() ?? r.Payload?.ToStringUtf8() ?? "";
            Console.WriteLine($"  code={r.Code} len={body.Length}: {body[..Math.Min(body.Length, 200)]}");
        }
    }
    catch (Exception ex) { Console.WriteLine($"  {ex.Message[..Math.Min(ex.Message.Length, 80)]}"); }
}

Console.WriteLine("Done");
'@

$tmpDir = "$env:TEMP\grpc_xsv"
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
