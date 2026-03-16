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
            ("u-r001-push.webullfintech.com", 1883, false),
            ("push.webullfintech.com", 1883, false),
            ("3.229.229.186", 1883, false),  // IP from active Webull connection
            ("u-r001-push.webullfintech.com", 8883, true),  // TLS variant
            ("push.webullfintech.com", 8883, true),
        ];
        string username = "tonychen@outlook.com";
        string password = "QZUdDfA$pz66HC_";

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        foreach (var (broker, port, useTls) in endpoints)
        {
            Console.WriteLine($"Trying broker: {broker}:{port} (TLS={useTls})");
            Console.WriteLine($"  Username: {username}");
            Console.WriteLine();

            try
            {
                var factory = new MqttClientFactory();
                using var client = factory.CreateMqttClient();

                // Log all events
                client.ApplicationMessageReceivedAsync += e =>
                {
                    string topic = e.ApplicationMessage.Topic;
                    byte[] payload = e.ApplicationMessage.Payload.ToArray();
                    string? text = TryDecodeUtf8(payload);

                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.Write($"[MSG] ");
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Write($"{topic} ");
                    Console.ForegroundColor = ConsoleColor.Gray;
                    Console.Write($"({payload.Length} bytes, QoS={e.ApplicationMessage.QualityOfServiceLevel})");
                    Console.ResetColor();
                    Console.WriteLine();

                    if (text != null)
                    {
                        string display = text.Length > 300 ? text[..300] + "..." : text;
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"  {display}");
                        Console.ResetColor();
                    }
                    else if (payload.Length > 0)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine($"  [hex] {Convert.ToHexString(payload.AsSpan(0, Math.Min(payload.Length, 100)))}");
                        Console.ResetColor();
                    }
                    return Task.CompletedTask;
                };

                client.DisconnectedAsync += e =>
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[DISCONNECTED] Reason: {e.Reason}, Exception: {e.Exception?.Message}");
                    Console.ResetColor();
                    return Task.CompletedTask;
                };

                client.ConnectedAsync += e =>
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[CONNECTED] Result: {e.ConnectResult.ResultCode}");
                    Console.ResetColor();
                    return Task.CompletedTask;
                };

                // Try MQTT v3.1.1 first (most common for IoT/trading)
                foreach (var protocol in new[] { MqttProtocolVersion.V311, MqttProtocolVersion.V500 })
                {
                    Console.Write($"  Connecting with MQTT {protocol}... ");

                    var optionsBuilder = new MqttClientOptionsBuilder()
                        .WithTcpServer(broker, port)
                        .WithCredentials(username, password)
                        .WithClientId($"wb_desktop_{Guid.NewGuid():N}"[..32])
                        .WithProtocolVersion(protocol)
                        .WithKeepAlivePeriod(TimeSpan.FromSeconds(60))
                        .WithTimeout(TimeSpan.FromSeconds(8));

                    if (useTls)
                    {
                        optionsBuilder.WithTlsOptions(o =>
                        {
                            o.WithAllowUntrustedCertificates();
                            o.WithIgnoreCertificateChainErrors();
                        });
                    }

                    var options = optionsBuilder.Build();

                    try
                    {
                        var result = await client.ConnectAsync(options, cts.Token);
                        Console.WriteLine($"OK! ResultCode={result.ResultCode}");

                        if (result.ResultCode == MqttClientConnectResultCode.Success)
                        {
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine($"\n  CONNECTED to {broker}:{port} via {protocol}!");
                            Console.ResetColor();

                            // Subscribe to wildcard to discover all topics
                            Console.WriteLine("  Subscribing to '#' (all topics)...");
                            var subResult = await client.SubscribeAsync(
                                new MqttClientSubscribeOptionsBuilder()
                                    .WithTopicFilter("#", MqttQualityOfServiceLevel.AtMostOnce)
                                    .Build(),
                                cts.Token);

                            foreach (var item in subResult.Items)
                            {
                                Console.WriteLine($"  Sub result: {item.TopicFilter.Topic} -> {item.ResultCode}");
                            }

                            Console.WriteLine();
                            Console.WriteLine("Listening for messages... (Ctrl+C to stop)");
                            Console.WriteLine(new string('-', 80));

                            try
                            {
                                await Task.Delay(Timeout.Infinite, cts.Token);
                            }
                            catch (OperationCanceledException) { }

                            await client.DisconnectAsync();
                            Console.WriteLine("\nDisconnected cleanly.");
                            return 0;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"FAILED - {ex.Message}");
                        Console.ResetColor();
                    }
                }

                Console.WriteLine();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  Error: {ex.Message}");
                Console.ResetColor();
                Console.WriteLine();
            }
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
