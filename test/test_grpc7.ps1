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

var headers = new Dictionary<string, string>();
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

// The function is getHeadMd5Sign(QMap<QString,QString> headers)
// QMap sorts by key. MD5 of what? Try:
// 1. sorted key+value concatenated
// 2. sorted values only
// 3. sorted "key=value" joined by &
// 4. MD5 of the path
// 5. MD5 of did+access_token
// 6. MD5 of path+did
// 7. just the did md5

var sortedHeaders = headers.OrderBy(h => h.Key).ToList();

string[] attempts = [
    // key+value concat
    string.Join("", sortedHeaders.Select(h => h.Key + h.Value)),
    // values only
    string.Join("", sortedHeaders.Select(h => h.Value)),
    // key=value&...
    string.Join("&", sortedHeaders.Select(h => $"{h.Key}={h.Value}")),
    // path only
    apiPath,
    // did + access_token
    headers.GetValueOrDefault("did", "") + headers.GetValueOrDefault("access_token", ""),
    // access_token + did + path
    headers.GetValueOrDefault("access_token", "") + headers.GetValueOrDefault("did", "") + apiPath,
    // path + did
    apiPath + headers.GetValueOrDefault("did", ""),
    // Empty string
    "",
    // did only
    headers.GetValueOrDefault("did", ""),
    // t_time + did
    headers.GetValueOrDefault("t_time", "") + headers.GetValueOrDefault("did", ""),
    // access_token only
    headers.GetValueOrDefault("access_token", ""),
];

for (int i = 0; i < attempts.Length; i++)
{
    var sign = Md5(attempts[i]);
    var req = new ClientRequest
    {
        RequestId = $"test-{i}-{DateTime.UtcNow.Ticks}",
        Type = MsgType.Request,
        Path = apiPath,
    };
    foreach (var h in headers)
        req.Header[h.Key] = h.Value;
    req.Header["sign"] = sign;

    try
    {
        using var call = client.StreamRequest();
        await call.RequestStream.WriteAsync(req);
        await call.RequestStream.CompleteAsync();
        if (await call.ResponseStream.MoveNext(default))
        {
            var r = call.ResponseStream.Current;
            var body = r.Msg?.ToStringUtf8() ?? r.Payload?.ToStringUtf8() ?? "";
            Console.WriteLine($"[{i}] code={r.Code} len={body.Length} sign={sign[..8]}... input_preview={attempts[i][..Math.Min(attempts[i].Length, 40)]}");
            if (body.Length > 10)
            {
                Console.WriteLine($"    DATA: {body[..Math.Min(body.Length, 200)]}");
                Console.WriteLine("    *** SUCCESS! ***");
                return;
            }
        }
    }
    catch (Exception ex)
    {
        var msg = ex.Message;
        if (msg.Contains("sign invalid"))
            Console.Write($"[{i}] sign invalid ");
        else
            Console.Write($"[{i}] {msg[..Math.Min(msg.Length, 50)]} ");
        Console.WriteLine($"(input_preview={attempts[i][..Math.Min(attempts[i].Length, 30)]})");
    }
}

Console.WriteLine("Done - no valid sign found");
'@

$tmpDir = "$env:TEMP\grpc_test7"
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
