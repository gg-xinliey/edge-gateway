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
//
// One client for the life of this object, and only one connect at a time.
// (It used to dispose and recreate the client on every connect; under load
// two connects overlapped and one destroyed the client the other was using:
// "Cannot access a disposed object. Object name: 'AsyncLockWaiter'".)
public class MqttPublisherService : IMqttPublisher
{
    private readonly MqttOptions _options;
    private readonly string _clientId;
    private readonly IMqttClient _client;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly ThrottledLog _problems = new(TimeSpan.FromSeconds(60));
    private volatile bool _disconnecting;

    public bool IsConnected => _client.IsConnected;

    public MqttPublisherService(MqttOptions options, string? clientId = null)
    {
        _options = options;
        _clientId = clientId ?? $"{Environment.MachineName}_{options.ClientIdSuffix}_TwinPublisher";
        _client = new MqttFactory().CreateMqttClient();
        _client.DisconnectedAsync += _ =>
        {
            // Our own DisconnectAsync (gateway stopping / restarting) is not a problem.
            if (!_disconnecting)
                _problems.Write("disconnected", "[mqtt] twin connection dropped — will reconnect");
            return Task.CompletedTask;
        };
    }

    // A failed connect for the log: a refused login says so, it is not the network.
    public static string Why(Exception ex) =>
        ex is MQTTnet.Adapter.MqttConnectingFailedException { ResultCode: MqttClientConnectResultCode.BadUserNameOrPassword or MqttClientConnectResultCode.NotAuthorized }
            ? $"broker refused the login (check username / password): {ex.Message}"
            : ex.Message;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _connectGate.WaitAsync(ct);
        try
        {
            await ConnectLockedAsync(ct);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    // Caller holds _connectGate.
    private async Task ConnectLockedAsync(CancellationToken ct)
    {
        if (_client.IsConnected)
            return;

        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.BrokerAddress, _options.BrokerPort)
            .WithClientId(_clientId)
            .WithCleanSession()
            .WithTimeout(TimeSpan.FromSeconds(5));
        if (!string.IsNullOrEmpty(_options.Username))
            builder = builder.WithCredentials(_options.Username, _options.Password);

        await _client.ConnectAsync(builder.Build(), ct);
        Console.WriteLine($"[mqtt] connected to {_options.BrokerAddress}:{_options.BrokerPort}");
    }

    public async Task PublishAsync(string topic, string payload, CancellationToken ct = default)
    {
        if (!_client.IsConnected)
        {
            // Someone else is connecting: drop this one instead of waiting behind them.
            if (!await _connectGate.WaitAsync(0, ct))
                return;
            try
            {
                await ConnectLockedAsync(ct);
            }
            catch (Exception ex)
            {
                // Best effort: this message is simply lost, per slide 10 — the
                // next publish on this topic replaces it. No retry, no outbox.
                _problems.Write("unreachable", $"[mqtt] publish skipped, broker unreachable: {Why(ex)}");
                return;
            }
            finally
            {
                _connectGate.Release();
            }
        }

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce) // QoS 0
            .Build();

        try
        {
            await _client.PublishAsync(message, ct);
        }
        catch (Exception ex)
        {
            _problems.Write("publish", $"[mqtt] publish failed for {topic}: {ex.Message} — dropped, not retried");
        }
    }

    public async Task DisconnectAsync()
    {
        _disconnecting = true;
        if (_client.IsConnected)
            await _client.DisconnectAsync();
    }
}
