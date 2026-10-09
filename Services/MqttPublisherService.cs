using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;


using EdgeGateway.Models;

namespace EdgeGateway.Services;

// Thin MQTTnet wrapper for the twin-publish path. Adapted from the
// Omniverse-side MqttService pattern already used elsewhere in the stack —
// publish-only here, since the gateway doesn't need to subscribe to
// anything on this connection. Every publish is QoS 0 (at most once), per
// slide 10's "best effort, no buffering" rule for this path.
public class MqttPublisherService : IMqttPublisher
{
    private readonly MqttOptions _options;
    private readonly string _clientId;
    private readonly MqttFactory _factory = new();
    private IMqttClient? _client;

    public bool IsConnected => _client?.IsConnected ?? false;

    public MqttPublisherService(MqttOptions options, string? clientId = null)
    {
        _options = options;
        _clientId = clientId ?? $"{Environment.MachineName}_TwinPublisher";
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _client?.Dispose();
        _client = _factory.CreateMqttClient();

        _client.DisconnectedAsync += _ =>
        {
            Console.WriteLine("[mqtt] disconnected from broker — will reconnect on next publish");
            return Task.CompletedTask;
        };

        var clientOptions = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.BrokerAddress, _options.BrokerPort)
            .WithClientId(_clientId)
            .WithCleanSession()
            .Build();

        await _client.ConnectAsync(clientOptions);
        Console.WriteLine($"[mqtt] connected to {_options.BrokerAddress}:{_options.BrokerPort}");
    }

    public async Task PublishAsync(string topic, string payload, CancellationToken ct = default)
    {
        if (_client is null || !_client.IsConnected)
        {
            try
            {
                await ConnectAsync(ct);
            }
            catch (Exception ex)
            {
                // Best effort: this message is simply lost, per slide 10 — the
                // next publish on this topic replaces it. No retry, no outbox.
                Console.WriteLine($"[mqtt] publish skipped, broker unreachable: {ex.Message}");
                return;
            }
        }

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce) // QoS 0
            .Build();

        try
        {
            await _client!.PublishAsync(message);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[mqtt] publish failed for {topic}: {ex.Message} — dropped, not retried");
        }
    }

    public async Task DisconnectAsync()
    {
        if (_client is { IsConnected: true })
            await _client.DisconnectAsync();
    }
}
