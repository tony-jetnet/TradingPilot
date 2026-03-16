using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using TradingPilot.Webull.Grpc;

namespace TradingPilot.Webull;

public class WebullGrpcClient : IDisposable
{
    private const string GrpcHost = "https://quotes-grpc-gw.webullfintech.com:443";
    // Secret = embedded literal + runtime-appended string from wbbasecore config
    // Read from wbgrpc.dll: fromUtf8("u556uuu7qflha9xt").append(*(.data+0x7A79A0))
    private const string SignSecret = "u556uuu7qflha9xtlabgd5dzb0wk4a4i";

    // Keys extracted from getHeadMd5Sign in wbgrpc.dll (sorted alphabetically by QMap)
    private static readonly string[] SignKeys = ["appid", "device-type", "did", "platform", "reqid", "ver"];

    private readonly GrpcChannel _channel;
    private readonly Gateway.GatewayClient _client;
    private readonly ILogger<WebullGrpcClient> _logger;
    private int _requestCounter;

    public WebullGrpcClient(ILogger<WebullGrpcClient> logger)
    {
        _logger = logger;
        _channel = GrpcChannel.ForAddress(GrpcHost);
        _client = new Gateway.GatewayClient(_channel);
    }

    public async Task<JsonElement?> RequestAsync(string authHeaderJson, string apiPath, CancellationToken ct = default)
    {
        var raw = await RequestRawAsync(authHeaderJson, apiPath, ct);
        if (raw == null) return null;

        try
        {
            return JsonDocument.Parse(raw).RootElement;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "gRPC response not JSON for {Path}: {Preview}", apiPath, raw[..Math.Min(raw.Length, 200)]);
            return null;
        }
    }

    public async Task<string?> RequestRawAsync(string authHeaderJson, string apiPath, CancellationToken ct = default)
    {
        var requestId = $"tp-{Interlocked.Increment(ref _requestCounter):D6}-{DateTime.UtcNow.Ticks}";

        // Parse auth headers and generate fresh reqid/t_time
        var authHeaders = new Dictionary<string, string>();
        using var doc = JsonDocument.Parse(authHeaderJson);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            string val = prop.Value.GetString() ?? prop.Value.ToString();
            if (!string.IsNullOrEmpty(val) && prop.Name != "content-type")
                authHeaders[prop.Name] = val;
        }
        authHeaders["reqid"] = requestId;
        authHeaders["t_time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

        // Compute sign from auth headers
        string sign = ComputeSign(authHeaders);

        // Auth headers + sign go in gRPC call metadata (HTTP/2 headers)
        var metadata = new Metadata();
        foreach (var kv in authHeaders)
            metadata.Add(kv.Key, kv.Value);
        metadata.Add("sign", sign);

        // Proto message carries path/requestId only; header map mirrors metadata
        var request = new ClientRequest
        {
            RequestId = requestId,
            Type = MsgType.Request,
            Path = apiPath,
        };

        _logger.LogDebug("gRPC StreamRequest {Id}: {Path} sign={Sign}", requestId, apiPath, sign);

        try
        {
            using var call = _client.StreamRequest(headers: metadata, cancellationToken: ct);

            await call.RequestStream.WriteAsync(request, ct);
            await call.RequestStream.CompleteAsync();

            if (await call.ResponseStream.MoveNext(ct))
            {
                var response = call.ResponseStream.Current;
                if (response.Msg != null && !response.Msg.IsEmpty)
                {
                    string body = response.Msg.ToStringUtf8();
                    _logger.LogDebug("gRPC response for {Path}: {Len}B code={Code}", apiPath, body.Length, response.Code);
                    return body;
                }
                if (response.Payload != null && !response.Payload.IsEmpty)
                {
                    string body = response.Payload.ToStringUtf8();
                    _logger.LogDebug("gRPC payload for {Path}: {Len}B code={Code}", apiPath, body.Length, response.Code);
                    return body;
                }

                _logger.LogWarning("gRPC empty response for {Path}, code={Code}", apiPath, response.Code);
            }
            else
            {
                _logger.LogWarning("gRPC no response for {Path}", apiPath);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "gRPC request failed for {Path}", apiPath);
            return null;
        }
    }

    /// <summary>
    /// Computes the MD5 sign from header values.
    /// Algorithm (from wbgrpc.dll getHeadMd5Sign):
    ///   1. Extract 6 keys from header map: appid, device-type, did, platform, reqid, ver
    ///   2. Build query string in alphabetical key order: "appid=X&amp;device-type=Y&amp;...&amp;ver=Z&amp;"
    ///   3. Append "secret={embedded_secret}{runtime_secret}"
    ///   4. Return lowercase hex MD5 of the full string
    /// </summary>
    internal static string ComputeSign(IDictionary<string, string> headers)
    {
        var sb = new StringBuilder();
        foreach (string key in SignKeys)
        {
            string val = headers.TryGetValue(key, out var v) ? v : "";
            sb.Append(key).Append('=').Append(val).Append('&');
        }
        sb.Append("secret=").Append(SignSecret);

        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public void Dispose()
    {
        _channel.Dispose();
    }
}
