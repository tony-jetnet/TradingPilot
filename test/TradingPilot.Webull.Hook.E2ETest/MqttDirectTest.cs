using System.Buffers;
using System.Text;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Packets;
using MQTTnet.Protocol;

namespace TradingPilot.Webull.Hook.E2ETest;

public static class MqttDirectTest
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Webull MQTT Direct Connection Test ===");
        Console.WriteLine();

        // Broker config from mqttServer.ini + observed network connections
        (string host, int port, bool tls)[] endpoints =
        [
            ("3.229.229.186", 1883, false),  // IP from active Webull connection
            ("u-r001-push.webullfintech.com", 1883, false),
        ];
        // Try multiple credential combos
        (string? user, string? pass, string label)[] creds =
        [
            (null, null, "no-auth"),
            ("0cbeb8ac323a472cd748d6b094305438", "", "did-only"),
            ("tonychen@outlook.com", "QZUdDfA$pz66HC_", "email-pass"),
        ];

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        foreach (var (broker, port, useTls) in endpoints)
        {
            foreach (var (user, pass, label) in creds)
            {
                Console.WriteLine($"Trying {broker}:{port} creds={label}");

                try
                {
                    var factory = new MqttClientFactory();
                    using var client = factory.CreateMqttClient();

                    client.ApplicationMessageReceivedAsync += e =>
                    {
                        Console.WriteLine($"[MSG] {e.ApplicationMessage.Topic} ({e.ApplicationMessage.Payload.Length}b)");
                        return Task.CompletedTask;
                    };
                    client.ConnectedAsync += e => { Console.WriteLine($"  CONNECTED: {e.ConnectResult.ResultCode}"); return Task.CompletedTask; };
                    client.DisconnectedAsync += e => { Console.WriteLine($"  DISCONNECTED: {e.Reason} {e.Exception?.Message}"); return Task.CompletedTask; };

                    var optionsBuilder = new MqttClientOptionsBuilder()
                        .WithTcpServer(broker, port)
                        .WithClientId($"wb_desktop_{Guid.NewGuid():N}"[..32])
                        .WithProtocolVersion(MqttProtocolVersion.V311)
                        .WithKeepAlivePeriod(TimeSpan.FromSeconds(60))
                        .WithTimeout(TimeSpan.FromSeconds(5));

                    if (user != null)
                        optionsBuilder.WithCredentials(user, pass ?? "");

                    var options = optionsBuilder.Build();
                    var result = await client.ConnectAsync(options, cts.Token);
                    Console.WriteLine($"  Result: {result.ResultCode}");

                    if (result.ResultCode == MqttClientConnectResultCode.Success)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"  SUCCESS with {label}! Subscribing to #...");
                        Console.ResetColor();
                        await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("#").Build(), cts.Token);
                        Console.WriteLine("  Listening 10s for messages...");
                        await Task.Delay(10000, cts.Token);
                        await client.DisconnectAsync();
                        return 0;
                    }
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  FAILED: {ex.Message}");
                    Console.ResetColor();
                }
            }
            Console.WriteLine();
        }

        Console.WriteLine("All brokers failed.");
        return 1;
    }

    private static string? TryDecodeUtf8(byte[] data)
    {
        try
        {
            string s = Encoding.UTF8.GetString(data);
            foreach (char c in s)
            {
                if (c != '\n' && c != '\r' && c != '\t' && (c < ' ' || c > '~') && c < 128)
                    return null;
            }
            return s;
        }
        catch { return null; }
    }
}
