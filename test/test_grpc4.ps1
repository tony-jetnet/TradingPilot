$testCode = @'
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

// Build request
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

// Test 1: Headers in protobuf header map (already done, gets "sign not found")
Console.WriteLine("Test 1: headers in proto header map");
try
{
    using var call1 = client.StreamRequest();
    await call1.RequestStream.WriteAsync(req);
    await call1.RequestStream.CompleteAsync();
    if (await call1.ResponseStream.MoveNext(default))
    {
        var r = call1.ResponseStream.Current;
        Console.WriteLine($"  code={r.Code} msg={r.Msg?.ToStringUtf8()?[..Math.Min(r.Msg?.Length ?? 0, 100)]}");
    }
}
catch (Exception ex) { Console.WriteLine($"  ERR: {ex.Message[..Math.Min(ex.Message.Length, 100)]}"); }

// Test 2: Also add auth as gRPC metadata
Console.WriteLine("\nTest 2: auth in gRPC metadata");
try
{
    var metadata = new Metadata();
    foreach (var prop in auth.RootElement.EnumerateObject())
    {
        var val = prop.Value.GetString() ?? prop.Value.ToString();
        if (!string.IsNullOrEmpty(val) && prop.Name != "content-type")
            metadata.Add(prop.Name, val);
    }

    using var call2 = client.StreamRequest(metadata);
    await call2.RequestStream.WriteAsync(req);
    await call2.RequestStream.CompleteAsync();
    if (await call2.ResponseStream.MoveNext(default))
    {
        var r = call2.ResponseStream.Current;
        var body = r.Msg?.ToStringUtf8() ?? r.Payload?.ToStringUtf8() ?? "";
        Console.WriteLine($"  code={r.Code} len={body.Length}");
        Console.WriteLine($"  {body[..Math.Min(body.Length, 200)]}");
    }
}
catch (Exception ex) { Console.WriteLine($"  ERR: {ex.Message[..Math.Min(ex.Message.Length, 100)]}"); }

// Test 3: Only metadata, no proto header map
Console.WriteLine("\nTest 3: auth ONLY in metadata, empty proto header");
try
{
    var metadata3 = new Metadata();
    foreach (var prop in auth.RootElement.EnumerateObject())
    {
        var val = prop.Value.GetString() ?? prop.Value.ToString();
        if (!string.IsNullOrEmpty(val) && prop.Name != "content-type")
            metadata3.Add(prop.Name, val);
    }

    var req3 = new ClientRequest
    {
        RequestId = $"test3-{DateTime.UtcNow.Ticks}",
        Type = MsgType.Request,
        Path = apiPath,
    };
    // No headers in proto

    using var call3 = client.StreamRequest(metadata3);
    await call3.RequestStream.WriteAsync(req3);
    await call3.RequestStream.CompleteAsync();
    if (await call3.ResponseStream.MoveNext(default))
    {
        var r = call3.ResponseStream.Current;
        var body = r.Msg?.ToStringUtf8() ?? r.Payload?.ToStringUtf8() ?? "";
        Console.WriteLine($"  code={r.Code} len={body.Length}");
        Console.WriteLine($"  {body[..Math.Min(body.Length, 200)]}");
    }
}
catch (Exception ex) { Console.WriteLine($"  ERR: {ex.Message[..Math.Min(ex.Message.Length, 100)]}"); }

Console.WriteLine("\nDone");
'@

$tmpDir = "$env:TEMP\grpc_test4"
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

Push-Location $tmpDir
& dotnet run 2>&1 | ForEach-Object { [console]::WriteLine($_) }
Pop-Location
Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
